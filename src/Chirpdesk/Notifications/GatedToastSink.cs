using System.IO;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Notifications;

/// <summary>
/// Reads mute/DND at toast time so a Settings change applies without recreating a webview.
/// Loading the small settings file per toast is cheaper than a stale cache and keeps the
/// policy in one place.
/// </summary>
internal sealed class GatedToastSink : IToastSink
{
    private readonly IToastSink _inner;
    private readonly SettingsStore _settings;
    private readonly ILog _log;

    public GatedToastSink(IToastSink inner, SettingsStore settings, ILog log)
    {
        _inner = inner;
        _settings = settings;
        _log = log;
    }

    public void Show(ToastMessage message)
    {
        try
        {
            var settings = _settings.Load();
            var instance = settings.Services.FirstOrDefault(
                service => string.Equals(service.InstanceId, message.ServiceId, StringComparison.OrdinalIgnoreCase));

            if (NotificationPolicy.ShouldSuppress(instance?.Mute ?? false, settings.DndUntil, DateTimeOffset.UtcNow))
            {
                _log.Info($"toast suppressed for '{message.ServiceId}' (mute/DND)");
                return;
            }

            _inner.Show(message);
        }
        catch (Exception ex) when (ex is IOException or InvalidSettingsException or UnauthorizedAccessException)
        {
            // Fail open: a broken settings read must not silence a real message.
            _log.Warn($"could not apply the toast policy: {ex.Message}");
            _inner.Show(message);
        }
    }
}
