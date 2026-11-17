using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Chirpdesk.CommandBar;
using Chirpdesk.Core.CommandBar;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Theming;
using Chirpdesk.Core.Time;
using Chirpdesk.Definitions;
using Chirpdesk.Diagnostics;
using Chirpdesk.Hosting;
using Chirpdesk.Logging;
using Chirpdesk.Notifications;
using Chirpdesk.Theming;
using Hardcodet.Wpf.TaskbarNotification;

namespace Chirpdesk;

public partial class App : Application
{
    /// <summary>The phase-0 gate service. Phase 1 replaces this with shipped definitions.</summary>
    private static readonly ServiceInstance WhatsAppSeed = new()
    {
        InstanceId = "whatsapp",
        Name = "WhatsApp",
        Url = "https://web.whatsapp.com/",
        WarmAtBoot = true,
    };

    private FileLog? _log;
    private SingleInstance? _singleInstance;
    private HostWindow? _host;
    private TaskbarIcon? _tray;
    private ServiceRegistry? _registry;
    private ServiceCatalog? _catalog;
    private CommandBarView? _bar;
    private BarState? _barState;
    private ModifierWatcher? _barWatcher;
    private IReadOnlyList<SearchCandidate> _searchCandidates = Array.Empty<SearchCandidate>();
    private bool _quitting;
    private ThemeMode _themeMode = ThemeMode.System;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = StartupOptions.Parse(e.Args);
        _log = new FileLog(AppPaths.LogsRoot, "chirpdesk.log");
        _registry = new ServiceRegistry(_log);
        _catalog = PluginCatalog.Load(_log);

        // Windows starts this process to answer a toast click that has left the screen.
        // It is not the app: no tray, no webviews, one call, then exit.
        if (ToastActivation.IsServerMode(e.Args))
        {
            RunActivationServer();
            return;
        }

        _log.Info($"starting with {e.Args.Length} argument(s), pid {Environment.ProcessId}");

        if (!SingleInstance.TryAcquire(out _singleInstance))
        {
            // A diagnostic run must never be mistaken for a successful one: it cannot take
            // the mutex, so it has nothing to test. Exit with a distinct code instead of 0.
            if (options.SelfTest || options.TestToast)
            {
                _log.Warn("cannot run diagnostics while another instance is running");
                Shutdown(3);
                return;
            }

            // Reached by a toast click and by a second shortcut launch: ask the resident
            // instance to surface, then leave. Logging the outcome is the difference between
            // "the click did nothing" and "the click was delivered".
            var signalled = SingleInstance.SignalShow();
            _log.Info($"another instance is already running; show requested={signalled}");
            Shutdown(signalled ? 0 : 1);
            return;
        }

        _singleInstance!.ShowRequested += OnShowRequested;

        RegisterAppIdentity();
        RegisterToastActivation();

        // Both diagnostic modes need the real window, because the thing under test is a hidden
        // WebView2 in it. Neither needs the tray icon.
        _host = new HostWindow();
        _host.Show();
        _host.KeyDown += OnHostKeyDown;
        _host.Closing += OnHostClosing;

        if (options.SelfTest)
        {
            _ = RunSelfTestAsync(options);
            return;
        }

        if (options.TestToast)
        {
            _ = RunToastCheckAsync();
            return;
        }

        _tray = TrayIconFactory.Create(OpenHost, HideHost, Quit);
        _host.Attach(_tray);

        _bar = new CommandBarView();
        _host.SetCommandBar(_bar);
        _barWatcher = new ModifierWatcher(Dispatcher);
        _barWatcher.ControlReleased += OnControlReleased;
        _host.PreviewKeyDown += OnHostPreviewKeyDown;
        _host.PreviewTextInput += OnHostPreviewTextInput;

        InitializeTheme();

        _log.Info("started hidden");

        if (options.WarmAll)
        {
            _ = WarmAllAsync(show: !options.Hidden);
        }
        else if (options.OpenService is not null)
        {
            _ = OpenServiceAsync(options.OpenService, show: !options.Hidden, probe: options.NotifyTest);
        }

