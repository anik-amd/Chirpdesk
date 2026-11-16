using System.Diagnostics;
using System.IO;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Time;
using Chirpdesk.Hosting;
using Chirpdesk.Interop;
using Chirpdesk.Notifications;
using Windows.UI.Notifications;

namespace Chirpdesk.Diagnostics;

/// <summary>
/// The automated half of the phase-0 gate: everything a machine can judge, headless, no login.
/// The human half (QR scan, a real message, a visible flash) stays in the checklist.
/// </summary>
internal sealed class SelfTest
{
    private readonly StartupOptions _options;
    private readonly HostWindow _host;
    private readonly ILog _log;
    private readonly List<string> _lines = [];
    private int _failures;

    private SelfTest(StartupOptions options, HostWindow host, ILog log) =>
        (_options, _host, _log) = (options, host, log);

    public static async Task<int> RunAsync(StartupOptions options, HostWindow host, ILog log)
    {
        var test = new SelfTest(options, host, log);
        return await test.ExecuteAsync().ConfigureAwait(true);
    }

    private async Task<int> ExecuteAsync()
    {
        // The window must be created before anything inspects it, and the report always
        // reflects every check that ran, even when one of them throws.
        CheckWebViewRuntime();

        Guarded("settings", CheckSettings);
        Guarded("app-identity", CheckAppIdentity);
        Guarded("tray", CheckTray);
        Guarded("window-hidden", CheckWindowIsHidden);
        Guarded("single-instance", CheckSingleInstance);

        try
        {
            var environmentOk = await CheckEnvironmentAsync().ConfigureAwait(true);
            Check("environment", environmentOk, environmentOk ? "created under the profiles root" : "could not create a profile environment");
        }
        catch (Exception ex)
        {
            Check("environment", false, ex.Message);
            _log.Error("environment check crashed", ex);
        }

        var probeDirectory = Path.Combine(AppPaths.ProfilesRoot, "__probe");
        try
        {
            var probe = await HiddenWebViewProbe.RunAsync(_host, _log, probeDirectory, _options.Soak).ConfigureAwait(true);

            Check(
                "hidden-warm",
                probe.TimerHealthy,
                probe.Error is null
                    ? $"timers={probe.Ticks}/{probe.ExpectedTicks} ready={probe.ReadyState}"
                    : probe.Error);

            Check(
                "notification-capability",
                probe.NotificationEventFired,
                $"event={probe.NotificationEventFired} script={probe.NotificationScriptRan} permission={probe.NotificationPermission}");
        }
        catch (Exception ex)
        {
            Check("hidden-warm", false, ex.Message);
            _log.Error("hidden-warm check crashed", ex);
        }

        CheckToastShown();
        await Task.Delay(1200).ConfigureAwait(true);

        var released = await TryDeleteAsync(probeDirectory).ConfigureAwait(true);
        Check("profile-released", released, released ? "no process is holding the profile" : "profile still locked after dispose");

        WriteReport();

        return _failures == 0 ? 0 : 1;
    }

    private void Check(string name, bool ok, string detail, bool soft = false)
    {
        var status = ok ? "PASS" : soft ? "WARN" : "FAIL";
        if (!ok && !soft)
        {
            _failures++;
        }

        _lines.Add($"[{status}] {name} - {detail}");
        ConsoleBridge.Write($"[{status}] {name} - {detail}");
    }

