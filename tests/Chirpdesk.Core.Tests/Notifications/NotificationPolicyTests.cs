using Chirpdesk.Core.Notifications;

namespace Chirpdesk.Core.Tests.Notifications;

public class NotificationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_muted_instance_suppresses_its_toasts()
    {
        Assert.True(NotificationPolicy.ShouldSuppress(muted: true, dndUntil: null, Now));
    }

    [Fact]
    public void Dnd_suppresses_every_toast_until_it_expires()
    {
        Assert.True(NotificationPolicy.ShouldSuppress(false, Now.AddMinutes(1), Now));
        Assert.False(NotificationPolicy.ShouldSuppress(false, Now, Now));
        Assert.False(NotificationPolicy.ShouldSuppress(false, Now.AddMinutes(-1), Now));
        Assert.False(NotificationPolicy.ShouldSuppress(false, null, Now));
    }

    [Fact]
    public void An_unmuted_instance_outside_dnd_shows()
    {
        Assert.False(NotificationPolicy.ShouldSuppress(muted: false, dndUntil: null, Now));
    }
}
