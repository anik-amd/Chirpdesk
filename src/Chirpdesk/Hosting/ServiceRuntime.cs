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
    private bool _disposed;

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
        _bridge = new WebNotificationBridge(
            core,
            _toast,
            _gate,
            new NotificationDedupe(_clock, TimeSpan.FromSeconds(3)),
            _log,
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
            core.Navigate(_instance.Url);
        }
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        var count = Core.Unread.UnreadParser.Parse(_view.CoreWebView2.DocumentTitle, _instance.UnreadRegex ?? @"\((\d+)\)");
        _gate?.ObserveTitle(count);
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
}
