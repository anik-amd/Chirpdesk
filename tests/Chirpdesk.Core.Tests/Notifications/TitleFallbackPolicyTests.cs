using Chirpdesk.Core.Notifications;

namespace Chirpdesk.Core.Tests.Notifications;

public sealed class TitleFallbackPolicyTests
{
    private static readonly TimeSpan QuietLongEnough = TitleFallbackPolicy.RequiredQuiet + TimeSpan.FromSeconds(1);

    [Fact]
    public void Starts_when_the_gate_is_open_the_title_was_quiet_and_nothing_covers_it()
    {
        Assert.True(TitleFallbackPolicy.ShouldStart(gateOpen: true, quietFor: QuietLongEnough, coveredElsewhere: false));
    }

    [Fact]
    public void Waits_while_the_warm_up_gate_is_closed()
    {
        Assert.False(TitleFallbackPolicy.ShouldStart(gateOpen: false, QuietLongEnough, coveredElsewhere: false));
    }

    [Fact]
    public void Waits_for_the_quiet_window_after_a_title_change()
    {
        Assert.False(TitleFallbackPolicy.ShouldStart(
            gateOpen: true,
            TitleFallbackPolicy.RequiredQuiet - TimeSpan.FromSeconds(1),
            coveredElsewhere: false));
    }

    [Fact]
    public void Yields_to_a_service_that_covers_the_same_conversations()
    {
        Assert.False(TitleFallbackPolicy.ShouldStart(gateOpen: true, QuietLongEnough, coveredElsewhere: true));
    }

    [Fact]
    public void Accepts_exactly_the_quiet_window()
    {
        Assert.True(TitleFallbackPolicy.ShouldStart(
            gateOpen: true,
            TitleFallbackPolicy.RequiredQuiet,
            coveredElsewhere: false));
    }
}
