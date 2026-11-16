using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Tests.Time;

namespace Chirpdesk.Core.Tests.Notifications;

public class NotificationDedupeTests
{
    private static NotificationDedupe Create(FakeClock clock) => new(clock, TimeSpan.FromSeconds(3));

    [Fact]
    public void Drops_same_notification_within_window()
    {
        var clock = new FakeClock();
        var dedupe = Create(clock);
        Assert.True(dedupe.ShouldShow("chat", "Alice", "hello"));
        clock.AdvanceSeconds(2);
        Assert.False(dedupe.ShouldShow("chat", "Alice", "hello"));
    }

    [Fact]
    public void Allows_same_notification_after_window()
    {
        var clock = new FakeClock();
        var dedupe = Create(clock);
        dedupe.ShouldShow("chat", "Alice", "hello");
        clock.AdvanceSeconds(4);
        Assert.True(dedupe.ShouldShow("chat", "Alice", "hello"));
    }

    [Fact]
    public void Different_body_is_not_a_duplicate()
    {
        var clock = new FakeClock();
        var dedupe = Create(clock);
        dedupe.ShouldShow("chat", "Alice", "hello");
        Assert.True(dedupe.ShouldShow("chat", "Alice", "hello there"));
    }

    [Fact]
    public void Same_body_from_a_different_sender_is_not_a_duplicate()
    {
        var clock = new FakeClock();
        var dedupe = Create(clock);
        dedupe.ShouldShow("chat", "Alice", "hello");
        Assert.True(dedupe.ShouldShow("chat", "Bob", "hello"));
    }

    [Fact]
    public void Same_body_in_a_different_tag_is_not_a_duplicate()
    {
        var clock = new FakeClock();
        var dedupe = Create(clock);
        dedupe.ShouldShow("chat", "Alice", "hello");
        Assert.True(dedupe.ShouldShow("call", "Alice", "hello"));
    }

    [Fact]
    public void Does_not_retain_the_message_body()
    {
        // Privacy invariant: only a hash is kept, so the body cannot leak from memory later.
        var clock = new FakeClock();
        var dedupe = Create(clock);
        dedupe.ShouldShow("chat", "Alice", "top-secret-text");
        Assert.DoesNotContain("top-secret-text", dedupe.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Prunes_expired_entries_instead_of_growing_forever()
    {
        var clock = new FakeClock();
        var dedupe = Create(clock);
        for (var i = 0; i < 100; i++)
        {
            dedupe.ShouldShow("chat", "Alice", $"message {i}");
            clock.AdvanceSeconds(10);
        }

        Assert.True(dedupe.Count <= 1);
    }
}
