using System.IO;
using System.Security;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Chirpdesk.Notifications;

/// <summary>
/// WinRT toasts under the app's own AUMID, so they are titled "Chirpdesk" and group together.
/// Every failure is logged and swallowed: a missing toast must never break the host.
/// </summary>
internal sealed class ToastService : IToastSink
{
    private readonly ILog _log;
    private readonly Action<string?>? _onActivated;
    private ToastNotifier? _notifier;

    public ToastService(ILog log, Action<string?>? onActivated = null)
    {
        _log = log;
        _onActivated = onActivated;
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                _ = Notifier;
                return true;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                _log.Warn($"toasts unavailable: {ex.Message}");
                return false;
            }
        }
    }

    private ToastNotifier Notifier => _notifier ??= ToastNotificationManager.CreateToastNotifier(Aumid.AppId);

    public void Show(ToastMessage message)
    {
        try
        {
            var xml = BuildXml(message);
            var document = new XmlDocument();
            document.LoadXml(xml);

            var toast = new ToastNotification(document);
            toast.Activated += (_, _) =>
            {
                _log.Info($"toast activated by the user for '{message.ServiceId}'");
                _onActivated?.Invoke(message.ServiceId);
            };
            toast.Dismissed += (_, args) => _log.Info($"toast dismissed ({args.Reason}) for '{message.ServiceId}'");
            toast.Failed += (_, args) => _log.Warn($"toast failed: {args.ErrorCode}");

            Notifier.Show(toast);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _log.Error("could not show a toast", ex);
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException
            or IOException or System.Runtime.InteropServices.COMException or FileNotFoundException;

    private static string BuildXml(ToastMessage message)
    {
        var image = string.Empty;
        if (!string.IsNullOrWhiteSpace(message.IconPath) && File.Exists(message.IconPath))
        {
            image = $"""<image placement="appLogoOverride" hint-crop="circle" src="{Escape(new Uri(message.IconPath).AbsoluteUri)}"/>""";
        }

        return $"""
            <toast activationType="foreground" launch="{Escape(message.ServiceId ?? string.Empty)}">
              <visual>
                <binding template="ToastGeneric">
                  <text>{Escape(message.Title)}</text>
                  <text>{Escape(message.Body)}</text>
                  {image}
                </binding>
              </visual>
            </toast>
            """;
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
