namespace Chirpdesk.Core.Notifications;

/// <summary>
/// WebView2 exposes a notification's creation time as a <see cref="DateTime"/>, and for
/// Chromium-created notifications the kind is <see cref="DateTimeKind.Unspecified"/> even
/// though the value is UTC. Comparing that straight against a <see cref="DateTimeOffset"/>
/// shifts it by the local offset, which silently turns "just now" into "six hours ago" and
/// drops the toast. Normalising explicitly is the only safe reading.
/// </summary>
public static class NotificationTime
{
    public static DateTimeOffset Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(value),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero),
    };
}