    /// <summary>A throwing check fails one line, never the whole gate.</summary>
    private void Guarded(string name, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Check(name, false, ex.Message);
            _log.Error($"self-test '{name}' crashed", ex);
        }
    }

    private void CheckWebViewRuntime()
    {
        var version = WebViewHost.BrowserVersion;
        Check("webview2-runtime", version.Length > 0, version.Length > 0 ? version : "Evergreen runtime not found");
    }

    private void CheckSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chirpdesk-selftest", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new SettingsStore(path, _log);
            var settings = new AppSettings();
            settings.Services.Add(new ServiceInstance
            {
                InstanceId = "probe",
                Name = "Probe",
                Url = "https://probe.example/",
            });

            store.Save(settings);
            var loaded = store.Load();

            File.WriteAllText(path, "{ not json");
            var recovered = store.Load();

            Check(
                "settings",
                loaded.Services.Count == 1
                && loaded.Services[0].InstanceId == "probe"
                && recovered.Services.Count == 0
                && File.Exists(path + ".corrupt"),
                "round-trip, atomic replace and corrupt-file quarantine");
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            Check("settings", false, ex.Message);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private void CheckAppIdentity()
    {
        var shortcut = Aumid.ShortcutPath;
        var toasts = new ToastService(_log);
        var available = toasts.IsAvailable;
        var processId = Aumid.CurrentProcessAppUserModelId();

        Check(
            "app-identity",
            File.Exists(shortcut) && available && string.Equals(processId, Aumid.AppId, StringComparison.Ordinal),
            $"shortcut={File.Exists(shortcut)} notifier={available} aumid={processId}");
    }

    /// <summary>The tray icon is a real Win32 icon; if the library cannot create one nothing else shows.</summary>
    private void CheckTray()
    {
        var tray = TrayIconFactory.Create(open: static () => { }, hide: static () => { }, quit: static () => { });
        try
        {
            Check("tray", tray.IconSource is not null, $"icon={tray.IconSource is not null} tooltip={tray.ToolTipText}");
        }
        finally
        {
            tray.Dispose();
        }
    }

    private void CheckWindowIsHidden()
    {
        var hwnd = _host.Handle;
        var toolWindow = WindowChrome.IsToolWindow(hwnd);
        var appWindow = WindowChrome.IsAppWindow(hwnd);
        var visible = NativeMethods.IsWindowVisible(hwnd);
        var hasRect = NativeMethods.GetWindowRect(hwnd, out var rect);
        var offScreen = hasRect && rect.Left <= -30_000 && rect.Top <= -30_000;

        Check(
            "window-hidden",
            toolWindow && !appWindow && visible && offScreen,
            $"toolwindow={toolWindow} appwindow={appWindow} visible={visible} at={rect.Left},{rect.Top}");
    }

    /// <summary>Re-launches the app: it must see the mutex and exit without a window.</summary>
    private void CheckSingleInstance()
    {
        var start = StartInfo();
        if (start is null)
        {
            Check("single-instance", false, "could not resolve the executable path", soft: true);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        using var second = Process.Start(start);
        if (second is null)
        {
            Check("single-instance", false, "second process did not start", soft: true);
            return;
        }

        var exited = second.WaitForExit(5000);
        if (!exited)
        {
            try
            {
                second.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        Check(
            "single-instance",
            exited && second.ExitCode == 0 && second.MainWindowHandle == nint.Zero,
            $"exited={exited} code={(exited ? second.ExitCode : -1)} windows={second.MainWindowHandle != nint.Zero} in {stopwatch.ElapsedMilliseconds} ms");
    }

    private static async Task<bool> CheckEnvironmentAsync()
    {
        var directory = Path.Combine(AppPaths.ProfilesRoot, "__envcheck");

        try
        {
            await WebViewHost.CreateEnvironmentAsync("__envcheck", directory).ConfigureAwait(true);
            return Directory.Exists(directory);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private void CheckToastShown()
    {
        var toasts = new ToastService(_log);
        toasts.Show(new ToastMessage("Chirpdesk self-test", "If you can read this, toasts work.", null, "selftest"));

        var found = false;
        try
        {
            found = ToastNotificationManager.History.GetHistory(Aumid.AppId).Count > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            _log.Warn($"could not read toast history: {ex.Message}");
        }

        // Soft: Windows can have notifications switched off for this app, which is a user setting,
        // not a defect in Chirpdesk.
        Check("toast-shown", found, found ? "appeared in Action Center history" : "not in history (notifications may be off)", soft: true);
    }

    private static ProcessStartInfo? StartInfo()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return null;
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        return info;
    }

    private static async Task<bool> TryDeleteAsync(string directory)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!Directory.Exists(directory))
            {
                return true;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(250);
            }
        }

        return false;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is harmless.
        }
    }

    private void WriteReport()
    {
        var report = string.Join(Environment.NewLine, _lines) + Environment.NewLine;

        try
        {
            Directory.CreateDirectory(AppPaths.LogsRoot);
            File.WriteAllText(AppPaths.SelfTestReport, report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("could not write the self-test report", ex);
        }

        ConsoleBridge.Write(report);
    }
}
