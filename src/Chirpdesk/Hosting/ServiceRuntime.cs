using System.IO;
using System.Windows;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Time;
using Chirpdesk.Notifications;
using Microsoft.Web.WebView2.Wpf;

namespace Chirpdesk.Hosting;

/// <summary>
/// One running service: its profile, its webview, its notification policy.
/// A definition is shared; this object exists per instance.
/// </summary>
internal sealed class ServiceRuntime : IAsyncDisposable
{
    private readonly HostWindow _host;
    private readonly ServiceInstance _instance;
    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly IToastSink _toast;
    private readonly DateTimeOffset _startedAt;
    private readonly WebView2 _view = new();

    private WebNotificationBridge? _bridge;
    private WarmUpGate? _gate;
    private NotificationDedupe? _fallbackDedupe;
    private int _lastUnread;
    private bool _darkTheme;
    private bool _disposed;

    /// <summary>
    /// Routes <c>ServiceWorkerRegistration.showNotification</c> through the page's own
    /// <c>new Notification()</c>. WebView2 does not surface service-worker notifications, so
    /// without this a site that notifies from a registration gets no toast at all. The
    /// override only applies when the call is made from page context; a worker calling it
    /// inside its own scope is invisible to every host-side hook, and that site falls back to
    /// the unread-title toast instead.
    /// </summary>
    private const string NotificationShimScript = """
        (function () {
          function install() {
            try {
              if (!self.ServiceWorkerRegistration || !ServiceWorkerRegistration.prototype) { return; }
              var proto = ServiceWorkerRegistration.prototype;
              if (proto.__chirpdeskShim || typeof proto.showNotification !== 'function') { return; }
              var original = proto.showNotification;
              proto.showNotification = function (title, options) {
                try {
                  new Notification(title, options || {});
                  return Promise.resolve();
                } catch (e) {
                  return original.apply(this, arguments);
                }
              };
              Object.defineProperty(proto, '__chirpdeskShim', { value: true });
            } catch (e) { }
          }
          install();
          document.addEventListener('DOMContentLoaded', install);
        })();
        """;

    /// <summary>
    /// Dark mode has to reach the page, not just the window. Two levers, in order: the
    /// profile's <c>prefers-color-scheme</c> (free and correct where the site supports it),
    /// then a filter on the root element for sites that do not. Images, video and canvases
    /// are inverted back so they keep their real colours.
    /// </summary>
    private const string ThemeScript = """
        (function () {
          function install() {
            if (!document.documentElement) { return false; }
            if (document.getElementById('cd-theme-style')) { return true; }
            var style = document.createElement('style');
            style.id = 'cd-theme-style';
            style.textContent =
              'html.cd-dark{background:#111214 !important;filter:invert(1) hue-rotate(180deg);}' +
              'html.cd-dark img,html.cd-dark video,html.cd-dark canvas,html.cd-dark svg,html.cd-dark iframe{filter:invert(1) hue-rotate(180deg) !important;}';
            (document.head || document.documentElement).appendChild(style);
            return true;
          }
          window.__chirpdeskTheme = function (dark) {
            try {
              if (!install()) { return 'no-document'; }
              var root = document.documentElement;
              if (!dark) { root.classList.remove('cd-dark'); return 'light'; }
              var bg = document.body ? getComputedStyle(document.body).backgroundColor : '';
              var m = bg.match(/rgba?\(\s*(\d+),\s*(\d+),\s*(\d+)/);
              var lum = m ? (0.2126 * +m[1] + 0.7152 * +m[2] + 0.0722 * +m[3]) / 255 : 1;
              root.classList.add('cd-dark');
              return 'dark:' + lum.toFixed(2);
            } catch (e) { return 'error:' + e; }
          };
          document.addEventListener('DOMContentLoaded', install);
        })();
        """;

    private ServiceRuntime(
        HostWindow host,
        ServiceInstance instance,
        ILog log,
        IClock clock,
        IToastSink toast,
        DateTimeOffset startedAt)
    {
        _host = host;
        _instance = instance;
        _log = log;
        _clock = clock;
        _toast = toast;
        _startedAt = startedAt;
    }

    public string? NotificationMode { get; private set; }

