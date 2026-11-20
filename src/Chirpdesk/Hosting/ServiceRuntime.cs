using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Chirpdesk.Core.Blocking;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Time;
using Chirpdesk.Interop;
using Chirpdesk.Notifications;
using Chirpdesk.Core.Userscripts;
using Microsoft.Web.WebView2.Wpf;

namespace Chirpdesk.Hosting;

/// <summary>A shortcut pressed inside the page that the host should handle instead.</summary>
public sealed class ServiceShortcutEventArgs : EventArgs
{
    public ServiceShortcutEventArgs(bool search, int direction, bool closeTab = false, bool tabs = false)
    {
        Search = search;
        Direction = direction;
        CloseTab = closeTab;
        Tabs = tabs;
    }

    /// <summary>True for Ctrl+K; false for the cycle shortcut.</summary>
    public bool Search { get; }

    /// <summary>True for the sticky Ctrl+Shift+K tab list.</summary>
    public bool Tabs { get; }

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
    private readonly Func<bool>? _suppressTitleFallback;
    private readonly Func<string, Task<IReadOnlyList<InjectedScript>>>? _userscripts;
    private readonly WebView2 _view = new();
    private IReadOnlyList<FilterPack> _filterPacks;

    private WebNotificationBridge? _bridge;
    private WarmUpGate? _gate;
    private NotificationDedupe? _fallbackDedupe;
    private RequestMatcher? _matcher;
    private int _lastUnread;
    private DateTimeOffset _lastUnreadChangeAt = DateTimeOffset.MinValue;
    private bool _unreadBaselinePending = true;
    private int _navigationToken;
    private int _blockedRequests;
    private bool _darkTheme;
    private bool _disposed;

    /// <summary>
    /// Host-owned link preview: hides Chromium's status bubble and shows the URL in the
    /// bottom-left corner, in a surface that follows the app's light/dark theme.
    /// </summary>
    private const string LinkTooltipScript = """
        (function () {
          if (window.__chirpdeskLinkTip) { return; }
          window.__chirpdeskLinkTip = true;

          function install() {
            if (!document.documentElement || !document.body) { return; }
            var tip = document.getElementById('chirpdesk-link-tip');
            if (!tip) {
              tip = document.createElement('div');
              tip.id = 'chirpdesk-link-tip';
              document.documentElement.appendChild(tip);
            }

            tip.style.cssText = 'position:fixed;z-index:2147483647;left:12px;bottom:12px;' +
              'pointer-events:none;opacity:0;transform:translateY(4px);' +
              'max-width:min(680px,calc(100vw - 24px));padding:6px 11px;' +
              'border:1px solid;border-radius:8px;' +
              'font:12px/1.4 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;' +
              'white-space:nowrap;overflow:hidden;text-overflow:ellipsis;' +
              'box-shadow:0 8px 24px rgba(0,0,0,.28);backdrop-filter:blur(10px) saturate(1.1);' +
              'transition:opacity .12s ease,transform .12s ease;';
            tip.setAttribute('aria-hidden', 'true');

            var titled = null;
            var savedTitle = null;

            function suppressNativeTitle(link, target) {
              var node = target && target.closest ? target.closest('[title]') : null;
              if (!node || (link.contains && !link.contains(node))) { node = link; }

              var value = node && node.getAttribute ? node.getAttribute('title') : null;
              if (!value) { return; }

              // Sites often put the destination in title="", which Chromium paints as a native
              // white tooltip next to ours. Keep human-readable titles, drop URL duplicates.
              if (/^https?:\/\//i.test(value) || value === link.href) {
                titled = node;
                savedTitle = value;
                node.removeAttribute('title');
              }
            }

            function restoreNativeTitle() {
              if (titled && savedTitle !== null) {
                titled.setAttribute('title', savedTitle);
              }
              titled = null;
              savedTitle = null;
            }

            function paint() {
              var dark = document.documentElement.getAttribute('data-chirpdesk-theme') === 'dark';
              tip.style.background = dark ? 'rgba(30,30,46,.95)' : 'rgba(250,248,242,.97)';
              tip.style.color = dark ? '#cdd6f4' : '#1d2422';
              tip.style.borderColor = dark ? '#45475a' : '#d3d0c6';
              tip.style.filter = document.documentElement.classList.contains('cd-dark')
                ? 'invert(1) hue-rotate(180deg)'
                : 'none';
            }

            function show(link, target) {
              if (!link || !link.href || link.href.indexOf('javascript:') === 0) { return; }
              hide();
              suppressNativeTitle(link, target);
              tip.textContent = link.href;
              paint();
              tip.style.opacity = '1';
              tip.style.transform = 'translateY(0)';
            }

            function hide() {
              tip.style.opacity = '0';
              tip.style.transform = 'translateY(4px)';
              restoreNativeTitle();
            }

            document.addEventListener('mouseover', function (event) {
              var link = event.target && event.target.closest
                ? event.target.closest('a[href]')
                : null;
              if (link) { show(link, event.target); } else { hide(); }
            }, true);

            document.addEventListener('mouseout', function (event) {
              if (!event.relatedTarget) { hide(); }
            }, true);

            document.addEventListener('mousedown', hide, true);
            window.addEventListener('scroll', hide, true);

            new MutationObserver(paint).observe(document.documentElement, {
              attributes: true,
              attributeFilter: ['data-chirpdesk-theme', 'class']
            });
          }

          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', install, { once: true });
          } else {
            install();
          }
        })();
        """;

