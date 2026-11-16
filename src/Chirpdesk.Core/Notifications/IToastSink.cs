namespace Chirpdesk.Core.Notifications;

/// <summary>What a notification looks like once the webview has been stripped away.</summary>
public sealed record ToastMessage(string Title, string Body, string? IconPath = null, string? ServiceId = null);

/// <summary>
/// The seam that keeps notification policy testable: Core decides, the shell shows.
/// </summary>
public interface IToastSink
{
    void Show(ToastMessage message);
}