        if (options.QuitAfter is { } delay)
        {
            _ = QuitAfterAsync(delay);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ToastActivation.RevokeClassObject();
        _barWatcher?.Detach();
        _tray?.Dispose();

        try
        {
            _registry?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _log?.Warn($"service dispose on exit: {ex.Message}");
        }

        _singleInstance?.Dispose();
        _log?.Info("exited");
        base.OnExit(e);
    }

    private async Task RunSelfTestAsync(StartupOptions options)
    {
        int exitCode;
        try
        {
            exitCode = await SelfTest.RunAsync(options, _host!, _log!);
        }
        catch (Exception ex)
        {
            _log?.Error("self-test crashed", ex);
            exitCode = 1;
        }

        Terminate(exitCode);
    }

    /// <summary>Proves the toast identity alone, with no webview involved.</summary>
    private async Task RunToastCheckAsync()
    {
        var toasts = new ToastService(_log!);
        toasts.Show(new ToastMessage("Chirpdesk", "Toast identity check.", null, "toast-check"));
        ConsoleBridge.Write($"toast available: {toasts.IsAvailable}");

        // Give the shell a moment to actually render it before the process goes away.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Terminate(0);
    }

    /// <summary>
    /// Phase 0's one show path for a service: create it, put it on screen and let the user sign
    /// in. Everything else about it (warm, parked, notifying) is the normal runtime.
    /// </summary>
    private async Task OpenServiceAsync(string requestedId, bool show, bool probe)
    {
        try
        {
            var store = new SettingsStore(AppPaths.SettingsFile, _log!);
            var settings = store.Load();
            var instance = ResolveInstance(settings, requestedId, store);

            if (instance is null)
            {
                _log?.Warn($"no service matches '{requestedId}'; nothing to open");
                return;
            }

            await OpenInstanceAsync(instance, show, probe);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidSettingsException)
        {
            _log?.Error($"could not open '{requestedId}'", ex);
        }
    }

    /// <summary>Creates (or reuses) the runtime for one instance and optionally shows it.</summary>
    private async Task OpenInstanceAsync(ServiceInstance instance, bool show, bool probe)
    {
        var resolved = _catalog?.Resolve(instance);
        if (resolved is null)
        {
            _log?.Warn($"'{instance.InstanceId}' has no url and no known definition; not opening it");
            return;
        }

        // The runtime consumes resolved values: definition defaults are already filled in.
        var effective = new ServiceInstance
        {
            InstanceId = resolved.InstanceId,
            Name = resolved.Name,
            Url = resolved.Url,
            UnreadRegex = resolved.UnreadRegex,
            NotificationMode = resolved.NotificationMode,
            PageDarkMode = resolved.PageDarkMode,
            ProfileId = instance.ProfileId ?? resolved.InstanceId,
        };

        var dark = ThemeDecision.IsDark(_themeMode, SystemTheme.UsesLight());

        var service = await ServiceRuntime.CreateAsync(
            _host!,
            effective,
            _log!,
            SystemClock.Instance,
            new ToastService(_log!, id => Dispatcher.Invoke(() => ShowHostFor(id))),
            DateTimeOffset.UtcNow,
            dark);

        service.ShortcutRequested += OnServiceShortcutRequested;

        await service.ApplyThemeAsync(dark);
        await _registry!.AddAsync(service);
        _registry.Activate(service.InstanceId);

        if (show)
        {
            ShowHost();
        }

        if (probe)
        {
            var raised = await service.RaiseProbeNotificationAsync(TimeSpan.FromSeconds(30));
            _log?.Info($"probe notification issued for '{instance.InstanceId}': {raised}");
        }

        _log?.Info($"opened '{instance.InstanceId}'{(show ? " for the user" : " parked")}");
    }

