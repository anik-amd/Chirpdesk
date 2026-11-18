using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chirpdesk.CommandBar;
using Chirpdesk.Core.Blocking;
using Chirpdesk.Core.CommandBar;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Lifecycle;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Theming;
using Chirpdesk.Core.Time;
using Chirpdesk.Definitions;
using Chirpdesk.Diagnostics;
using Chirpdesk.Hosting;
using Chirpdesk.Interop;
using Chirpdesk.Logging;
using Chirpdesk.Notifications;
using Chirpdesk.Onboarding;
using Chirpdesk.Repository;
using Chirpdesk.Settings;
using Chirpdesk.Theming;
using Chirpdesk.Ui;
using Chirpdesk.Updates;
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
    private IReadOnlyList<PanelDefinition> _panels = Array.Empty<PanelDefinition>();
    private IReadOnlyList<InstalledPlugin> _installedPlugins = Array.Empty<InstalledPlugin>();
    private IReadOnlyList<FilterPack> _filterPacks = Array.Empty<FilterPack>();
    private IReadOnlyList<ThemePack> _themePacks = Array.Empty<ThemePack>();
    private SettingsPage? _settingsPage;
    private PluginsPage? _pluginsPage;
    private OnboardingPage? _onboarding;
    private bool _firstRun;
    private CommandBarView? _bar;
    private BitmapSource? _lastBackdrop;
    private BarState? _barState;
    private ModifierWatcher? _barWatcher;
    private IReadOnlyList<SearchCandidate> _searchCandidates = Array.Empty<SearchCandidate>();
    private bool _barOpening;
    private IntPtr _keyboardHook;
    private NativeMethods.LowLevelKeyboardProc? _keyboardHookProc;
    private bool _quitting;
    private ThemeMode _themeMode = ThemeMode.System;
    private UpdateService? _updates;
    private RepositoryService? _repositories;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = StartupOptions.Parse(e.Args);
        _firstRun = !File.Exists(AppPaths.SettingsFile)
            && !options.Autostart
            && !options.Hidden
            && options.OpenService is null
            && !options.WarmAll
            && !options.SelfTest
            && !options.TestToast;
        _log = new FileLog(AppPaths.LogsRoot, "chirpdesk.log");
        _updates = new UpdateService(_log);
        _updates.Cleanup();
        _repositories = new RepositoryService(new SettingsStore(AppPaths.SettingsFile, _log), _log);
        _registry = new ServiceRegistry(_log);
        _catalog = PluginCatalog.Load(_log);
        _panels = PluginCatalog.LoadPanels(_log);
        _installedPlugins = PluginCatalog.LoadInstalled(_log);
        _filterPacks = PluginCatalog.LoadFilters(_log);
        _themePacks = PluginCatalog.LoadThemes(_log);
        _log.Info($"tool panels: {_panels.Count} loaded; plugins: {_installedPlugins.Count} installed; filter packs: {_filterPacks.Count}; theme packs: {_themePacks.Count}");
        LinkLegacyInstances();

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
        TaskbarList.Remove(_host.Handle);
        _host.KeyDown += OnHostKeyDown;
        InstallKeyboardHook();
        _host.Closing += OnHostClosing;
        _host.HideToTrayRequested += (_, _) => HideHost();
        _host.RailServiceSelected += (_, instanceId) =>
        {
            ClosePage();
            _ = ActivateServiceAsync(instanceId);
        };
        _host.RailSearchRequested += (_, _) => _ = OpenSearchBarAsync();
        _host.RailAddRequested += (_, _) => _ = OpenSearchBarAsync();
        _host.RailPluginsRequested += (_, _) => OpenPlugins();
        _host.RailSettingsRequested += (_, _) => OpenSettings();

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

        _tray = TrayIconFactory.Create(OpenHost, HideHost, OpenSettings, Quit);
        _host.Attach(_tray);

        _bar = new CommandBarView();
        _host.SetCommandBar(_bar);
        _bar.QueryChanged += OnBarQueryChanged;
        _barWatcher = new ModifierWatcher(Dispatcher);
        _barWatcher.ControlReleased += OnControlReleased;
        _host.PreviewKeyDown += OnHostPreviewKeyDown;
        _host.SizeChanged += (_, _) => _registry?.Active?.SyncToHost();
        _registry.ActiveChanged += (_, _) =>
        {
            UpdateBusyFor(_registry.Active);
            RefreshServiceRail();
            UpdateTrayStatus();
        };
        RefreshServiceRail();

        InitializeTheme();
        SyncAutostartMirror();
        UpdateTrayStatus();

        _log.Info("started");

        if (LoadSettingsForUpdateCheck())
        {
            _ = AutoCheckUpdatesAsync();
        }

        if (!string.IsNullOrEmpty(options.Page))
        {
            if (options.Page.Equals("plugins", StringComparison.OrdinalIgnoreCase))
            {
                OpenPlugins();
            }
            else
            {
                OpenSettings();
            }

            if (options.QuitAfter is { } pageDelay)
            {
                _ = QuitAfterAsync(pageDelay);
            }

            return;
        }

        if (options.WarmAll)
        {
            _ = WarmAllAsync(show: !options.Hidden);
        }
        else if (options.OpenService is not null)
        {
            _ = OpenServiceAsync(options.OpenService, show: !options.Hidden, probe: options.NotifyTest);
        }
        else
        {
            _ = WarmPinnedAtBootAsync(options.Autostart, show: !options.Autostart && !options.Hidden && !_firstRun);
        }

        if (_firstRun)
        {
            ShowOnboarding();
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

        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

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
            Blocking = instance.Blocking,
            DefinitionId = resolved.DefinitionId,
            ProfileId = instance.ProfileId ?? resolved.InstanceId,
        };

        var dark = ThemeDecision.IsDark(_themeMode, SystemTheme.UsesLight());
        var filterPacks = EnabledFilterPacks();

        var service = await ServiceRuntime.CreateAsync(
            _host!,
            effective,
            _log!,
            SystemClock.Instance,
            new GatedToastSink(
                new ToastService(_log!, id => Dispatcher.Invoke(() => ShowHostFor(id))),
                new SettingsStore(AppPaths.SettingsFile, _log!),
                _log!),
            DateTimeOffset.UtcNow,
            dark,
            filterPacks);

        service.ShortcutRequested += OnServiceShortcutRequested;
        service.NavigationStateChanged += (_, _) => UpdateBusyFor(_registry?.Active);
        service.UnreadChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateTrayStatus));

        await service.ApplyThemeAsync(dark);
        await _registry!.AddAsync(service);
        _registry.Activate(service.InstanceId);
        RefreshServiceRail();
        UpdateTrayStatus();

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
    /// Normal launch path: warm every instance pinned at boot, hidden, staggered. Autostart adds
    /// one 20 s delay before the first webview so login is not competing with Chromium.
    /// </summary>
    private async Task WarmPinnedAtBootAsync(bool autostart, bool show)
    {
        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            var plan = BootPlan.Select(settings);

            if (plan.Count == 0)
            {
                _log?.Info("boot: no services pinned warm");
                if (show)
                {
                    ShowLaunchTarget();
                }

                return;
            }

            var initial = autostart ? TimeSpan.FromSeconds(20) : TimeSpan.Zero;
            var started = DateTimeOffset.UtcNow;
            _log?.Info($"boot: warming {plan.Count} pinned service(s), autostart={autostart}");

            for (var index = 0; index < plan.Count; index++)
            {
                var wait = BootPlan.DelayFor(index, initial) - (DateTimeOffset.UtcNow - started);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait);
                }

                await OpenInstanceAsync(plan[index], show: false, probe: false);

                // A user launch should surface promptly; the rest keep warming behind it.
                if (show && index == 0)
                {
                    ShowHost();
                }
            }

            _log?.Info($"boot: warmed {plan.Count} pinned service(s)");
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or InvalidOperationException)
        {
            _log?.Error("could not warm pinned services", ex);
        }
    }

    /// <summary>
    /// A normal launch has no service argument. Show the first enabled account, or the plugin
    /// page when there is nothing to open. Autostart never reaches this method with show=true.
    /// </summary>
    private void ShowLaunchTarget()
    {
        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            var first = settings.Services
                .Where(instance => instance.Enabled)
                .OrderBy(instance => instance.Order)
                .FirstOrDefault();

            if (first is null)
            {
                OpenPlugins();
                return;
            }

            _ = ActivateServiceAsync(first.InstanceId);
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log?.Warn($"could not choose a launch target: {ex.Message}");
            OpenPlugins();
        }
    }

    /// <summary>Keeps the settings mirror honest with the HKCU entry; the OS is the source of truth.</summary>
    private void SyncAutostartMirror()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            return;
        }

        try
        {
            var store = new SettingsStore(AppPaths.SettingsFile, _log!);
            var settings = store.Load();
            var enabled = Autostart.IsEnabled(executable, _log!);

            if (settings.Autostart != enabled)
            {
                settings.Autostart = enabled;
                store.Save(settings);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log?.Warn($"could not mirror the autostart state: {ex.Message}");
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
        Dispatcher.Invoke(() =>
        {
            if (_registry?.Active is null)
            {
                ShowLaunchTarget();
            }
            else
            {
                ShowHost();
            }
        });
    }

    private void InstallKeyboardHook()
    {
        _keyboardHookProc = KeyboardHookCallback;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _keyboardHookProc,
            IntPtr.Zero,
            0);

        if (_keyboardHook == IntPtr.Zero)
        {
            _log?.Warn("keyboard hook unavailable; command-bar keys fall back to the window");
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WmKeyDown || wParam == (IntPtr)NativeMethods.WmSysKeyDown))
        {
            var vkCode = Marshal.ReadInt32(lParam);
            var isCommandKey = vkCode == 0x4B;
            var isTabKey = vkCode == 0x09;

            if ((isCommandKey || isTabKey) && NativeMethods.IsControlDown())
            {
                uint processId = 0;
                _ = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out processId);

                if (processId == (uint)Environment.ProcessId)
                {
                    var shift = NativeMethods.IsShiftDown();

                    if (isCommandKey)
                    {
                        HandleCommandBarHotkey(shift);
                    }
                    else
                    {
                        HandleTabHotkey(shift);
                    }

                    return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void HandleCommandBarHotkey(bool shift)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_host is null || _bar is null || _registry is null)
            {
                return;
            }

            if (shift)
            {
                if (_barState is null && !_barOpening)
                {
                    _ = OpenTabsBarAsync();
                }
                else if (_barState is not null)
                {
                    SwitchToTabs();
                }

                return;
            }

            if (_barState is null && !_barOpening)
            {
                _ = OpenSearchBarAsync();
            }
            else if (_barState?.Mode is BarMode.Cycle or BarMode.Tabs)
            {
                SwitchToSearch();
            }
        }));
    }

    private void HandleTabHotkey(bool shift)
    {
        var direction = shift ? -1 : 1;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_host is null || _bar is null || _registry is null)
            {
                return;
            }

            if (_barState is not null)
            {
                if (_barState.Move(direction))
                {
                    RenderCommandBar();
                }

                return;
            }

            if (!_barOpening)
            {
                _ = OpenCommandBarAsync(direction);
            }
        }));
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (_host?.IsPageOpen == true)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;

                if (_onboarding is not null)
                {
                    DismissOnboarding();
                }
                else
                {
                    ClosePage();
                }

                return;
            }

            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;

                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    _ = OpenTabsBarAsync();
                }
                else
                {
                    _ = OpenSearchBarAsync();
                }

                return;
            }

            return;
        }

        if (_barState is not null)
        {
            return;
        }

        // Ctrl+K opens search; Ctrl+Shift+K opens the sticky tab list.
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _ = OpenTabsBarAsync();
            }
            else
            {
                _ = OpenSearchBarAsync();
            }

            return;
        }

        if (e.Key == Key.W && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            _ = CloseTabAsync(_registry?.Active?.InstanceId);
            return;
        }

        // Ctrl+Tab opens cycle mode on the next warm service; releasing Ctrl commits. The
        // always-available version needs the global hotkey and the overlay, which is the next
        // slice; this one proves the registry and bar path.
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            var direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
            _ = OpenCommandBarAsync(direction);
        }
    }

    private void OnBarQueryChanged(object? sender, string query)
    {
        if (_barState?.Mode != BarMode.Search)
        {
            return;
        }

        _barState.SetQuery(query);
        UpdateSearchResults(keepSelected: null);
    }

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_barState is null)
        {
            return;
        }

        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                SwitchToTabs();
            }
            else if (_barState.Mode is BarMode.Cycle or BarMode.Tabs)
            {
                SwitchToSearch();
            }

            return;
        }

        var action = BarKeys.Decide(_barState, ToBarKey(e.Key), ToBarModifiers());

        switch (action.Kind)
        {
            case BarKeyActionKind.Move:
                e.Handled = true;
                if (_barState.Move(action.Value))
                {
                    RenderCommandBar();
                }

                return;

            case BarKeyActionKind.Backspace:
                // The query line is a real TextBox; let it handle editing and clipboard.
                return;

            case BarKeyActionKind.Commit:
                e.Handled = true;
                CommitSearchSelection();
                return;

            case BarKeyActionKind.Cancel:
                e.Handled = true;
                CloseCommandBar(restore: true);
                return;

            case BarKeyActionKind.CloseTab:
                e.Handled = true;
                _ = CloseTabAsync(_barState.Selected ?? _registry?.Active?.InstanceId);
                return;

            case BarKeyActionKind.SelectIndex:
                e.Handled = true;
                CommitBarIndex(action.Value);
                return;
        }
    }

    private static BarKey ToBarKey(Key key) => key switch
    {
        Key.Tab => BarKey.Tab,
        Key.Up => BarKey.Up,
        Key.Down => BarKey.Down,
        Key.Escape => BarKey.Escape,
        Key.Enter => BarKey.Enter,
        Key.Back => BarKey.Backspace,
        Key.W => BarKey.W,
        Key.D1 => BarKey.Digit1,
        Key.D2 => BarKey.Digit2,
        Key.D3 => BarKey.Digit3,
        Key.D4 => BarKey.Digit4,
        Key.D5 => BarKey.Digit5,
        Key.D6 => BarKey.Digit6,
        Key.D7 => BarKey.Digit7,
        Key.D8 => BarKey.Digit8,
        Key.D9 => BarKey.Digit9,
        _ => BarKey.None,
    };

    private static BarModifiers ToBarModifiers()
    {
        var modifiers = BarModifiers.None;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            modifiers |= BarModifiers.Control;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            modifiers |= BarModifiers.Shift;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            modifiers |= BarModifiers.Alt;
        }

        return modifiers;
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

            if (e.CloseTab)
            {
                _ = CloseTabAsync(_registry?.Active?.InstanceId);
                return;
            }

            if (e.Tabs)
            {
                _ = OpenTabsBarAsync();
            }
            else if (e.Search)
            {
                _ = OpenSearchBarAsync();
            }
            else
            {
                _ = OpenCommandBarAsync(e.Direction);
            }
        });
    }

    private async Task OpenCommandBarAsync(int direction)
    {
        if (_host is null || _bar is null || _registry is null || _barState is not null || _barOpening)
        {
            return;
        }

        var order = _registry.Order;
        if (order.Count == 0)
        {
            return;
        }

        _barOpening = true;

        try
        {
            var backdrop = await CaptureActiveAsync();
            if (_barState is not null)
            {
                return;
            }

            _barState = BarState.ForCycle(order, _registry.Active?.InstanceId, direction);
            RenderCommandBar();

            // The webview is an HWND and would paint over the bar until it is collapsed; the
            // captured frame keeps the site visible behind the scrim. A page is WPF and stays
            // exactly where it is, so the palette opens over it.
            if (_host.IsPageOpen != true)
            {
                _registry.Active?.CollapseForCommandBar();
            }

            _host.ShowCommandBar(backdrop);
            _host.Focus();
            _barWatcher?.Start();

            _log?.Info($"command bar opened: {order.Count} warm service(s)");
        }
        finally
        {
            _barOpening = false;
        }
    }

    /// <summary>Ctrl+Shift+K shows the same open-tab list as Ctrl+Tab, but sticky.</summary>
    private async Task OpenTabsBarAsync()
    {
        if (_host is null || _bar is null || _registry is null || _barState is not null || _barOpening)
        {
            return;
        }

        var order = _registry.Order;
        if (order.Count == 0)
        {
            return;
        }

        _barOpening = true;

        try
        {
            var backdrop = await CaptureActiveAsync();
            if (backdrop is not null)
            {
                _lastBackdrop = backdrop;
            }
            else
            {
                backdrop = _lastBackdrop;
            }

            if (_barState is not null)
            {
                return;
            }

            _barState = BarState.ForTabs(order, _registry.Active?.InstanceId);
            RenderCommandBar();

            if (_host.IsPageOpen != true)
            {
                _registry.Active?.CollapseForCommandBar();
            }

            _host.ShowCommandBar(backdrop);
            _host.Focus();

            _log?.Info($"command bar opened: {order.Count} open tab(s)");
        }
        finally
        {
            _barOpening = false;
        }
    }

    private async Task OpenSearchBarAsync()
    {
        if (_host is null || _bar is null || _registry is null || _barState is not null || _barOpening)
        {
            return;
        }

        _barOpening = true;

        try
        {
             var backdrop = await CaptureActiveAsync();
             if (backdrop is not null)
             {
                 _lastBackdrop = backdrop;
             }
             else
             {
                 backdrop = _lastBackdrop;
             }
             if (_barState is not null)
            {
                return;
            }

            _searchCandidates = BuildSearchCandidates();
            if (_searchCandidates.Count == 0)
            {
                return;
            }

            _barState = BarState.ForSearch();
            UpdateSearchResults(keepSelected: null);

            // Same airspace rule as cycle mode, but a Settings or Plugins page stays visible
            // underneath: only the webview is collapsed when it is the surface being shown.
            if (_host.IsPageOpen != true)
            {
                _registry.Active?.CollapseForCommandBar();
            }

            _host.ShowCommandBar(backdrop);
            _host.Focus();
            _bar.FocusQuery();

            _log?.Info(
                $"command bar opened in search mode: {_searchCandidates.Count} tab(s), backdrop={backdrop?.PixelWidth ?? 0}x{backdrop?.PixelHeight ?? 0}");
        }
        finally
        {
            _barOpening = false;
        }
    }

    private async Task<BitmapSource?> CaptureActiveAsync()
    {
        var active = _registry?.Active;
        return active is null ? null : await active.CapturePreviewAsync();
    }

    /// <summary>
    /// Ctrl+K while cycling keeps the unified bar open and switches it to search, with the
    /// highlighted tab still highlighted.
    /// </summary>
    private void SwitchToSearch()
    {
        if (_barState is null || _barState.Mode is not (BarMode.Cycle or BarMode.Tabs))
        {
            return;
        }

        var keep = _barState.Selected;
        _barWatcher?.Stop();

        _barState = BarState.ForSearch();
        _searchCandidates = BuildSearchCandidates();
        UpdateSearchResults(keepSelected: keep);
        _bar?.FocusQuery();

        _log?.Info("command bar switched to search mode");
    }

    /// <summary>Ctrl+Shift+K switches any open bar to the sticky tab list.</summary>
    private void SwitchToTabs()
    {
        if (_barState is null || _registry is null)
        {
            return;
        }

        var keep = _barState.Selected;
        _barWatcher?.Stop();

        _barState = BarState.ForTabs(_registry.Order, keep ?? _registry.Active?.InstanceId);
        RenderCommandBar();

        _log?.Info("command bar switched to tab list");
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

        // Open tabs first, in MRU order.
        foreach (var id in _registry.Order)
        {
            byId.TryGetValue(id, out var instance);

            if (instance is not null && !instance.Enabled)
            {
                continue;
            }

            var resolved = instance is null ? null : _catalog?.Resolve(instance);
            var aliases = resolved?.DefinitionId is { } definitionId
                ? _catalog?.Find(definitionId)?.Aliases
                : null;

            candidates.Add(new SearchCandidate(
                id,
                resolved?.Name ?? _registry.Find(id)?.Name ?? id,
                SearchKind.Warm,
                aliases,
                HostOf(resolved?.Url),
                State: "open",
                Accent: AccentOf(resolved?.DefinitionId),
                Initial: InitialOf(resolved?.Name ?? _registry.Find(id)?.Name ?? id),
                DefinitionId: resolved?.DefinitionId ?? _registry.Find(id)?.DefinitionId));
        }

        // Enabled instances that are not warm yet: asleep or cold.
        foreach (var instance in settings.Services.Where(service => service.Enabled).OrderBy(service => service.Order))
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

            var state = _registry.StateOf(instance.InstanceId).ToString().ToLowerInvariant();
            candidates.Add(new SearchCandidate(
                instance.InstanceId,
                resolved.Name,
                SearchKind.Cold,
                aliases,
                HostOf(resolved.Url),
                State: state,
                Accent: AccentOf(resolved.DefinitionId),
                Initial: InitialOf(resolved.Name),
                DefinitionId: resolved.DefinitionId));
        }

        var definitions = _catalog is null
            ? Array.Empty<ServiceDefinition>()
            : _catalog.Definitions.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var servicePlugins = _installedPlugins
            .Where(plugin => string.Equals(plugin.Kind, PluginLoader.ServiceKind, StringComparison.Ordinal))
            .Select(plugin => plugin.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Where(definition => servicePlugins.Contains(definition.Id)))
        {
            var hasEnabledInstance = settings.Services.Any(
                instance => instance.Enabled
                    && string.Equals(instance.DefinitionId, definition.Id, StringComparison.OrdinalIgnoreCase));

            if (hasEnabledInstance)
            {
                candidates.Add(new SearchCandidate(
                    $"plugin-off:{definition.Id}",
                    $"Deactivate {definition.Name}",
                    SearchKind.Command,
                    (definition.Aliases ?? []).Append("plugin").Append("deactivate").ToList(),
                    State: "active",
                    Accent: definition.Accent,
                    Initial: InitialOf(definition.Name),
                    DefinitionId: definition.Id));
                continue;
            }

            candidates.Add(new SearchCandidate(
                $"plugin:{definition.Id}",
                definition.Name,
                SearchKind.Cold,
                (definition.Aliases ?? []).Append("plugin").Append("off").ToList(),
                State: "inactive",
                Accent: definition.Accent,
                Initial: InitialOf(definition.Name),
                DefinitionId: definition.Id));
        }

        candidates.Add(new SearchCandidate(
            "command:settings", "Settings", SearchKind.Command,
            ["settings", "preferences"], State: null, Accent: "#5E5A60", Initial: "S"));
        candidates.Add(new SearchCandidate(
            "command:plugins", "Plugins & services", SearchKind.Command,
            ["plugins", "plugin", "add", "services"], State: null, Accent: "#275949", Initial: "P"));

        // Only meaningful with a current tab and while the page-dark plugin is active.
        if (_registry.Active is { } active
            && PluginState.IsEnabled(settings, PluginState.PageDarkId))
        {
            var pageDark = string.Equals(
                active.PageDarkMode,
                ServiceDefinition.PageDarkOn,
                StringComparison.OrdinalIgnoreCase);

            candidates.Add(new SearchCandidate(
                "command:page-dark",
                pageDark ? "Turn page dark mode off" : "Turn page dark mode on",
                SearchKind.Command,
                ["page", "dark", "theme", "night", "mode", "appearance", "toggle"],
                State: null,
                Accent: "#3A4A63",
                Initial: "D"));
        }

        // Per-tab blocking switch; only when a tab is active.
        if (_registry.Active is { } blockingTab)
        {
            var blockingOff = string.Equals(
                blockingTab.Blocking,
                "off",
                StringComparison.OrdinalIgnoreCase);

            candidates.Add(new SearchCandidate(
                "command:blocking",
                blockingOff ? "Turn blocking on for this tab" : "Turn blocking off for this tab",
                SearchKind.Command,
                ["blocking", "ads", "adblock", "tracker", "filters", "on", "off"],
                State: null,
                Accent: "#8A4B2A",
                Initial: "B"));
        }

        return candidates;
    }

    private IReadOnlyList<FilterPack> EnabledFilterPacks()
    {
        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            return _filterPacks
                .Where(pack => PluginState.IsEnabled(settings, pack.Id))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            // Fail open: if settings cannot be read, keep the packs loaded at boot.
            _log?.Warn($"could not read filter plugin state: {ex.Message}");
            return _filterPacks;
        }
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

        var ranked = string.IsNullOrWhiteSpace(_barState.Query)
            ? SearchIndex.Rank(
                "",
                _searchCandidates
                    .Where(candidate =>
                        (candidate.Kind == SearchKind.Warm && candidate.State == "open")
                        || candidate.State == "inactive")
                    .ToList())
            : SearchIndex.Rank(_barState.Query, _searchCandidates);
        _barState.ShowResults(ranked.Select(candidate => candidate.Id).ToList(), keepSelected);
        RenderCommandBar();
    }

    private void CommitBarIndex(int index)
    {
        if (_barState is null || index < 0 || index >= _barState.Entries.Count)
        {
            return;
        }

        var id = _barState.Entries[index];
        var mode = _barState.Mode;

        _barState.Commit();
        CloseCommandBar(restore: true);

        if (mode == BarMode.Search)
        {
            ClosePage();
            _ = ActivateServiceAsync(id);
            _log?.Info($"command bar search committed '{id}' via Ctrl+{index + 1}");
            return;
        }

        ClosePage();
        var switched = _registry?.Activate(id) == true;
        _registry?.Active?.FocusPage();
        _log?.Info($"command bar committed '{id}' via Ctrl+{index + 1}: switched={switched}");    }

    private void CommitSearchSelection()
    {        if (_barState is null)
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

        if (string.Equals(selected, "command:settings", StringComparison.Ordinal))
        {
            OpenSettings();
            return;
        }

        if (string.Equals(selected, "command:plugins", StringComparison.Ordinal))
        {
            OpenPlugins();
            return;
        }

        if (string.Equals(selected, "command:page-dark", StringComparison.Ordinal))
        {
            _ = TogglePageDarkModeAsync();
            return;
        }

        if (string.Equals(selected, "command:blocking", StringComparison.Ordinal))
        {
            ToggleBlocking();
            return;
        }

        if (selected.StartsWith("plugin:", StringComparison.Ordinal))
        {
            var definition = _catalog?.Find(selected["plugin:".Length..]);
            if (definition is not null)
            {
                _log?.Info($"command bar activating plugin '{definition.Id}'");
                _ = EnablePluginAsync(definition);
            }

            return;
        }

        if (selected.StartsWith("plugin-off:", StringComparison.Ordinal))
        {
            var definition = _catalog?.Find(selected["plugin-off:".Length..]);
            if (definition is not null)
            {
                _log?.Info($"command bar deactivating plugin '{definition.Id}'");
                _ = DisablePluginAsync(definition);
            }

            return;
        }

        ClosePage();
        _ = ActivateServiceAsync(selected);
        _log?.Info($"command bar search committed '{selected}'");
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    private string? AccentOf(string? definitionId) =>
        definitionId is null ? null : _catalog?.Find(definitionId)?.Accent;

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.TrimStart()[0]).ToString();

    private static System.Windows.Media.SolidColorBrush BrushFor(string? accent)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(accent)
                && System.Windows.Media.ColorConverter.ConvertFromString(accent) is System.Windows.Media.Color color)
            {
                return new System.Windows.Media.SolidColorBrush(color);
            }
        }
        catch (FormatException)
        {
            // Fall through to the neutral badge.
        }

        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0x6B, 0x62));
    }

    private void RenderCommandBar()
    {
        if (_bar is null || _barState is null || _host is null || _registry is null)
        {
            return;
        }

        var lookup = _searchCandidates
            .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var rows = _barState.Entries
            .Select(id =>
            {
                lookup.TryGetValue(id, out var candidate);
                var runtime = _registry.Find(id);
                var name = candidate?.Name ?? runtime?.Name ?? id;

                if (!string.IsNullOrWhiteSpace(candidate?.State)
                    && candidate.State is not ("open" or "inactive" or "active"))
                {
                    name = $"{name} · {candidate.State}";
                }

                var accent = candidate?.Accent ?? AccentOf(runtime?.DefinitionId ?? candidate?.DefinitionId);
                var initial = candidate?.Initial ?? InitialOf(name);
                var unread = runtime?.UnreadCount ?? 0;
                var definitionId = runtime?.DefinitionId ?? candidate?.DefinitionId;
                var accentBrush = ServiceBadge.BrushFor(accent);
                var icon = id switch
                {
                    "command:settings" => UiIcons.Create("settings", accentBrush, 22),
                    "command:plugins" => UiIcons.Create("plugins", accentBrush, 22),
                    "command:page-dark" => UiIcons.Create("moon", accentBrush, 22),
                    "command:blocking" => UiIcons.Create("shield", accentBrush, 22),
                    _ => definitionId is null
                        ? ServiceBadge.Create(name, accent, 22)
                        : ServiceIcons.Create(definitionId, name, accent, 22),
                };

                if (candidate?.State is "inactive" or "plugin off")
                {
                    icon.Opacity = 0.35;
                }

                return new BarRow(
                    id,
                    name,
                    icon,
                    unread > 0 ? unread.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
            })
            .ToList();

        _bar.Render(rows, _barState.Highlight, _barState.Mode, _barState.Query, _host.IsDark);
    }

    private void CloseCommandBar(bool restore)
    {
        _barWatcher?.Stop();
        _barState?.Cancel();
        _barState = null;

        _host?.HideCommandBar();

        if (restore && _host?.IsPageOpen != true)
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

        if (_host?.CloseToTray == false)
        {
            e.Cancel = true;
            Quit();
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
            ServiceRuntime.PageDarkeningEnabled = PluginState.IsEnabled(settings, PluginState.PageDarkId);

            if (_host is not null)
            {
                _host.MinimizeToTray = settings.MinimizeToTray;
                _host.CloseToTray = settings.CloseToTray;
                _host.SetRailMode(settings.RailMode);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _themeMode = ThemeMode.System;
            ServiceRuntime.PageDarkeningEnabled = true;
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

        string? lightTheme = null;
        string? darkTheme = null;
        var catppuccin = false;

        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            lightTheme = settings.LightTheme;
            darkTheme = settings.DarkTheme;
            catppuccin = PluginState.IsEnabled(settings, PluginState.CatppuccinId);
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log?.Warn($"could not read the palette selection: {ex.Message}");
        }

        UiTheme.Apply(dark, lightTheme, darkTheme, catppuccin);
        _host?.SetDark(dark);
        _settingsPage?.SetDark(dark);
        _pluginsPage?.Reload();

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

        if (_host.IsPageOpen)
        {
            ClosePage();
        }

        if (_barState is not null)
        {
            CloseCommandBar(restore: true);
        }

        var wasVisible = _host.IsVisible;
        _registry?.Active?.Activate();
        var foreground = _host.ShowForUser();
        UpdateBusyFor(_registry?.Active);
        _registry?.Active?.FocusPage();

        if (_registry?.Active is { } activeService)
        {
            activeService.SyncToHost();
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(activeService.SyncToHost));
        }

        _log?.Info($"window shown: wasVisible={wasVisible} foreground={foreground} visible={_host.IsVisible}");
    }

    /// <summary>
    /// Shows the window for the service a toast came from. Warm switches in place; cold starts
    /// it from settings instead of showing the wrong page. Fire-and-forget because the caller
    /// is a WinRT event or a COM callback, neither of which can await.
    /// </summary>
    private bool LoadSettingsForUpdateCheck()
    {
        try
        {
            return new SettingsStore(AppPaths.SettingsFile, _log!).Load().UpdateAutoCheck;
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task AutoCheckUpdatesAsync()
    {
        if (_updates is null)
        {
            return;
        }

        var result = await _updates.CheckAsync();
        if (!result.Available)
        {
            return;
        }

        _log?.Info($"update ready: {result.Version}");
        try
        {
            var toasts = new ToastService(
                _log!,
                id => Dispatcher.Invoke(() => ShowHostFor(id)));
            toasts.Show(new ToastMessage(
                "Chirpdesk update",
                $"Version {result.Version} is ready. Open Settings to install.",
                null,
                "update"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _log?.Warn($"could not show the update toast: {ex.Message}");
        }
    }

    private async Task<bool> InstallUpdateAsync(UpdateCheckResult result)
    {
        if (_updates is null || result.Manifest is null)
        {
            return false;
        }

        var installer = await _updates.DownloadAsync(result.Manifest);
        if (installer is null)
        {
            return false;
        }

        _log?.Info("update: closing webviews before the installer");
        _quitting = true;

        try
        {
            if (_registry is not null)
            {
                await _registry.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _log?.Warn($"update: service dispose reported {ex.Message}");
        }

        if (!_updates.LaunchInstaller(installer))
        {
            MessageBox.Show(
                "The verified installer could not be started. Chirpdesk will close; open it again from the Start menu.",
                "Chirpdesk update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return true;
        }

        Shutdown(0);
        return true;
    }

    private void ShowHostFor(string? serviceId)
    {
        var id = string.IsNullOrWhiteSpace(serviceId) ? null : Uri.UnescapeDataString(serviceId);

        if (id is null)
        {
            ShowHost();
            return;
        }

        if (string.Equals(id, "update", StringComparison.Ordinal))
        {
            OpenSettings();
            return;
        }

        _ = ActivateServiceAsync(id);
    }

    private void HideHost()
    {
        if (_host?.IsPageOpen == true)
        {
            ClosePage();
        }

        if (_barState is not null)
        {
            CloseCommandBar(restore: true);
        }

        _registry?.ParkActive();
        _host?.HideToBackground();
    }

    /// <summary>
    /// Closes a warm tab (the highlighted one when the palette is open, otherwise the active
    /// one). The instance stays in settings and becomes cold; if it was the last tab, the
    /// window hides.
    /// </summary>
    private async Task CloseTabAsync(string? instanceId)
    {
        if (_registry is null || string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        if (_barState is not null)
        {
            CloseCommandBar(restore: false);
        }

        if (!await _registry.CloseAsync(instanceId))
        {
            return;
        }

        _log?.Info($"closed tab '{instanceId}'");
        RefreshServiceRail();
        UpdateTrayStatus();

        if (_registry.Active is not null)
        {
            ShowHost();
        }
        else
        {
            HideHost();
        }
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

    /// <summary>Keeps the tray honest about DND without opening Settings.</summary>
    private void UpdateBusyFor(ServiceRuntime? service) =>
        _host?.SetBusy(service?.IsNavigating == true);

    /// <summary>Rebuilds the rail from the warm instances; multi-profile entries get a 1/2 badge.</summary>
    private void RefreshServiceRail()
    {
        if (_host is null || _registry is null)
        {
            return;
        }

        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            var warm = settings.Services
                .Where(instance => _registry.IsWarm(instance.InstanceId))
                .OrderBy(instance => instance.Order)
                .ToList();

            var keys = warm.Select(instance => instance.DefinitionId ?? instance.InstanceId).ToList();
            var counts = keys
                .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<HostWindow.ServiceRailEntry>();

            foreach (var instance in warm)
            {
                var key = instance.DefinitionId ?? instance.InstanceId;
                var ordinal = ordinals.TryGetValue(key, out var seen) ? seen + 1 : 1;
                ordinals[key] = ordinal;

                entries.Add(new HostWindow.ServiceRailEntry(
                    instance.InstanceId,
                    instance.Name,
                    instance.DefinitionId,
                    _catalog?.Find(instance.DefinitionId)?.Accent,
                    ordinal,
                    counts[key] > 1));
            }

            _host.SetServiceRail(entries, _registry.Active?.InstanceId);
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log?.Warn($"could not refresh the service rail: {ex.Message}");
        }
    }

    private void UpdateTrayStatus()
    {
        if (_tray is null)
        {
            return;
        }

        try
        {
            var settings = new SettingsStore(AppPaths.SettingsFile, _log!).Load();
            var unread = _registry?.Order
                .Select(id => _registry.Find(id)?.UnreadCount ?? 0)
                .Sum() ?? 0;

            _tray.IconSource = TrayIconFactory.CreateIcon(unread > 0);

            var title = unread > 0 ? $"Chirpdesk - {unread} unread" : "Chirpdesk";
            _tray.ToolTipText = settings.DndUntil is { } until && until > DateTimeOffset.UtcNow
                ? $"{title} - DND until {until.ToLocalTime():HH:mm}"
                : title;
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log?.Warn($"could not update the tray status: {ex.Message}");
        }
    }

    private void OpenHost() => ShowHost();

    private void ShowOnboarding()
    {
        if (_host is null)
        {
            return;
        }

        _onboarding = new OnboardingPage(
            () =>
            {
                CompleteFirstRun();
                OpenPlugins();
            },
            DismissOnboarding);

        _log?.Info("first run: showing onboarding");
        ShowPage(_onboarding);
    }

    /// <summary>
    /// Closing the overlay on a fresh install must return to hidden-warm. There is no active tab
    /// behind it, so leaving the window visible would be exactly the unprompted window the
    /// product forbids.
    /// </summary>
    private void DismissOnboarding()
    {
        CompleteFirstRun();
        ClosePage();

        if (_registry?.Active is { } active)
        {
            active.FocusPage();
        }
        else
        {
            HideHost();
        }
    }

    /// <summary>
    /// Writing the default settings file is the durable "onboarding seen" marker. It keeps the
    /// overlay off every later launch without adding a schema field that existing settings lack.
    /// </summary>
    private void CompleteFirstRun()
    {
        if (!_firstRun)
        {
            return;
        }

        _firstRun = false;

        try
        {
            var store = new SettingsStore(AppPaths.SettingsFile, _log!);
            store.Save(store.Load());
            _log?.Info("first run: onboarding dismissed");
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log?.Warn($"could not record the first run: {ex.Message}");
        }
    }

    private void OpenSettings()
    {
        if (_host is null || _registry is null || _log is null)
        {
            return;
        }

        if (_settingsPage is null)
        {
            _settingsPage = new SettingsPage(
                new SettingsStore(AppPaths.SettingsFile, _log),
                _catalog ?? new ServiceCatalog([]),
                _panels,
                _registry,
                _host,
                _themePacks.SelectMany(pack => pack.Flavors).ToList(),
                _log,
                ApplyTheme,
                UpdateTrayStatus,
                ClosePage,
                _updates ?? new UpdateService(_log),
                InstallUpdateAsync);
        }

        ShowPage(_settingsPage);
        _settingsPage.Reload();
    }

    private void OpenPlugins()
    {
        if (_host is null || _registry is null || _log is null || _catalog is null)
        {
            return;
        }

        if (_pluginsPage is null)
        {
            _pluginsPage = new PluginsPage(
                _catalog,
                _installedPlugins,
                new SettingsStore(AppPaths.SettingsFile, _log),
                _registry,
                _log,
                EnablePluginAsync,
                AddAccountAsync,
                DisablePluginAsync,
                SetPluginEnabledAsync,
                ClosePage,
                _repositories ?? new RepositoryService(new SettingsStore(AppPaths.SettingsFile, _log), _log));
        }

        ShowPage(_pluginsPage);
        _pluginsPage.Reload();
    }

    private void ShowPage(FrameworkElement page)
    {
        if (_host is null || _registry is null)
        {
            return;
        }

        if (_barState is not null)
        {
            CloseCommandBar(restore: false);
        }

        if (!ReferenceEquals(page, _onboarding))
        {
            _onboarding = null;
        }

        _registry.Active?.CollapseForCommandBar();
        _host.ShowForUser();

        _host.SetPage(page);
        _host.ShowPage();
        _host.Focus();
    }

    private void LinkLegacyInstances()
    {
        if (_catalog is null || _log is null)
        {
            return;
        }

        try
        {
            var store = new SettingsStore(AppPaths.SettingsFile, _log);
            var settings = store.Load();
            var changed = false;

            foreach (var instance in settings.Services.Where(instance => instance.DefinitionId is null))
            {
                var definition = _catalog.Definitions.FirstOrDefault(
                    candidate => string.Equals(candidate.Url, instance.Url, StringComparison.OrdinalIgnoreCase));

                if (definition is null)
                {
                    continue;
                }

                instance.DefinitionId = definition.Id;
                changed = true;
            }

            if (changed)
            {
                store.Save(settings);
                _log.Info("linked legacy instances to their definitions");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log.Warn($"could not link legacy instances: {ex.Message}");
        }
    }

    private void ClosePage()
    {
        if (_host is null)
        {
            return;
        }

        if (_onboarding is not null)
        {
            CompleteFirstRun();
            _onboarding = null;
        }

        _host.HidePage();
        _registry?.Active?.RestoreFromCommandBar();
        _host.UpdateLayout();
    }

    private async Task EnablePluginAsync(ServiceDefinition definition)
    {
        if (_log is null || _catalog is null)
        {
            return;
        }

        var store = new SettingsStore(AppPaths.SettingsFile, _log);
        var settings = store.Load();
        var instances = settings.Services
            .Where(instance => string.Equals(instance.DefinitionId, definition.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (instances.Count == 0)
        {
            var instance = InstanceFactory.Create(definition, definition.Name, settings.Services);
            instance.Enabled = true;
            settings.Services.Add(instance);
            instances.Add(instance);
            _log.Info($"plugin '{definition.Id}' turned on; instance '{instance.InstanceId}' created");
        }

        foreach (var instance in instances)
        {
            instance.Enabled = true;
        }

        store.Save(settings);

        var first = instances[0];
        ClosePage();
        await OpenInstanceAsync(first, show: true, probe: false);
    }

    private async Task AddAccountAsync(ServiceDefinition definition)
    {
        if (_log is null)
        {
            return;
        }

        var store = new SettingsStore(AppPaths.SettingsFile, _log);
        var settings = store.Load();
        ServiceInstance instance;

        try
        {
            instance = InstanceFactory.Create(definition, definition.Name, settings.Services);
        }
        catch (ArgumentException ex)
        {
            _log.Warn($"plugin '{definition.Id}' cannot add another account: {ex.Message}");
            return;
        }

        instance.Enabled = true;
        settings.Services.Add(instance);
        store.Save(settings);
        _log.Info($"plugin '{definition.Id}' added account '{instance.InstanceId}'");

        ClosePage();
        await OpenInstanceAsync(instance, show: true, probe: false);
    }

    private async Task DisablePluginAsync(ServiceDefinition definition)
    {
        if (_log is null || _registry is null)
        {
            return;
        }

        var store = new SettingsStore(AppPaths.SettingsFile, _log);
        var settings = store.Load();
        var instances = settings.Services
            .Where(instance => string.Equals(instance.DefinitionId, definition.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var instance in instances)
        {
            instance.Enabled = false;

            if (_registry.StateOf(instance.InstanceId) == RuntimeState.Warm)
            {
                await _registry.SleepAsync(instance.InstanceId);
            }
        }

        store.Save(settings);
        RefreshServiceRail();
        UpdateTrayStatus();
        _log.Info($"plugin '{definition.Id}' turned off; sessions preserved");
    }

    /// <summary>
    /// Flips the current tab between forced dark and off. The choice is persisted per instance,
    /// so a toggle survives a restart and stays scoped to that account.
    /// </summary>
    private async Task TogglePageDarkModeAsync()
    {
        if (_log is null || _registry?.Active is not { } runtime)
        {
            return;
        }

        var store = new SettingsStore(AppPaths.SettingsFile, _log);
        var settings = store.Load();

        if (!PluginState.IsEnabled(settings, PluginState.PageDarkId))
        {
            _log.Info("page dark mode ignored: the page-dark plugin is deactivated");
            return;
        }

        var next = string.Equals(runtime.PageDarkMode, ServiceDefinition.PageDarkOn, StringComparison.OrdinalIgnoreCase)
            ? ServiceDefinition.PageDarkOff
            : ServiceDefinition.PageDarkOn;

        var instance = settings.Services.FirstOrDefault(
            service => string.Equals(service.InstanceId, runtime.InstanceId, StringComparison.OrdinalIgnoreCase));

        if (instance is not null)
        {
            instance.PageDarkMode = next;
            store.Save(settings);
        }

        await runtime.SetPageDarkModeAsync(next);
        _log.Info($"page dark mode for '{runtime.InstanceId}' set to {next}");
    }

    /// <summary>
    /// Flips request blocking for the current tab. The choice is persisted per instance, so it
    /// survives a restart and stays scoped to that account. Turning it on never weakens the
    /// permanent allowlist (ADR 0004).
    /// </summary>
    private void ToggleBlocking()
    {
        if (_log is null || _registry?.Active is not { } runtime)
        {
            return;
        }

        var store = new SettingsStore(AppPaths.SettingsFile, _log);
        var settings = store.Load();

        var next = string.Equals(runtime.Blocking, "off", StringComparison.OrdinalIgnoreCase)
            ? "default"
            : "off";

        var instance = settings.Services.FirstOrDefault(
            service => string.Equals(service.InstanceId, runtime.InstanceId, StringComparison.OrdinalIgnoreCase));

        if (instance is not null)
        {
            instance.Blocking = next;
            store.Save(settings);
        }

        runtime.SetBlockingEnabled(!string.Equals(next, "off", StringComparison.OrdinalIgnoreCase), EnabledFilterPacks());
        _log.Info($"blocking for '{runtime.InstanceId}' set to {next}");
    }

    /// <summary>
    /// Activates or deactivates a non-service plugin. The page-dark theme plugin owns the
    /// global darkening switch, so its state is mirrored onto every warm runtime immediately.
    /// </summary>
    private async Task SetPluginEnabledAsync(string pluginId, bool enabled)
    {
        if (_log is null)
        {
            return;
        }

        var store = new SettingsStore(AppPaths.SettingsFile, _log);
        var settings = store.Load();
        PluginState.SetEnabled(settings, pluginId, enabled);
        store.Save(settings);

        if (string.Equals(pluginId, PluginState.PageDarkId, StringComparison.OrdinalIgnoreCase))
        {
            ServiceRuntime.PageDarkeningEnabled = enabled;
            var dark = ThemeDecision.IsDark(_themeMode, SystemTheme.UsesLight());

            foreach (var id in _registry?.Order ?? Array.Empty<string>())
            {
                if (_registry?.Find(id) is { } runtime)
                {
                    await runtime.ApplyThemeAsync(dark);
                }
            }
        }

        if (string.Equals(pluginId, PluginState.CatppuccinId, StringComparison.OrdinalIgnoreCase))
        {
            ApplyTheme();
        }

        // A filter plugin owns the packs; activating or deactivating it must reach every warm
        // runtime now, not at the next restart.
        if (_filterPacks.Any(pack => string.Equals(pack.Id, pluginId, StringComparison.OrdinalIgnoreCase)))
        {
            var packs = EnabledFilterPacks();

            foreach (var id in _registry?.Order ?? Array.Empty<string>())
            {
                _registry?.Find(id)?.SetFilterPacks(packs);
            }
        }

        _log.Info($"plugin '{pluginId}' {(enabled ? "activated" : "deactivated")}");
    }

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
