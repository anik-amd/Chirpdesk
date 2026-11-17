using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
    private ServiceRuntime? _service;
    private bool _quitting;
    private ThemeMode _themeMode = ThemeMode.System;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = StartupOptions.Parse(e.Args);
        _log = new FileLog(AppPaths.LogsRoot, "chirpdesk.log");
        ShippedDefinitions.Load(_log);

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

        InitializeTheme();

        _log.Info("started hidden");

        if (options.AddSite is not null)
        {
            _ = AddSiteAsync(options.AddSite, show: !options.Hidden);
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
        _tray?.Dispose();

        try
        {
            _service?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        var dark = ThemeDecision.IsDark(_themeMode, SystemTheme.UsesLight());

        var service = await ServiceRuntime.CreateAsync(
            _host!,
            instance,
            _log!,
            SystemClock.Instance,
            new ToastService(_log!, id => Dispatcher.Invoke(() => ShowHostFor(id))),
            DateTimeOffset.UtcNow,
            dark);
        _service = service;

        await service.ApplyThemeAsync(dark);

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

    /// <summary>
    /// The built-in add-any-site flow, command-line edition until the settings UI lands:
    /// <c>--add-site "My Chat|https://chat.example.com/"</c>. The instance it creates is a
    /// custom site (no definition), which is exactly what the UI will write in phase 2.
    /// </summary>
    private async Task AddSiteAsync(string spec, bool show)
    {
        var separator = spec.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator == spec.Length - 1)
        {
            _log?.Warn("--add-site needs Name|https://url");
            return;
        }

        var name = spec[..separator].Trim();
        var rawUrl = spec[(separator + 1)..].Trim();

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _log?.Warn("--add-site needs an absolute https url");
            return;
        }

        try
        {
            var store = new SettingsStore(AppPaths.SettingsFile, _log!);
            var settings = store.Load();

            var id = MakeInstanceId(name, settings);
            var instance = new ServiceInstance
            {
                InstanceId = id,
                Name = name,
                Url = uri.ToString(),
                WarmAtBoot = true,

                // A site added by URL is assumed to have no built-in dark theme; a shipped
                // definition says otherwise and wins once it exists.
                PageDarkMode = "auto",
            };

            settings.Services.Add(instance);
            store.Save(settings);
            _log?.Info($"added site '{id}'");

            await OpenInstanceAsync(instance, show, probe: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            _log?.Error("could not add the site", ex);
        }
    }

    /// <summary>Stable, path-safe id from a display name; collisions get a numeric suffix.</summary>
    private static string MakeInstanceId(string name, AppSettings settings)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-')
            .ToArray());

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');

        if (slug.Length == 0)
        {
            slug = "site";
        }

        if (!char.IsLetterOrDigit(slug[0]))
        {
            slug = "site-" + slug;
        }

        if (slug.Length > 32)
        {
            slug = slug[..32].TrimEnd('-');
        }

        var unique = slug;
        var suffix = 2;
        while (settings.Services.Exists(s => s.InstanceId == unique))
        {
            unique = $"{slug}-{suffix++}";
        }

        return unique;
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
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideHost();
        }
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

        if (_service is not null)
        {
            _ = _service.ApplyThemeAsync(dark);
        }
    }

    private void ShowHost()
    {
        if (_host is null)
        {
            return;
        }

        var wasVisible = _host.IsVisible;
        _service?.Activate();
        var foreground = _host.ShowForUser();

        _log?.Info($"window shown: wasVisible={wasVisible} foreground={foreground} visible={_host.IsVisible}");
    }

    /// <summary>
    /// Shows the window for the service a toast came from. Phase 0 has one warm service; the
    /// id is still read and logged, so an activation for a service that is not warm is never
    /// silently attributed to the wrong one, and phase 1's registry has a seam to fill.
    /// </summary>
    private void ShowHostFor(string? serviceId)
    {
        var id = string.IsNullOrWhiteSpace(serviceId) ? null : Uri.UnescapeDataString(serviceId);

        if (id is null)
        {
            ShowHost();
            return;
        }

        if (_service is not null && !string.Equals(id, _service.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Info($"activation for '{id}', which is not warm; showing '{_service.InstanceId}'");
        }
        else
        {
            _log?.Info($"activation for '{id}'");
        }

        ShowHost();
    }

    private void HideHost()
    {
        _service?.Park();
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
