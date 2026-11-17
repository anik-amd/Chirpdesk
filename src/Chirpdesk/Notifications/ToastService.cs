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
    /// <summary>
    /// Windows raises <c>Activated</c> on the object that created the toast, not on the AUMID.
    /// If the <see cref="ToastNotification"/> has been collected, a click in the Action Center
    /// silently does nothing, which is exactly what happened until this list existed.
    /// Timed-out toasts stay here because that is when they are clickable from the panel.
    /// </summary>
    private readonly List<ToastNotification> _liveToasts = [];
    private readonly object _liveGate = new();
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

    public void Show(ToastMessage message) => ShowAndTrack(message);

    /// <summary>
    /// Shows a toast and returns the live object so a diagnostic can hide exactly that one.
    /// Normal callers use <see cref="Show"/>; the reference list still owns the lifetime.
    /// </summary>
    public ToastNotification? ShowAndTrack(ToastMessage message)
    {
        try
        {
            var xml = BuildXml(message);
            var document = new XmlDocument();
            document.LoadXml(xml);

            var toast = new ToastNotification(document);

            // The gate's toast is proof, not a notification the user should keep. Tagging it
            // lets SelfTest remove exactly this one from Action Center afterwards.
            if (string.Equals(message.ServiceId, "selftest", StringComparison.Ordinal))
            {
                toast.Tag = "selftest";
                toast.Group = "selftest";
            }

            Remember(toast);

            toast.Activated += (_, _) =>
            {
                _log.Info($"toast activated by the user for '{message.ServiceId}'");
                Forget(toast);

                try
                {
                    _onActivated?.Invoke(message.ServiceId);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    _log.Error("toast activation handler failed", ex);
                }
            };

            toast.Dismissed += (_, args) =>
            {
                _log.Info($"toast dismissed ({args.Reason}) for '{message.ServiceId}'");

                // TimedOut means it moved to the Action Center and is still clickable there,
                // so it must stay referenced. Any other reason means it is gone.
                if (args.Reason != ToastDismissalReason.TimedOut)
                {
                    Forget(toast);
                }
            };

            toast.Failed += (_, args) =>
            {
                _log.Warn($"toast failed: {args.ErrorCode}");
                Forget(toast);
            };

            Notifier.Show(toast);
            return toast;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _log.Error("could not show a toast", ex);
            return null;
        }
    }

    /// <summary>Hides a tracked toast and removes it from history; used only by the self-test.</summary>
    public void Hide(ToastNotification? toast)
    {
        if (toast is null)
        {
            return;
        }

        try
        {
            Notifier.Hide(toast);
            ToastNotificationManager.History.Remove(toast.Tag, toast.Group, Aumid.AppId);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _log.Warn($"could not remove a toast: {ex.Message}");
        }
        finally
        {
            Forget(toast);
        }
    }

    private void Remember(ToastNotification toast)
    {
        lock (_liveGate)
        {
            _liveToasts.Add(toast);

            // A long session must not grow this without bound; the oldest entries are the
            // least likely to still be waiting in the Action Center.
            if (_liveToasts.Count > 64)
            {
                _liveToasts.RemoveAt(0);
            }
        }
    }

    private void Forget(ToastNotification toast)
    {
        lock (_liveGate)
        {
            _liveToasts.Remove(toast);
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