    /// <summary>
    /// Routes <c>ServiceWorkerRegistration.showNotification</c> through the page's own
    /// <c>new Notification()</c>. WebView2 does not surface service-worker notifications, so
    /// without this a site that notifies from a registration gets no toast at all.
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
        bool darkTheme,
        IReadOnlyList<FilterPack> filterPacks,
        Func<bool>? suppressTitleFallback,
        Func<string, Task<IReadOnlyList<InjectedScript>>>? userscripts)
    {
        _host = host;
        _instance = instance;
        _log = log;
        _clock = clock;
        _toast = toast;
        _startedAt = startedAt;
        _darkTheme = darkTheme;
        _filterPacks = filterPacks;
        _suppressTitleFallback = suppressTitleFallback;
        _userscripts = userscripts;
        _view.MessageHook += OnMessageHook;
    }

    /// <summary>
    /// Raised when the page has focus and the user presses Ctrl+Tab, Ctrl+K or Ctrl+Shift+K,
    /// which the page would otherwise swallow as a browser shortcut. The app opens the one
    /// Command Bar.
    /// </summary>
    public event EventHandler<ServiceShortcutEventArgs>? ShortcutRequested;

    public string? NotificationMode { get; private set; }

    public string InstanceId => _instance.InstanceId;

    public string Name => _instance.Name;

    public string? DefinitionId => _instance.DefinitionId;

    public string? Url => _instance.Url;

    /// <summary><c>default</c> or <c>off</c> for this tab.</summary>
    public string Blocking => _instance.Blocking;

    /// <summary>The persisted page-dark policy: <c>on</c>, <c>auto</c>, <c>off</c> or unset.</summary>
    public string? PageDarkMode => _instance.PageDarkMode;

    /// <summary>
    /// Hides the page while the Command Bar is open without changing its layout bounds or
    /// resizing the WebView2. Collapsing would force a reflow; hiding the HWND keeps the page
    /// still so the captured backdrop and the restored page are the same frame.
    /// </summary>
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
        const int VkK = 0x4B;
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
        else if (key == VkK)
        {
            handled = true;
            ShortcutRequested?.Invoke(
                this,
                new ServiceShortcutEventArgs(
                    search: !NativeMethods.IsShiftDown(),
                    direction: 1,
                    tabs: NativeMethods.IsShiftDown()));
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

    /// <summary>Last parsed unread count; 0 when the title does not show one.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>True between Navigate and NavigationCompleted, including redirects.</summary>
    public bool IsNavigating { get; private set; }

    /// <summary>Raised when <see cref="IsNavigating"/> changes; the host filters by active tab.</summary>
    public event EventHandler? NavigationStateChanged;

    /// <summary>Raised when the unread count changes; the tray aggregates it.</summary>
    public event EventHandler? UnreadChanged;

    private void SetNavigating(bool navigating)
    {
        if (IsNavigating == navigating)
        {
            return;
        }

        IsNavigating = navigating;
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Requests cancelled by the blocking matcher for this runtime.</summary>
    public int BlockedRequests => Volatile.Read(ref _blockedRequests);

    /// <summary>Live per-tab switch used by the Command Bar; persists through the caller.</summary>
    public void SetBlockingEnabled(bool enabled, IReadOnlyList<FilterPack>? packs = null)
    {
        _instance.Blocking = enabled ? "default" : "off";

        if (packs is not null)
        {
            _filterPacks = packs;
        }

        _matcher = enabled ? new RequestMatcher(_instance.Url ?? string.Empty, _filterPacks) : null;
    }

    /// <summary>Replaces the packs after a filter plugin is activated or deactivated.</summary>
    public void SetFilterPacks(IReadOnlyList<FilterPack> packs)
    {
        _filterPacks = packs;

        if (!string.Equals(_instance.Blocking, "off", StringComparison.OrdinalIgnoreCase))
        {
            _matcher = new RequestMatcher(_instance.Url ?? string.Empty, _filterPacks);
        }
    }

    /// <summary>
    /// Master switch owned by the bundled <c>page-dark</c> theme plugin. When the plugin is
    /// deactivated no page is darkened, whatever the per-service policy says.
    /// </summary>
    public static bool PageDarkeningEnabled { get; set; } = true;

    public static async Task<ServiceRuntime> CreateAsync(
        HostWindow host,
        ServiceInstance instance,
        ILog log,
        IClock clock,
        IToastSink toast,
        DateTimeOffset startedAt,
        bool darkTheme,
        IReadOnlyList<FilterPack>? filterPacks = null,
        Func<bool>? suppressTitleFallback = null,
        Func<string, Task<IReadOnlyList<InjectedScript>>>? userscripts = null)
    {
        var runtime = new ServiceRuntime(
            host,
            instance,
            log,
            clock,
            toast,
            startedAt,
            darkTheme,
            filterPacks ?? [],
            suppressTitleFallback,
            userscripts);
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
    /// Re-applies the host size after a hidden window is shown. The controller otherwise keeps
    /// the 320×240 parked bounds until some unrelated resize arrives, which shows the page as a
    /// small card in the top-left while the rest of the window is blank.
    /// </summary>
    public void SyncToHost()
    {
        if (_view.Visibility != Visibility.Visible)
        {
            return;
        }

        _host.UpdateLayout();

        var width = _view.ActualWidth;
        var height = _view.ActualHeight;
        if (width < 1 || height < 1)
        {
            return;
        }

        // Nudging the explicit size is what makes the WebView2 controller recompute its bounds.
        _view.Width = width;
        _view.Height = height;
        _host.UpdateLayout();

        _view.Width = double.NaN;
        _view.Height = double.NaN;
        _host.UpdateLayout();

        _log.Info($"webview '{_instance.InstanceId}' synced to {width:0}x{height:0}");
    }

    /// <summary>
    /// Warm but not on screen. Keep the last full size: resizing a visible WebView2 before the
    /// window fades out is what produced the small page flash on close, and it also left the
    /// controller at 320×240 when the window was shown again.
    /// </summary>
    public void Park()
    {
        _view.HorizontalAlignment = HorizontalAlignment.Stretch;
        _view.VerticalAlignment = VerticalAlignment.Stretch;
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

        try
        {
            core.Settings.IsStatusBarEnabled = false;
        }
        catch (InvalidOperationException)
        {
            // Older runtimes: leave the native status bar as-is.
        }

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
            _instance.DefinitionId,
            _instance.Accent,
            HostOf(_instance.Url),
            _startedAt);

        _bridge.Attach();
        SetupBlocking(core);
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.NavigationStarting += (_, _) =>
        {
            _unreadBaselinePending = true;
            _lastUnreadChangeAt = _clock.UtcNow;
            _navigationToken++;
            _ = ClearUnreadBaselineAsync(_navigationToken);
            SetNavigating(true);
        };
        core.NavigationCompleted += (_, args) =>
        {
            SetNavigating(false);

            if (args.IsSuccess)
            {
                _gate.MarkReady();
                _ = LogPageCapabilitiesAsync(core);
                _ = ApplyThemeAsync(_darkTheme);
                _ = ApplyCosmeticFiltersAsync(core);
                _ = ApplyUserscriptsAsync(core);
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
            await core.AddScriptToExecuteOnDocumentCreatedAsync(LinkTooltipScript);
            SetNavigating(true);
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
    /// Applies the configured page theme. Page darkening always follows the app: it only runs
    /// while the app is dark, whatever the policy says. <c>on</c> and <c>auto</c> both allow the
    /// filter in dark mode (<c>auto</c> is the plugin default, <c>on</c> an explicit user
    /// override); <c>off</c> never touches the page. The returned string is logged so the choice
    /// is observable, not assumed.
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

            var mode = _instance.PageDarkMode;
            var forced = string.Equals(mode, ServiceDefinition.PageDarkOn, StringComparison.OrdinalIgnoreCase);
            var follow = string.Equals(mode, ServiceDefinition.PageDarkAuto, StringComparison.OrdinalIgnoreCase);
            var filterEnabled = PageDarkeningEnabled && dark && (forced || follow);

            core.Profile.PreferredColorScheme = dark
                ? Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark
                : Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;

            var argument = filterEnabled ? "true" : "false";
            var theme = dark ? "dark" : "light";
            var script = "document.documentElement.dataset.chirpdeskTheme='" + theme + "'; "
                + "window.__chirpdeskTheme ? window.__chirpdeskTheme(" + argument + ") : ''";
            var result = await core.ExecuteScriptAsync(script);

            var policy = forced ? "on" : follow ? "auto" : "off";
            var outcome = result.Trim('"');
            _log.Info($"web theme for '{_instance.InstanceId}': mode={(dark ? "dark" : "light")} pageDark={policy} result={outcome}");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log.Warn($"could not apply the page theme for '{_instance.InstanceId}': {ex.Message}");
        }
    }

    /// <summary>Session-level switch used by the Command Bar; updates the page immediately.</summary>
    public Task SetPageDarkModeAsync(string mode)
    {
        _instance.PageDarkMode = mode;
        return ApplyThemeAsync(_darkTheme);
    }

    /// <summary>
    /// Installs the request matcher. Any failure leaves blocking off for this runtime rather
    /// than half-applied: a chat client that cannot log in is worse than an ad (ADR 0004).
    /// </summary>
    private void SetupBlocking(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        try
        {
            core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _matcher = null;
            _log.Warn($"blocking unavailable for '{_instance.InstanceId}': {ex.Message}");
            return;
        }

        if (string.Equals(_instance.Blocking, "off", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info($"blocking for '{_instance.InstanceId}': off");
            return;
        }

        try
        {
            _matcher = new RequestMatcher(_instance.Url ?? string.Empty, _filterPacks);
            _log.Info(
                $"blocking for '{_instance.InstanceId}': on, packs={_filterPacks.Count}, skipped={_matcher.SkippedRules}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _matcher = null;
            _log.Warn($"blocking disabled for '{_instance.InstanceId}': {ex.Message}");
        }
    }

    private void OnWebResourceRequested(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            if (_matcher?.Evaluate(e.Request.Uri) != RequestVerdict.Block)
            {
                return;
            }

            var core = _view.CoreWebView2;
            if (core is null)
            {
                return;
            }

            e.Response = core.Environment.CreateWebResourceResponse(
                null,
                403,
                "Blocked",
                "Content-Type: text/plain");
            Interlocked.Increment(ref _blockedRequests);
        }
        catch
        {
            // Fail open: a matcher that throws must never break the page.
        }
    }

    /// <summary>
    /// Tier 2: hide promoted containers with a single injected stylesheet. Selectors that look
    /// like they could break out of the rule are dropped; a bad selector is a visual bug only.
    /// </summary>
    /// <summary>
    /// Runs the enabled third-party scripts whose @match covers this page. They execute in the
    /// page's own world after load; nothing is loaded in-process and nothing is signed.
    /// </summary>
    private async Task ApplyUserscriptsAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        if (_userscripts is null)
        {
            return;
        }

        IReadOnlyList<InjectedScript> scripts;
        try
        {
            scripts = await _userscripts(core.Source);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            _log.Warn($"userscripts unavailable for '{_instance.InstanceId}': {ex.GetType().Name}");
            return;
        }

        foreach (var script in scripts)
        {
            try
            {
                await core.ExecuteScriptAsync(UserscriptWrapper.Build(script));
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or System.Runtime.InteropServices.COMException)
            {
                _log.Warn($"userscript '{script.Name}' failed for '{_instance.InstanceId}': {ex.GetType().Name}");
            }
        }
    }

    /// <summary>Re-runs new scripts against the page that is already loaded; the wrapper dedupes.</summary>
    public Task ApplyUserscriptsAsync()
    {
        var core = _view.CoreWebView2;
        return core is null ? Task.CompletedTask : ApplyUserscriptsAsync(core);
    }

    private async Task ApplyCosmeticFiltersAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        var selectors = _matcher?.CosmeticSelectors;
        if (selectors is not { Count: > 0 })
        {
            return;
        }

        var safe = selectors
            .Where(selector => !selector.Contains('{')
                && !selector.Contains('}')
                && !selector.Contains("</", StringComparison.Ordinal))
            .ToArray();

        if (safe.Length == 0)
        {
            return;
        }

        try
        {
            var css = string.Join(",", safe) + "{display:none !important}";
            var script = "(function(){var s=document.createElement('style');s.textContent="
                + JsonSerializer.Serialize(css)
                + ";document.documentElement.appendChild(s);})()";
            await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            _log.Warn($"could not apply cosmetic filters for '{_instance.InstanceId}': {ex.Message}");
        }
    }

    /// <summary>
    /// A page that stays at zero unread for a minute has no restored backlog. Clear the
    /// baseline so the next real message can still produce a title-only toast.
    /// </summary>
    private async Task ClearUnreadBaselineAsync(int token)
    {
        await Task.Delay(TimeSpan.FromSeconds(60));

        if (_disposed || token != _navigationToken || _lastUnread != 0)
        {
            return;
        }

        _unreadBaselinePending = false;
    }

    /// <summary>Counts only: a title is user content and never goes to the log.</summary>
    private void OnDocumentTitleChanged(object? sender, object e)
    {
        var count = Core.Unread.UnreadParser.Parse(_view.CoreWebView2.DocumentTitle, _instance.UnreadRegex ?? @"\((\d+)\)");
        _gate?.ObserveTitle(count);

        var previous = _lastUnread;
        var now = _clock.UtcNow;
        var quietFor = now - _lastUnreadChangeAt;
        _lastUnread = count;
        UnreadCount = count;

        if (count != previous)
        {
            _lastUnreadChangeAt = now;
        }

        // A page load restores the site's existing unread backlog. The first non-zero title
        // after navigation is therefore a baseline, never a "new message"; a title that stays
        // empty for a minute also clears the baseline so a later real message still toasts.
        if (_unreadBaselinePending)
        {
            if (count > 0)
            {
                _unreadBaselinePending = false;
            }

            if (count != previous)
            {
                UnreadChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (count == previous)
        {
            return;
        }

        _log.Info($"unread {count} for '{_instance.InstanceId}'");
        UnreadChanged?.Invoke(this, EventArgs.Empty);

        if (count > previous && count > 0)
        {
            // Require a quiet title first: a reconnect restores the backlog in bursts
            // (5, 6, 7 in a few seconds), and only the first increase would be mistaken for a
            // message. The policy also asks the gate instead of reading IsOpen: a service that
            // never sends a page notification has nothing else to flip it open.
            var gateOpen = !(_gate?.ShouldSuppress() ?? true);

            if (TitleFallbackPolicy.ShouldStart(gateOpen, quietFor, _suppressTitleFallback?.Invoke() ?? false))
            {
                _ = ShowTitleOnlyFallbackAsync();
            }
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
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (!_disposed && DateTimeOffset.UtcNow < deadline)
        {
            if (!(_gate?.ShouldSuppress() ?? true))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        await Task.Delay(TimeSpan.FromSeconds(3));

        if (_disposed
            || UnreadCount <= 0
            || (_bridge?.Shown ?? 0) != shownBefore
            || (_gate?.ShouldSuppress() ?? true)
            || (_suppressTitleFallback?.Invoke() ?? false)
            || _fallbackDedupe is null
            || !_fallbackDedupe.ShouldShow("title-only", _instance.InstanceId, string.Empty))
        {
            return;
        }

        _log.Info($"title-only fallback toast for '{_instance.InstanceId}'");
        _toast.Show(new ToastMessage(
            _instance.Name,
            "New message",
            ToastIcons.ForService(_instance.DefinitionId, _instance.Name, _instance.Accent),
            _instance.InstanceId));
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
}
