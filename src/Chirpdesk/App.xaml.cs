using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Time;
using Chirpdesk.Diagnostics;
using Chirpdesk.Hosting;
using Chirpdesk.Logging;
using Chirpdesk.Notifications;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = StartupOptions.Parse(e.Args);
        _log = new FileLog(AppPaths.LogsRoot, "chirpdesk.log");

        if (!SingleInstance.TryAcquire(out _singleInstance))
        {
            _log.Info("another instance is already running; exiting");
            Shutdown(0);
            return;
        }

        RegisterAppIdentity();

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

        _log.Info("started hidden");

        if (options.OpenService is not null)
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

            var service = await ServiceRuntime.CreateAsync(
                _host!,
                instance,
                _log!,
                SystemClock.Instance,
                new ToastService(_log!, _ => OpenHost()),
                DateTimeOffset.UtcNow);
            _service = service;

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
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidSettingsException)
        {
            _log?.Error($"could not open '{requestedId}'", ex);
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

    private void ShowHost()
    {
        _service?.Activate();
        _host?.ShowForUser();
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
            Aumid.SetProcessAppUserModelId();
            Aumid.EnsureShortcut(Environment.ProcessPath ?? throw new InvalidOperationException("no process path"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            // Fail open: the app still runs, but a toast may show "(unknown app)".
            _log?.Error("could not register the app identity; toasts may not appear", ex);
        }
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
