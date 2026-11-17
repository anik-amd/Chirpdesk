namespace Chirpdesk.Core.Notifications;

/// <summary>
/// Toast filtering only. Unread counts keep running; DND drops the toast instead of replaying it
/// later (PLAN §6/§9).
/// </summary>
public static class NotificationPolicy
{
    public static bool ShouldSuppress(bool muted, DateTimeOffset? dndUntil, DateTimeOffset now) =>
        muted || (dndUntil is { } until && until > now);
}
