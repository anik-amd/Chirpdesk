using Chirpdesk.Core.Notifications;

namespace Chirpdesk.Core.Tests.Notifications;

public class NotificationTimeTests
{
    [Fact]
    public void An_unspecified_kind_is_read_as_utc()
    {
        // The real WebView2 case: a Chromium notification timestamp arrives with no kind but
        // a UTC value. Reading it as local time is what dropped every WhatsApp toast.
        var raw = new DateTime(2026, 11, 16, 16, 2, 3, 323, DateTimeKind.Unspecified);

        var instant = NotificationTime.Normalize(raw);

        Assert.Equal(TimeSpan.Zero, instant.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 16, 2, 3, 323, TimeSpan.Zero), instant);
    }

    [Fact]
    public void An_utc_kind_stays_utc()
    {
        var raw = new DateTime(2026, 11, 16, 16, 2, 3, DateTimeKind.Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 16, 16, 2, 3, TimeSpan.Zero), NotificationTime.Normalize(raw));
    }

    [Fact]
    public void A_local_kind_keeps_its_offset()
    {
        var raw = new DateTime(2026, 11, 16, 16, 2, 3, DateTimeKind.Local);

        Assert.Equal(new DateTimeOffset(raw), NotificationTime.Normalize(raw));
    }

    [Fact]
    public void A_fresh_notification_is_not_mistaken_for_an_old_one()
    {
        // The exact regression: a notification created "now" must compare as newer than a
        // process that started a few seconds earlier, whatever the local offset is.
        var startedAt = new DateTimeOffset(2026, 11, 16, 16, 1, 59, TimeSpan.Zero);
        var created = NotificationTime.Normalize(
            new DateTime(2026, 11, 16, 16, 2, 3, DateTimeKind.Unspecified));

        Assert.True(created > startedAt);
    }
}
