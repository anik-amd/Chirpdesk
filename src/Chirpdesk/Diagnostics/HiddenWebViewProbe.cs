using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Chirpdesk.Core.Logging;
using Chirpdesk.Hosting;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Chirpdesk.Diagnostics;

internal sealed record ProbeResult(
    int Ticks,
    int ExpectedTicks,
    string ReadyState,
    string NotificationPermission,
    bool NotificationScriptRan,
    bool NotificationEventFired,
    string? Error)
{
    public double Rate => ExpectedTicks <= 0 ? 0 : (double)Ticks / ExpectedTicks;

    public bool TimerHealthy => Error is null && ReadyState == "complete" && Rate >= 0.8;
}

/// <summary>
/// Proves R2 without WhatsApp, a phone or three hours: park a real webview off-screen, let it
/// tick once a second, and measure whether Chromium throttled it. A throttled renderer would
/// show roughly one tick per minute instead of one per second.
/// The same page asks for notification permission and raises a notification, which is the
/// capability probe — a real origin, the real permission path, the real event.
/// </summary>
internal sealed class HiddenWebViewProbe : IAsyncDisposable
{
    public const string ProbeOrigin = "https://probe.chirpdesk.invalid";

    private const string ProbeUrl = ProbeOrigin + "/";

    private const string ProbeHtml = """
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>probe</title></head>
        <body>
        <script>
          window.__ticks = 0;
          window.__notified = false;
          setInterval(function () { window.__ticks++; }, 1000);
          window.__permission = 'unknown';
          setTimeout(async function () {
            try {
              if (!('Notification' in window)) { window.__permission = 'unsupported'; return; }
              var p = Notification.permission;
              if (p === 'default') { p = await Notification.requestPermission(); }
              window.__permission = p;
              if (p === 'granted') {
                new Notification('probe', { body: 'probe', tag: 'selftest' });
                window.__notified = true;
              }
            } catch (e) { window.__permission = 'error:' + e; }
          }, 1500);
        </script>
        </body>
        </html>
        """;

    private readonly HostWindow _host;
    private readonly ILog _log;
    private readonly string _profileDirectory;
    private readonly WebView2 _view = new();
    private readonly TaskCompletionSource<bool> _navigated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Stream? _responseBody;
    private int _notificationEvents;
    private bool _disposed;

    public HiddenWebViewProbe(HostWindow host, ILog log, string profileDirectory)
    {
        _host = host;
        _log = log;
        _profileDirectory = profileDirectory;
    }

    public static async Task<ProbeResult> RunAsync(
        HostWindow host,
        ILog log,
        string profileDirectory,
        TimeSpan duration)
    {
        await using var probe = new HiddenWebViewProbe(host, log, profileDirectory);
        return await probe.ExecuteAsync(duration);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _host.ClearServiceContent();
        _view.Dispose();
        _responseBody?.Dispose();

        // The probe deletes its profile folder afterwards; a cached environment pointing at a
        // deleted folder would fail the next run for no reason.
        WebViewHost.Forget(_profileDirectory);

        await Task.CompletedTask;
    }

    private async Task<ProbeResult> ExecuteAsync(TimeSpan duration)
    {
        try
        {
            Directory.CreateDirectory(_profileDirectory);

            var environment = await WebViewHost.CreateEnvironmentAsync("__probe", _profileDirectory);

            // Parked exactly like a warm service: visible, 1×1, off-screen window.
            _view.Width = 1;
            _view.Height = 1;
            _view.HorizontalAlignment = HorizontalAlignment.Left;
            _view.VerticalAlignment = VerticalAlignment.Top;
            _host.SetServiceContent(_view);

            await _view.EnsureCoreWebView2Async(environment);

            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.PermissionRequested += OnPermissionRequested;
            core.NotificationReceived += OnNotificationReceived;
            core.NavigationCompleted += (_, _) => _navigated.TrySetResult(true);
            core.AddWebResourceRequestedFilter(ProbeUrl + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;

            core.Navigate(ProbeUrl);
            await _navigated.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var stopwatch = Stopwatch.StartNew();
            await Task.Delay(duration);

            var ticks = await EvaluateIntAsync(core, "window.__ticks");
            var notified = await EvaluateStringAsync(core, "window.__notified");
            var permission = await EvaluateStringAsync(core, "window.__permission");
            var ready = await EvaluateStringAsync(core, "document.readyState");

            return new ProbeResult(
                ticks,
                (int)Math.Round(stopwatch.Elapsed.TotalSeconds),
                ready,
                permission,
                string.Equals(notified, "true", StringComparison.Ordinal),
                _notificationEvents > 0,
                null);
        }
        catch (Exception ex)
        {
            _log.Error("hidden-warm probe failed", ex);
            return new ProbeResult(0, 0, "error", "unknown", false, false, ex.Message);
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    private void OnNotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        _notificationEvents++;
        e.Handled = true;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = _view.CoreWebView2.Environment;
        _responseBody = new MemoryStream(Encoding.UTF8.GetBytes(ProbeHtml));
        e.Response = environment.CreateWebResourceResponse(
            _responseBody,
            200,
            "OK",
            "Content-Type: text/html; charset=utf-8");
    }

    private static async Task<int> EvaluateIntAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script);
        return int.TryParse(raw.Trim('"'), out var value) ? value : 0;
    }

    private static async Task<string> EvaluateStringAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script);
        return raw.Trim('"');
    }
}
