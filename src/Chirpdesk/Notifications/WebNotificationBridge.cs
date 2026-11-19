using System.IO;
using System.Text.Json;
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
    private readonly string _serviceId;
    private readonly string _serviceName;
    private readonly string? _definitionId;
    private readonly string? _accent;
    private readonly string? _allowedHost;
    private readonly DateTimeOffset _startedAt;

    public WebNotificationBridge(
        CoreWebView2 core,
        IToastSink toast,
        WarmUpGate gate,
        NotificationDedupe dedupe,
        ILog log,
        string serviceId,
        string serviceName,
        string? definitionId,
        string? accent,
        string? allowedHost,
        DateTimeOffset startedAt)
    {
        _core = core;
        _toast = toast;
        _gate = gate;
        _dedupe = dedupe;
        _log = log;
        _serviceId = serviceId;
        _serviceName = serviceName;
        _definitionId = definitionId;
        _accent = accent;
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
        var allowed = IsAllowedOrigin(e.Uri);

        if (allowed)
        {
            e.State = CoreWebView2PermissionState.Allow;
        }

        _log.Info(
            $"notification permission requested for '{_serviceName}' host={HostOf(e.Uri) ?? "none"} allowed={allowed}");
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

        if (body.Length == 0 && string.Equals(_definitionId, "whatsapp", StringComparison.Ordinal))
        {
            // WhatsApp's service worker sends the sender but no body. The chat list on the
            // page already shows the last message, so use it instead of a generic toast.
            _ = ShowWithPagePreviewAsync(title);
            return;
        }

        ShowToast(title, body, notification.IconUri);
    }

    private async Task ShowWithPagePreviewAsync(string title)
    {
        string? preview = null;

        try
        {
            // The notification lands a moment before the chat list repaints.
            await Task.Delay(350);
            preview = await ReadPreviewAsync(title);
            if (preview is null)
            {
                await Task.Delay(700);
                preview = await ReadPreviewAsync(title);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException)
        {
            _log.Warn($"page preview unavailable for '{_serviceName}': {ex.GetType().Name}");
        }

        if (preview is not null)
        {
            _log.Info($"page preview used for '{_serviceName}' (body length {preview.Length})");
        }

        ShowToast(title, preview ?? string.Empty, null);
    }

    private async Task<string?> ReadPreviewAsync(string title)
    {
        var raw = await _core.ExecuteScriptAsync(PreviewScript);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        var rows = JsonSerializer.Deserialize<List<PreviewRow>>(raw, JsonOptions);
        if (rows is null || rows.Count == 0)
        {
            return null;
        }

        var candidates = rows
            .Select(row => new PreviewCandidate(row.N, row.B))
            .ToList();
        return NotificationPreviewSelector.Select(title, candidates);
    }

    private void ShowToast(string title, string body, string? iconUri)
    {
        Shown++;
        _log.Info($"toast shown for '{_serviceName}' (#{Shown}), title length {title.Length}, body length {body.Length}");
        _toast.Show(new ToastMessage(title, body, LocalIcon(iconUri) ?? ToastIcons.ForService(_definitionId, _serviceName, _accent), _serviceId));
    }

    private sealed record PreviewRow(string? N, string? B);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// WhatsApp Web's chat list, as unread rows with the name and last message. Returns data,
    /// never markup; the host matches the row to the notification title.
    /// </summary>
    private const string PreviewScript = """
        (function () {
          try {
            var out = [];
            var rows = document.querySelectorAll('#pane-side [role="row"]');
            for (var i = 0; i < rows.length && out.length < 12; i++) {
              var row = rows[i];
              if (!row.querySelector('[data-testid="icon-unread-count"]')) { continue; }
              var nameEl = row.querySelector('[data-testid="cell-frame-title"] span');
              var bodyEl = row.querySelector('[data-testid="last-msg-status"]')
                || row.querySelector('[data-testid="chat-msg-symbol"]')
                || row.querySelector('[data-testid="cell-frame-secondary"] span[dir="auto"]');
              var body = bodyEl ? (bodyEl.textContent || '') : '';
              if (!body.trim()) { continue; }
              out.push({ n: nameEl ? (nameEl.textContent || '') : '', b: body });
            }
            return out;
          } catch (e) { return []; }
        })()
        """;

    /// <summary>IconUri is a string; only a local file can be attached to a toast.</summary>
    private static string? LocalIcon(string? iconUri) =>
        !string.IsNullOrWhiteSpace(iconUri)
        && Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)
        && uri.IsFile
        ? uri.LocalPath
        : null;

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

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
