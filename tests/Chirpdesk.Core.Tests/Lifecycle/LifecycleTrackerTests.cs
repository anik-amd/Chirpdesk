using Chirpdesk.Core.Lifecycle;

namespace Chirpdesk.Core.Tests.Lifecycle;

public class LifecycleTrackerTests
{
    [Fact]
    public void An_unknown_service_is_cold()
    {
        var tracker = new LifecycleTracker();

        Assert.Equal(RuntimeState.Cold, tracker.StateOf("whatsapp"));
    }

    [Fact]
    public void Warm_asleep_and_cold_are_distinct_states()
    {
        var tracker = new LifecycleTracker();

        tracker.MarkWarm("whatsapp");
        Assert.Equal(RuntimeState.Warm, tracker.StateOf("whatsapp"));

        tracker.MarkAsleep("whatsapp");
        Assert.Equal(RuntimeState.Asleep, tracker.StateOf("whatsapp"));

        tracker.MarkCold("whatsapp");
        Assert.Equal(RuntimeState.Cold, tracker.StateOf("whatsapp"));
    }

    [Fact]
    public void Waking_an_asleep_service_returns_it_to_warm()
    {
        var tracker = new LifecycleTracker();
        tracker.MarkAsleep("telegram");

        tracker.MarkWarm("telegram");

        Assert.Equal(RuntimeState.Warm, tracker.StateOf("telegram"));
    }

    [Fact]
    public void Ids_are_case_insensitive()
    {
        var tracker = new LifecycleTracker();
        tracker.MarkWarm("WhatsApp");

        Assert.Equal(RuntimeState.Warm, tracker.StateOf("whatsapp"));
    }

    [Fact]
    public void Forgetting_a_service_returns_it_to_cold()
    {
        var tracker = new LifecycleTracker();
        tracker.MarkWarm("whatsapp");

        tracker.Forget("whatsapp");

        Assert.Equal(RuntimeState.Cold, tracker.StateOf("whatsapp"));
    }
}