    public string InstanceId => _instance.InstanceId;

    public int ReceivedNotifications => _bridge?.Received ?? 0;

    public static async Task<ServiceRuntime> CreateAsync(
        HostWindow host,
        ServiceInstance instance,
        ILog log,
        IClock clock,
        IToastSink toast,
        DateTimeOffset startedAt)
    {
        var runtime = new ServiceRuntime(host, instance, log, clock, toast, startedAt);
        await runtime.InitializeAsync();
        return runtime;
    }

    /// <summary>Warm service takes the window. Size is the only thing that changes.</summary>
    public void Activate()
    {
        _view.Width = double.NaN;
        _view.Height = double.NaN;
        _view.HorizontalAlignment = HorizontalAlignment.Stretch;
        _view.VerticalAlignment = VerticalAlignment.Stretch;
        _view.Visibility = Visibility.Visible;
    }

    /// <summary>Warm but not on screen: 1×1 keeps the renderer's composition target alive.</summary>
    public void Park()
    {
        _view.Width = 1;
        _view.Height = 1;
        _view.HorizontalAlignment = HorizontalAlignment.Left;
        _view.VerticalAlignment = VerticalAlignment.Top;
        _view.Visibility = Visibility.Visible;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_bridge is not null)
        {
            _bridge.Detach();
        }

        _host.ClearServiceContent();
        _view.Dispose();

