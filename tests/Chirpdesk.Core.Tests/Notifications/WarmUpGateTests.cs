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
    public void Suppresses_while_the_title_is_still_changing()
    {
        var clock = new FakeClock();
        var gate = Create(clock);
        gate.MarkReady();
        clock.AdvanceSeconds(2);
        gate.ObserveTitle(7);
        clock.AdvanceSeconds(2);
        gate.ObserveTitle(8);

        Assert.True(gate.ShouldSuppress());
    }

    [Fact]
    public void Allows_after_five_seconds_of_a_stable_title()
    {
        var clock = new FakeClock();
        var gate = Create(clock);
        gate.MarkReady();
        clock.AdvanceSeconds(6);

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
        gate.ObserveTitle(4);
        Assert.False(gate.ShouldSuppress());
    }

    [Fact]
    public void An_unchanged_title_does_not_reset_the_stability_timer()
    {
        var clock = new FakeClock();
        var gate = Create(clock);
        gate.MarkReady();

        for (var i = 0; i < 4; i++)
        {
            clock.AdvanceSeconds(2);
            gate.ObserveTitle(2);
        }

        Assert.False(gate.ShouldSuppress());
    }
}
