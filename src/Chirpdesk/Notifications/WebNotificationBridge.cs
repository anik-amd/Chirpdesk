using System.IO;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Time;
using Microsoft.Web.WebView2.Core;

namespace Chirpdesk.Notifications;

/// <summary>
/// Turns a page notification into a toast, with every policy step in front of it:
/// origin check, boot cutoff, warm-up gate, dedupe. Policy objects come from Core and are
/// unit-tested; this class only wires them to WebView2 events.
/// </summary>
internal sealed class WebNotificationBridge
{
    private readonly CoreWebView2 _core;
    private readonly IToastSink _toast;
    private readonly WarmUpGate _gate;
    private readonly NotificationDedupe _dedupe;
    private readonly ILog _log;
    private readonly string _serviceName;
    private readonly string? _allowedHost;
    private readonly DateTimeOffset _startedAt;

    public WebNotificationBridge(
        CoreWebView2 core,
        IToastSink toast,
        WarmUpGate gate,
        NotificationDedupe dedupe,
        ILog log,
        string serviceName,
        string? allowedHost,
        DateTimeOffset startedAt)
    {
        _core = core;
        _toast = toast;
        _gate = gate;
        _dedupe = dedupe;
        _log = log;
        _serviceName = serviceName;
        _allowedHost = allowedHost;
        _startedAt = startedAt;
    }

    public int Received { get; private set; }

    public int Shown { get; private set; }

    /// <summary>True once a real page notification has reached us — the capability probe's answer.</summary>
    public bool CapabilityConfirmed => Received > 0;

    public void Attach()
    {
        _core.PermissionRequested += OnPermissionRequested;
        _core.NotificationReceived += OnNotificationReceived;
    }

    public void Detach()
    {
        _core.PermissionRequested -= OnPermissionRequested;
        _core.NotificationReceived -= OnNotificationReceived;
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind != CoreWebView2PermissionKind.Notifications)
        {
            return;
        }

        // Phase 0: grant notifications for the service's own origin only. The full per-origin
        // policy (camera, mic, geolocation denial) lands in phase 2.
        if (IsAllowedOrigin(e.Uri))
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    private void OnNotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        var notification = e.Notification;

        if (!IsAllowedOrigin(e.SenderOrigin))
        {
            _log.Warn($"notification from an unexpected origin dropped for '{_serviceName}'");
            return;
        }

        e.Handled = true;
        Received++;

        if (Received == 1)
        {
            // The phase-0 capability record, without any message content.
            _log.Info($"notification capability: native for '{_serviceName}'");
        }

        // Boot cutoff: a reconnect replays days of notifications; none of that is new.
        // A site is free to leave `timestamp` at its default (epoch) — that is "unknown",
        // not "old", and dropping on it would swallow every WhatsApp message.
        var created = NotificationTime.Normalize(notification.Timestamp);
        if (created > DateTimeOffset.UnixEpoch && created < _startedAt)
        {
            _log.Info($"notification from before start dropped for '{_serviceName}' ({created:o} < {_startedAt:o})");
            return;
        }

        if (_gate.ShouldSuppress())
        {
            _log.Info($"notification suppressed during warm-up for '{_serviceName}'");
            return;
        }

        var title = string.IsNullOrWhiteSpace(notification.Title) ? _serviceName : notification.Title;
        var body = notification.Body ?? string.Empty;

        if (!_dedupe.ShouldShow(notification.Tag ?? string.Empty, title, body))
        {
            _log.Info($"duplicate notification dropped for '{_serviceName}'");
            return;
        }

        Shown++;
        _log.Info($"toast shown for '{_serviceName}' (#{Shown}), title length {title.Length}");
        _toast.Show(new ToastMessage(title, body, LocalIcon(notification.IconUri), _serviceName));
    }

    /// <summary>IconUri is a string; only a local file can be attached to a toast.</summary>
    private static string? LocalIcon(string? iconUri) =>
        !string.IsNullOrWhiteSpace(iconUri)
        && Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)
        && uri.IsFile
        ? uri.LocalPath
        : null;

    private bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || _allowedHost is null)
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, _allowedHost, StringComparison.OrdinalIgnoreCase);
    }
}
