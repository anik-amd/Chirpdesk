using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Tests.Time;

namespace Chirpdesk.Core.Tests.Notifications;

public class WarmUpGateTests
{
    private static WarmUpGate Create(FakeClock clock) => new(clock, TimeSpan.FromSeconds(5));

    [Fact]
    public void Suppresses_before_the_service_is_ready()
    {
        var clock = new FakeClock();
        var gate = Create(clock);
        clock.AdvanceSeconds(30);
        Assert.True(gate.ShouldSuppress());
    }

    [Fact]
    public void Suppresses_for_five_seconds_after_the_page_is_ready()
    {
        var clock = new FakeClock();
        var gate = Create(clock);
        gate.MarkReady();

        clock.AdvanceSeconds(2);
        Assert.True(gate.ShouldSuppress());
        clock.AdvanceSeconds(2);
        Assert.True(gate.ShouldSuppress());
    }

    [Fact]
    public void Opens_after_five_seconds_of_readiness()
    {
        var clock = new FakeClock();
        var gate = Create(clock);
        gate.MarkReady();
        clock.AdvanceSeconds(6);

        Assert.False(gate.ShouldSuppress());
    }

    [Fact]
    public void Opening_is_not_blocked_by_the_title_still_changing()
    {
        // The regression that suppressed every WhatsApp message: a message changes the title
        // at the same instant it notifies, so a gate waiting for a stable title never opened.
        var clock = new FakeClock();
        var gate = Create(clock);
        gate.MarkReady();

        clock.AdvanceSeconds(6);
        gate.ObserveTitle(1);
        gate.ObserveTitle(2);

        Assert.False(gate.ShouldSuppress());
    }

    [Fact]
    public void Once_open_it_never_re_suppresses()
    {
        var clock = new FakeClock();
        var gate = Create(clock);
        gate.MarkReady();
        clock.AdvanceSeconds(6);
        Assert.False(gate.ShouldSuppress());

        gate.ObserveTitle(3);
        Assert.False(gate.ShouldSuppress());
        clock.AdvanceSeconds(60);
        Assert.False(gate.ShouldSuppress());
    }

    [Fact]
    public void A_late_mark_ready_does_not_restart_the_clock()
    {
        var clock = new FakeClock();
        var gate = Create(clock);
        gate.MarkReady();
        clock.AdvanceSeconds(3);
        gate.MarkReady();
        clock.AdvanceSeconds(3);

        Assert.False(gate.ShouldSuppress());
    }
}
