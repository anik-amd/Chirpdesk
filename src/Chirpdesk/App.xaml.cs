using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Diagnostics;
using Chirpdesk.Hosting;
using Chirpdesk.Logging;
using Chirpdesk.Notifications;
using Hardcodet.Wpf.TaskbarNotification;

namespace Chirpdesk;

public partial class App : Application
{
    private FileLog? _log;
    private SingleInstance? _singleInstance;
    private HostWindow? _host;
    private TaskbarIcon? _tray;

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

        _tray = TrayIconFactory.Create(OpenHost, Quit);
        _host.Attach(_tray);

        _log.Info("started hidden");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
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

    private void OpenHost() => _host?.ShowForUser();

    private void Terminate(int exitCode)
    {
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