        await Task.CompletedTask;
    }

    private async Task InitializeAsync()
    {
        Park();
        _host.SetServiceContent(_view);

        var profileId = _instance.ProfileId ?? _instance.InstanceId;
        var environment = await WebViewHost.CreateEnvironmentAsync(profileId);
        await _view.EnsureCoreWebView2Async(environment);

        var core = _view.CoreWebView2;

        _gate = new WarmUpGate(_clock, TimeSpan.FromSeconds(5));
        _fallbackDedupe = new NotificationDedupe(_clock, TimeSpan.FromSeconds(3));
        _bridge = new WebNotificationBridge(
            core,
            _toast,
            _gate,
            new NotificationDedupe(_clock, TimeSpan.FromSeconds(3)),
            _log,
            _instance.InstanceId,
            _instance.Name,
            HostOf(_instance.Url),
            _startedAt);

        _bridge.Attach();
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                _gate.MarkReady();
                _ = LogPageCapabilitiesAsync(core);
                _ = ApplyThemeAsync(_darkTheme);
            }
            else
            {
                _log.Warn($"navigation failed for '{_instance.InstanceId}': {args.WebErrorStatus}");
            }
        };

        // Phase 0 records the capability rather than guessing: native when the event fires,
        // title-only when only DocumentTitleChanged is available.
        NotificationMode = "probing";

        if (!string.IsNullOrWhiteSpace(_instance.Url))
        {
            // Ask once, before the page loads: a site that checks Notification.permission
            // without calling requestPermission would otherwise never notify.
            await GrantNotificationsAsync(core);

            // Installed before navigation so it is in place before any app script runs.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(NotificationShimScript);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ThemeScript);
            core.Navigate(_instance.Url);
        }
    }

    private async Task GrantNotificationsAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        if (!Uri.TryCreate(_instance.Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        try
        {
            await core.Profile.SetPermissionStateAsync(
                Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Notifications,
                origin,
                Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow);

            _log.Info($"notifications pre-granted for '{_instance.InstanceId}'");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            // Fail open: the PermissionRequested handler still answers prompts.
            _log.Warn($"could not pre-grant notifications for '{_instance.InstanceId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the two facts that decide the notification mode: whether the page may notify at
    /// all, and whether a service worker controls it. WebView2's NotificationReceived covers
    /// non-persistent (page) notifications only, so a page notified through its worker is the
    /// title-only case, and phase 0 needs to know which one WhatsApp is.
    /// </summary>
    private async Task LogPageCapabilitiesAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        try
        {
            var permission = await core.ExecuteScriptAsync("Notification.permission");
            var controlled = await core.ExecuteScriptAsync("!!navigator.serviceWorker.controller");
            _log.Info(
                $"page capability for '{_instance.InstanceId}': permission={permission.Trim('"')} serviceWorker={controlled.Trim('"')}");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log.Warn($"could not read page capability for '{_instance.InstanceId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Raises a notification from inside the real service page. That tests the whole pipeline
    /// (origin check, gate, dedupe, toast) without depending on whether the service itself has
    /// its own notifications switched on.
    /// </summary>
    public async Task<bool> RaiseProbeNotificationAsync(TimeSpan timeout)
    {
        var core = _view.CoreWebView2;
        var deadline = DateTimeOffset.UtcNow + timeout;

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                var state = await core.ExecuteScriptAsync("document.readyState");
                if (state.Contains("complete", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(500);
            }

            // The warm-up gate refuses anything in the first five seconds of a session on
            // purpose (R4: no toast storm after login), so a probe has to wait it out.
            await Task.Delay(TimeSpan.FromSeconds(6));

            // A unique tag every time so the dedupe cannot swallow a second probe.
            const string Script = """
                (function () {
                  try {
                    new Notification('Chirpdesk probe', { body: 'pipeline test', tag: 'probe-' + Date.now() });
                    return 'ok';
                  } catch (e) {
                    return 'error:' + e;
                  }
                })()
                """;

            var result = await core.ExecuteScriptAsync(Script);
            return result.Contains("ok", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log.Warn($"probe notification failed for '{_instance.InstanceId}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Applies dark or light to the page. <c>PreferredColorScheme</c> is the clean lever and
    /// works without touching the DOM where the site supports it; the filter fallback is what
    /// makes dark actually dark on sites that ignore the media query. The returned string is
    /// logged so the choice is observable, not assumed.
    /// </summary>
    public async Task ApplyThemeAsync(bool dark)
    {
        _darkTheme = dark;

        if (_disposed)
        {
            return;
        }

        try
        {
            var core = _view.CoreWebView2;
            if (core is null)
            {
                return;
            }

            core.Profile.PreferredColorScheme = dark
                ? Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark
                : Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;

            var argument = dark ? "true" : "false";
            var script = "window.__chirpdeskTheme ? window.__chirpdeskTheme(" + argument + ") : ''";
            var result = await core.ExecuteScriptAsync(script);

            var mode = dark ? "dark" : "light";
            var outcome = result.Trim('"');
            _log.Info($"web theme for '{_instance.InstanceId}': mode={mode} result={outcome}");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log.Warn($"could not apply the page theme for '{_instance.InstanceId}': {ex.Message}");
        }
    }

    /// <summary>Counts only: a title is user content and never goes to the log.</summary>
    private void OnDocumentTitleChanged(object? sender, object e)
    {
        var count = Core.Unread.UnreadParser.Parse(_view.CoreWebView2.DocumentTitle, _instance.UnreadRegex ?? @"\((\d+)\)");
        _gate?.ObserveTitle(count);

        var previous = _lastUnread;
        _lastUnread = count;

        if (count == previous)
        {
            return;
        }

        _log.Info($"unread {count} for '{_instance.InstanceId}'");

        if (count > previous && count > 0 && _gate?.IsOpen == true)
        {
            _ = ShowTitleOnlyFallbackAsync();
        }
    }

    /// <summary>
    /// The safety net that makes an arbitrary site usable: if the unread count rose and no
    /// page notification arrived shortly after, raise a generic toast. A site using a service
    /// worker from its own scope, or one whose notification API WebView2 does not surface,
    /// then degrades to "New message in X" instead of silence. When the real notification
    /// does arrive, the bridge's shown counter changes first and this returns quietly.
    /// </summary>
    private async Task ShowTitleOnlyFallbackAsync()
    {
        var shownBefore = _bridge?.Shown ?? 0;

        await Task.Delay(TimeSpan.FromSeconds(3));

        if (_disposed
            || (_bridge?.Shown ?? 0) != shownBefore
            || (_gate?.ShouldSuppress() ?? true)
            || _fallbackDedupe is null
            || !_fallbackDedupe.ShouldShow("title-only", _instance.InstanceId, string.Empty))
        {
            return;
        }

        _log.Info($"title-only fallback toast for '{_instance.InstanceId}'");
        _toast.Show(new ToastMessage(_instance.Name, "New message", null, _instance.InstanceId));
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
}
