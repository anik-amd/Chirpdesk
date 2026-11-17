using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Time;
using Chirpdesk.Interop;
using Chirpdesk.Notifications;
using Microsoft.Web.WebView2.Wpf;

namespace Chirpdesk.Hosting;

/// <summary>A shortcut pressed inside the page that the host should handle instead.</summary>
public sealed class ServiceShortcutEventArgs : EventArgs
{
    public ServiceShortcutEventArgs(bool search, int direction, bool closeTab = false)
    {
        Search = search;
        Direction = direction;
        CloseTab = closeTab;
    }

    /// <summary>True for Ctrl+P; false for the cycle shortcut.</summary>
    public bool Search { get; }

    /// <summary>+1 for forward cycling, -1 for Shift+Tab.</summary>
    public int Direction { get; }

    /// <summary>True for Ctrl+W, which closes the active tab.</summary>
    public bool CloseTab { get; }
}

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
    /// <c>auto</c> only uses the second lever when the page still measures light: a site that
    /// already follows <c>prefers-color-scheme</c> must not have its own dark theme inverted
    /// back into a washed-out one (ADR 0010).
    /// </summary>
    private const string ThemeScript = """
        (function () {
          var LIGHT_THRESHOLD = 0.5;
          var SETTLE_MS = 250;
          var SETTLE_CHECKS = 40;

          var state = { dark: false, timer: 0, checks: 0 };

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

          // An early SPA paint can have a transparent body and report white while the site is
          // still deciding its theme; an opaque background is the first trustworthy sample.
          function measure() {
            var roots = [document.body, document.documentElement];
            for (var i = 0; i < roots.length; i++) {
              var el = roots[i];
              if (!el) { continue; }
              var bg = getComputedStyle(el).backgroundColor;
              var m = bg && bg.match(/rgba?\(\s*(\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?/);
              if (!m) { continue; }
              var alpha = m[4] === undefined ? 1 : parseFloat(m[4]);
              if (alpha < 0.5) { continue; }
              return {
                opaque: true,
                lum: (0.2126 * +m[1] + 0.7152 * +m[2] + 0.0722 * +m[3]) / 255
              };
            }
            return { opaque: false, lum: 1 };
          }

          function decide(final) {
            var root = document.documentElement;

            // Measure the page, not the filter we applied last time: the class overrides the
            // root background, so leaving it on would report our own colour back to us.
            root.classList.remove('cd-dark');

            if (!state.dark) { return 'light'; }

            var sample = measure();
            if (!sample.opaque && !final) { return null; }
            if (sample.opaque && sample.lum < LIGHT_THRESHOLD) {
              return 'native-dark:' + sample.lum.toFixed(2);
            }

            root.classList.add('cd-dark');
            return 'filtered:' + (sample.opaque ? sample.lum.toFixed(2) : 'transparent');
          }

          function tick() {
            var final = state.checks <= 1;
            decide(final);

            if (final) {
              state.timer = 0;
              return;
            }

            state.checks--;
            state.timer = setTimeout(tick, SETTLE_MS);
          }

          window.__chirpdeskTheme = function (dark) {
            try {
              if (!install()) { return 'no-document'; }
              if (state.timer) {
                clearTimeout(state.timer);
                state.timer = 0;
              }

              state.dark = !!dark;
              if (!state.dark) { return decide(false); }

              // Keep watching for a few seconds after the first answer: chat apps apply their
              // own night theme after hydration, and the filter must yield to it.
              state.checks = SETTLE_CHECKS;
              state.timer = setTimeout(tick, SETTLE_MS);

              var outcome = decide(false);
              return outcome === null ? 'pending' : outcome;
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
        DateTimeOffset startedAt,
        bool darkTheme)
    {
        _host = host;
        _instance = instance;
        _log = log;
        _clock = clock;
        _toast = toast;
        _startedAt = startedAt;
        _darkTheme = darkTheme;
        _view.MessageHook += OnMessageHook;
    }

    /// <summary>
    /// Raised when the page has focus and the user presses Ctrl+Tab or Ctrl+P, which the page
    /// would otherwise swallow as a browser shortcut. The app opens the one Command Bar.
    /// </summary>
    public event EventHandler<ServiceShortcutEventArgs>? ShortcutRequested;

    public string? NotificationMode { get; private set; }

    public string InstanceId => _instance.InstanceId;

    public string Name => _instance.Name;

    public string? DefinitionId => _instance.DefinitionId;

    public string? Url => _instance.Url;

    /// <summary>Hides the page while the Command Bar is open. Not disposed; state survives.</summary>
    public void CollapseForCommandBar() => _view.Visibility = Visibility.Collapsed;

    public void RestoreFromCommandBar()
    {
        _view.Visibility = Visibility.Visible;
        _host.UpdateLayout();
        FocusPage();
    }

    /// <summary>Puts keyboard focus back into the page without reloading it.</summary>
    public void FocusPage()
    {
        if (_view.Visibility != Visibility.Visible || !_host.IsVisible)
        {
            return;
        }

        _view.Focus();

        var handle = _view.Handle;
        if (handle != nint.Zero)
        {
            _ = NativeMethods.SetFocus(handle);
        }
    }

    /// <summary>
    /// Last frame of the page, used as the palette's backdrop after the HWND is collapsed.
    /// JPEG because it only sits behind a scrim and the capture is on the open path.
    /// </summary>
    public async Task<BitmapSource?> CapturePreviewAsync()
    {
        var core = _view.CoreWebView2;
        if (core is null || _view.Visibility != Visibility.Visible)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
        {
            _log.Warn($"could not capture the page for the command bar: {ex.Message}");
            return null;
        }
    }

    private nint OnMessageHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WmKeyDown = 0x0100;
        const int WmSysKeyDown = 0x0104;
        const int VkTab = 0x09;
        const int VkP = 0x50;
        const int VkW = 0x57;

        if ((msg != WmKeyDown && msg != WmSysKeyDown) || !NativeMethods.IsControlDown())
        {
            return nint.Zero;
        }

        var key = wParam.ToInt32();

        if (key == VkTab)
        {
            handled = true;
            ShortcutRequested?.Invoke(
                this,
                new ServiceShortcutEventArgs(search: false, NativeMethods.IsShiftDown() ? -1 : 1));
        }
        else if (key == VkP && !NativeMethods.IsShiftDown())
        {
            handled = true;
            ShortcutRequested?.Invoke(this, new ServiceShortcutEventArgs(search: true, direction: 1));
        }
        else if (key == VkW)
        {
            handled = true;
            ShortcutRequested?.Invoke(
                this,
                new ServiceShortcutEventArgs(search: false, direction: 1, closeTab: true));
        }

        return nint.Zero;
    }

    public int ReceivedNotifications => _bridge?.Received ?? 0;

    public static async Task<ServiceRuntime> CreateAsync(
        HostWindow host,
        ServiceInstance instance,
        ILog log,
        IClock clock,
        IToastSink toast,
        DateTimeOffset startedAt,
        bool darkTheme)
    {
        var runtime = new ServiceRuntime(host, instance, log, clock, toast, startedAt, darkTheme);
        await runtime.InitializeAsync();
        return runtime;
    }

    /// <summary>
    /// Warm service takes the window. Attaching the view is the part that actually switches:
    /// the previous active one is still in the tree from its own <see cref="Activate"/>, and
    /// resizing alone would leave the last-added page on screen.
    /// </summary>
    public void Activate()
    {
        _host.SetServiceContent(_view);

        _view.Width = double.NaN;
        _view.Height = double.NaN;
        _view.HorizontalAlignment = HorizontalAlignment.Stretch;
        _view.VerticalAlignment = VerticalAlignment.Stretch;
        _view.Visibility = Visibility.Visible;

        // Resizing from the parked 1×1 needs a layout pass before the renderer repaints,
        // otherwise the page stays blank until the next manual resize.
        _host.UpdateLayout();
    }

    /// <summary>
    /// Warm but not on screen: 320×240 keeps the renderer's composition target alive until
    /// the next <see cref="Activate"/> puts this view back in the window.
    /// </summary>
    public void Park()
    {
        _view.Width = 320;
        _view.Height = 240;
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

        _host.RemoveServiceContent(_view);
        _view.MessageHook -= OnMessageHook;
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
            // Before the first navigation, so the site's own prefers-color-scheme handling
            // runs with the right answer instead of being corrected after it painted.
            core.Profile.PreferredColorScheme = _darkTheme
                ? Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark
                : Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;

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

            var pageDark = dark
                && string.Equals(_instance.PageDarkMode, "auto", StringComparison.OrdinalIgnoreCase);

            var argument = pageDark ? "true" : "false";
            var script = "window.__chirpdeskTheme ? window.__chirpdeskTheme(" + argument + ") : ''";
            var result = await core.ExecuteScriptAsync(script);

            var mode = dark ? "dark" : "light";
            var policy = pageDark ? "auto" : "off";
            var outcome = result.Trim('"');
            _log.Info($"web theme for '{_instance.InstanceId}': mode={mode} pageDark={policy} result={outcome}");
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