    /// <summary>Warms every configured service, one after another, so Ctrl+Tab has something to cycle.</summary>
    private async Task WarmAllAsync(bool show)
    {
        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            foreach (var instance in settings.Services.OrderBy(s => s.Order))
            {
                await OpenInstanceAsync(instance, show: false, probe: false);
            }

            if (show)
            {
                ShowHost();
            }

            _log?.Info($"warmed {settings.Services.Count} service(s)");
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or InvalidOperationException)
        {
            _log?.Error("could not warm all services", ex);
        }
    }

    /// <summary>
    /// The one entry point for every user request that names a service: warm switch, or cold
    /// start from the catalog. A toast click, a Command Bar commit and the tray all land here.
    /// </summary>
    private async Task<bool> ActivateServiceAsync(string instanceId)
    {
        if (_registry?.Activate(instanceId) == true)
        {
            _log?.Info($"activation for '{instanceId}'");
            ShowHost();
            return true;
        }

        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            var instance = settings.Services.FirstOrDefault(
                s => string.Equals(s.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

            if (instance is null)
            {
                _log?.Warn($"activation for unknown service '{instanceId}'");
                return false;
            }

            _log?.Info($"cold start for '{instanceId}'");
            await OpenInstanceAsync(instance, show: true, probe: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidSettingsException)
        {
            _log?.Error($"could not activate '{instanceId}'", ex);
            return false;
        }
    }

    private static ServiceInstance? ResolveInstance(AppSettings settings, string requestedId, SettingsStore store)
    {
        var match = string.IsNullOrEmpty(requestedId)
            ? settings.Services.OrderBy(s => s.Order).FirstOrDefault()
            : settings.Services.FirstOrDefault(s => string.Equals(s.InstanceId, requestedId, StringComparison.OrdinalIgnoreCase));

        if (match is not null || (!string.IsNullOrEmpty(requestedId) && !requestedId.Equals("whatsapp", StringComparison.OrdinalIgnoreCase)))
        {
            return match;
        }

        // The phase-0 gate needs exactly one real service; create it once, on request.
        settings.Services.Add(WhatsAppSeed);
        store.Save(settings);
        return WhatsAppSeed;
    }

    /// <summary>Raised by a second launch (a toast click or the shortcut) on the listener thread.</summary>
    private void OnShowRequested(object? sender, EventArgs e)
    {
        _log?.Info("show requested by another launch");
        Dispatcher.Invoke(ShowHost);
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (_barState is not null)
        {
            return;
        }

        // Ctrl+P opens the same bar directly in search mode; Ctrl+Shift+P belongs to the page.
        if (e.Key == Key.P
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenSearchBar();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideHost();
            return;
        }

        // Ctrl+Tab opens cycle mode on the next warm service; releasing Ctrl commits. The
        // always-available version needs the global hotkey and the overlay, which is the next
        // slice; this one proves the registry and bar path.
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            var direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
            OpenCommandBar(direction);
        }
    }

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_barState is null)
        {
            return;
        }

        if (e.Key == Key.P
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.Control)
        {
            e.Handled = true;

            if (_barState.Mode == BarMode.Cycle)
            {
                SwitchToSearch();
            }

            return;
        }

        // Ctrl+P leaves Ctrl down; support typing the query until it is released. Once released,
        // TextInput handles the rest.
        if (_barState.Mode == BarMode.Search
            && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && TryMapText(e.Key, out var typed))
        {
            e.Handled = true;
            _barState.Append(typed);
            UpdateSearchResults(keepSelected: null);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseCommandBar(restore: true);
                return;

            case Key.Tab:
                e.Handled = true;
                if (_barState.Move((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1))
                {
                    RenderCommandBar();
                }

                return;

            case Key.Enter when _barState.Mode == BarMode.Search:
                e.Handled = true;
                CommitSearchSelection();
                return;

            case Key.Back when _barState.Mode == BarMode.Search:
                e.Handled = true;
                _barState.Backspace();
                UpdateSearchResults(keepSelected: null);
                return;
        }
    }

    private static bool TryMapText(Key key, out string text)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            text = ((char)('a' + ((int)key - (int)Key.A))).ToString();
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            text = ((char)('0' + ((int)key - (int)Key.D0))).ToString();
            return true;
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            text = ((char)('0' + ((int)key - (int)Key.NumPad0))).ToString();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private void OnHostPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_barState is not { Mode: BarMode.Search } || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        e.Handled = true;
        _barState.Append(e.Text);
        UpdateSearchResults(keepSelected: null);
    }

    /// <summary>
    /// Ctrl is up. In cycle mode that is the commit gesture; in search the bar stays open and
    /// nothing switches (ADR 0002).
    /// </summary>
    private void OnControlReleased(object? sender, EventArgs e)
    {
        if (_barState is null)
        {
            return;
        }

        var committed = _barState.ReleaseControl();

        if (committed is null)
        {
            if (_barState.IsOpen)
            {
                RenderCommandBar();
            }
            else
            {
                CloseCommandBar(restore: true);
            }

            return;
        }

        CloseCommandBar(restore: true);
        var switched = _registry?.Activate(committed) == true;
        _registry?.Active?.FocusPage();
        _log?.Info($"command bar committed '{committed}': switched={switched}");
    }

    private void OnServiceShortcutRequested(object? sender, ServiceShortcutEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_barState is not null)
            {
                return;
            }

            if (e.Search)
            {
                OpenSearchBar();
            }
            else
            {
                OpenCommandBar(e.Direction);
            }
        });
    }

    private void OpenCommandBar(int direction)
    {
        if (_host is null || _bar is null || _registry is null)
        {
            return;
        }

        var order = _registry.Order;
        if (order.Count == 0)
        {
            return;
        }

        _barState = BarState.ForCycle(order, _registry.Active?.InstanceId, direction);
        RenderCommandBar();

        // The webview is an HWND and would paint over the bar until it is collapsed.
        _registry.Active?.CollapseForCommandBar();
        _host.ShowCommandBar();
        _host.Focus();
        _barWatcher?.Start();

        _log?.Info($"command bar opened: {order.Count} warm service(s)");
    }

    private void OpenSearchBar()
    {
        if (_host is null || _bar is null || _registry is null || _barState is not null)
        {
            return;
        }

        _barState = BarState.ForSearch();
        _searchCandidates = BuildSearchCandidates();
        UpdateSearchResults(keepSelected: null);

        // Same airspace rule as cycle mode: the webview would paint over the bar.
        _registry.Active?.CollapseForCommandBar();
        _host.ShowCommandBar();
        _host.Focus();

        _log?.Info($"command bar opened in search mode: {_searchCandidates.Count} candidate(s)");
    }

    /// <summary>
    /// Ctrl+P while cycling keeps the unified bar open and switches it to search, with the
    /// highlighted tab still highlighted.
    /// </summary>
    private void SwitchToSearch()
    {
        if (_barState is null || _barState.Mode != BarMode.Cycle)
        {
            return;
        }

        var keep = _barState.Selected;
        _barWatcher?.Stop();

        _barState = BarState.ForSearch();
        _searchCandidates = BuildSearchCandidates();
        UpdateSearchResults(keepSelected: keep);

        _log?.Info("command bar switched to search mode");
    }

    private IReadOnlyList<SearchCandidate> BuildSearchCandidates()
    {
        if (_registry is null)
        {
            return Array.Empty<SearchCandidate>();
        }

        var settings = LoadSearchSettings();
        var byId = new Dictionary<string, ServiceInstance>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in settings.Services)
        {
            byId[instance.InstanceId] = instance;
        }

        var candidates = new List<SearchCandidate>();

        // Warm rows keep MRU order; cold rows follow settings order.
        foreach (var id in _registry.Order)
        {
            byId.TryGetValue(id, out var instance);
            var resolved = instance is null ? null : _catalog?.Resolve(instance);
            var aliases = resolved?.DefinitionId is { } definitionId
                ? _catalog?.Find(definitionId)?.Aliases
                : null;

            candidates.Add(new SearchCandidate(
                id,
                resolved?.Name ?? _registry.Find(id)?.Name ?? id,
                SearchKind.Warm,
                aliases,
                HostOf(resolved?.Url)));
        }

        foreach (var instance in settings.Services.OrderBy(service => service.Order))
        {
            if (_registry.IsWarm(instance.InstanceId))
            {
                continue;
            }

            var resolved = _catalog?.Resolve(instance);
            if (resolved is null)
            {
                continue;
            }

            var aliases = resolved.DefinitionId is { } definitionId
                ? _catalog?.Find(definitionId)?.Aliases
                : null;

            candidates.Add(new SearchCandidate(
                instance.InstanceId,
                resolved.Name,
                SearchKind.Cold,
                aliases,
                HostOf(resolved.Url)));
        }

        candidates.Add(new SearchCandidate("command:hide", "Hide window", SearchKind.Command, ["hide"]));
        candidates.Add(new SearchCandidate("command:reload", "Reload active tab", SearchKind.Command, ["reload"]));
        candidates.Add(new SearchCandidate("command:quit", "Quit Chirpdesk", SearchKind.Command, ["quit"]));

        return candidates;
    }

    private AppSettings LoadSearchSettings()
    {
        try
        {
            return new SettingsStore(AppPaths.SettingsFile, _log!).Load();
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log?.Warn($"search settings unavailable: {ex.Message}");
            return new AppSettings();
        }
    }

    private void UpdateSearchResults(string? keepSelected)
    {
        if (_barState is null)
        {
            return;
        }

        var ranked = SearchIndex.Rank(_barState.Query, _searchCandidates);
        _barState.ShowResults(ranked.Select(candidate => candidate.Id).ToList(), keepSelected);
        RenderCommandBar();
    }

    private void CommitSearchSelection()
    {
        if (_barState is null)
        {
            return;
        }

        var selected = _barState.Selected;
        if (selected is null)
        {
            return;
        }

        _barState.Commit();
        CloseCommandBar(restore: true);

        if (selected.StartsWith("command:", StringComparison.Ordinal))
        {
            ExecuteCommand(selected["command:".Length..]);
            return;
        }

        _ = ActivateServiceAsync(selected);
        _log?.Info($"command bar search committed '{selected}'");
    }

    private void ExecuteCommand(string command)
    {
        _log?.Info($"command bar command: {command}");

        switch (command)
        {
            case "hide":
                HideHost();
                return;

            case "reload":
                _registry?.Active?.Reload();
                return;

            case "quit":
                Quit();
                return;
        }
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    private void RenderCommandBar()
    {
        if (_bar is null || _barState is null || _host is null || _registry is null)
        {
            return;
        }

        var names = _searchCandidates
            .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);

        var rows = _barState.Entries
            .Select(id => new BarRow(
                id,
                names.TryGetValue(id, out var name) ? name : _registry.Find(id)?.Name ?? id))
            .ToList();

        _bar.Render(rows, _barState.Highlight, _barState.Mode, _barState.Query, _host.IsDark);
    }

    private void CloseCommandBar(bool restore)
    {
        _barWatcher?.Stop();
        _barState?.Cancel();
        _barState = null;

        _host?.HideCommandBar();

        if (restore)
        {
            _registry?.Active?.RestoreFromCommandBar();
        }

        _host?.UpdateLayout();
    }

    /// <summary>
    /// Alt+F4 and any other close request hides instead of destroying the window: closing it
    /// would take the webviews with it, and the product is a tray app.
    /// </summary>
    private void OnHostClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        e.Cancel = true;
        HideHost();
    }

    private async Task QuitAfterAsync(TimeSpan delay)
    {
        await Task.Delay(delay);
        Dispatcher.Invoke(Quit);
    }

    /// <summary>
    /// Reads the one theme setting and follows Windows while it is <c>system</c>. There is no
    /// colour-family picker yet on purpose: one palette, two ends, so the contrast is right
    /// before there is anything to choose between.
    /// </summary>
    private void InitializeTheme()
    {
        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            _themeMode = ThemeDecision.Parse(settings.Theme);
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _themeMode = ThemeMode.System;
        }

        SystemTheme.Start();
        SystemTheme.Changed += (_, _) =>
        {
            if (_themeMode == ThemeMode.System)
            {
                ApplyTheme();
            }
        };

        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var dark = ThemeDecision.IsDark(_themeMode, SystemTheme.UsesLight());
        _host?.SetDark(dark);

        var mode = ThemeDecision.ToStorageValue(_themeMode);
        var resolved = dark ? "dark" : "light";
        _log?.Info($"theme: {mode} -> {resolved}");

        if (_registry?.Active is { } active)
        {
            _ = active.ApplyThemeAsync(dark);
        }

        if (_host?.IsCommandBarOpen == true)
        {
            RenderCommandBar();
        }
    }

    private void ShowHost()
    {
        if (_host is null)
        {
            return;
        }

        if (_barState is not null)
        {
            CloseCommandBar(restore: true);
        }

        var wasVisible = _host.IsVisible;
        _registry?.Active?.Activate();
        var foreground = _host.ShowForUser();
        _registry?.Active?.FocusPage();

        _log?.Info($"window shown: wasVisible={wasVisible} foreground={foreground} visible={_host.IsVisible}");
    }

    /// <summary>
    /// Shows the window for the service a toast came from. Warm switches in place; cold starts
    /// it from settings instead of showing the wrong page. Fire-and-forget because the caller
    /// is a WinRT event or a COM callback, neither of which can await.
    /// </summary>
    private void ShowHostFor(string? serviceId)
    {
        var id = string.IsNullOrWhiteSpace(serviceId) ? null : Uri.UnescapeDataString(serviceId);

        if (id is null)
        {
            ShowHost();
            return;
        }

        _ = ActivateServiceAsync(id);
    }

    private void HideHost()
    {
        if (_barState is not null)
        {
            CloseCommandBar(restore: true);
        }

        _registry?.ParkActive();
        _host?.HideToBackground();
    }

    private void RegisterAppIdentity()
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");

            Aumid.SetProcessAppUserModelId();
            Aumid.EnsureShortcut(executable);

            // Same registration the shortcut points at: without it Windows has no COM class
            // to call when a toast is clicked from the Action Center.
            ToastActivation.RegisterServer(executable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.Security.SecurityException)
        {
            // Fail open: the app still runs, but a toast may show "(unknown app)".
            _log?.Error("could not register the app identity; toasts may not appear", ex);
        }
    }

    /// <summary>
    /// Publishes the toast CLSID for this session. Registering here, in the resident process,
    /// is the part that makes Action Center clicks arrive: Windows routes an activation to an
    /// already-registered class object instead of launching <c>LocalServer32</c> a second time.
    /// </summary>
    private void RegisterToastActivation()
    {
        ToastActivation.Log = message => _log?.Info(message);
        ToastActivationCallback.Activated = args =>
        {
            _log?.Info("toast activation: callback received");

            if (!_quitting)
            {
                Dispatcher.Invoke(() => ShowHostFor(args));
            }
        };

        try
        {
            ToastActivation.RegisterClassObject();
            _log?.Info("toast activation class object registered");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log?.Error("could not register toast activation; Action Center clicks will not open the app", ex);
        }
    }

    /// <summary>
    /// Serves exactly one toast activation. COM launches the exe with <c>-Embedding</c>, so
    /// this process registers the class object, waits for the call, signals the resident
    /// instance and exits. The timeout exists so a failed activation cannot leave a stray
    /// process behind.
    /// </summary>
    private void RunActivationServer()
    {
        ToastActivation.Log = message => _log?.Info(message);

        ToastActivationCallback.Activated = _ =>
        {
            _log?.Info("toast activation: callback received");
            var signalled = SingleInstance.SignalShow();
            _log?.Info($"toast activation: show requested={signalled}");
            Shutdown(signalled ? 0 : 1);
        };

        try
        {
            ToastActivation.RegisterClassObject();
            _log?.Info($"toast activation server ready (pid {Environment.ProcessId})");
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or PlatformNotSupportedException)
        {
            _log?.Error("could not register the toast activation server", ex);
            Shutdown(1);
            return;
        }

        var timeout = new DispatcherTimer(
            TimeSpan.FromSeconds(60),
            DispatcherPriority.Normal,
            (_, _) => Shutdown(0),
            Dispatcher);
        timeout.Start();
    }

    private void OpenHost() => ShowHost();

    private void Terminate(int exitCode)
    {
        _quitting = true;

        _tray?.Dispose();
        _tray = null;

        _host?.Close();
        _host = null;

        _singleInstance?.Dispose();
        _singleInstance = null;

        Shutdown(exitCode);
    }

    private void Quit()
    {
        _log?.Info("quit requested");
        Terminate(0);
    }
}
