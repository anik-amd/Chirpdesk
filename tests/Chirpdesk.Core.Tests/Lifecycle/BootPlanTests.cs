using Chirpdesk.Core.Lifecycle;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Tests.Lifecycle;

public class BootPlanTests
{
    private static ServiceInstance Instance(string id, bool warm, int order = 0) => new()
    {
        InstanceId = id,
        Name = id,
        DefinitionId = id,
        WarmAtBoot = warm,
        Order = order,
    };

    [Fact]
    public void Only_pinned_instances_are_selected()
    {
        var settings = new AppSettings
        {
            Services =
            [
                Instance("cold", warm: false),
                Instance("warm", warm: true),
            ],
        };

        var plan = BootPlan.Select(settings);

        Assert.Single(plan);
        Assert.Equal("warm", plan[0].InstanceId);
    }

    [Fact]
    public void Pinned_instances_follow_user_order_then_id()
    {
        var settings = new AppSettings
        {
            Services =
            [
                Instance("third", warm: true, order: 2),
                Instance("second-b", warm: true, order: 1),
                Instance("second-a", warm: true, order: 1),
                Instance("first", warm: true, order: 0),
            ],
        };

        var plan = BootPlan.Select(settings);

        Assert.Equal(["first", "second-a", "second-b", "third"], plan.Select(p => p.InstanceId));
    }

    [Fact]
    public void Staggering_starts_at_the_initial_delay_and_spaces_entries()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), BootPlan.DelayFor(0, TimeSpan.FromSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(21.5), BootPlan.DelayFor(1, TimeSpan.FromSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(23), BootPlan.DelayFor(2, TimeSpan.FromSeconds(20)));
        Assert.Equal(TimeSpan.Zero, BootPlan.DelayFor(0, TimeSpan.Zero));
    }

    [Fact]
    public void Autostart_command_quotes_the_executable_and_uses_the_switch()
    {
        var command = AutostartEntry.Command(@"C:\Program Files\Chirpdesk\Chirpdesk.exe");

        Assert.Equal("\"C:\\Program Files\\Chirpdesk\\Chirpdesk.exe\" --autostart", command);
        Assert.True(AutostartEntry.IsCurrent(command, @"C:\Program Files\Chirpdesk\Chirpdesk.exe"));
        Assert.False(AutostartEntry.IsCurrent(null, @"C:\Program Files\Chirpdesk\Chirpdesk.exe"));
        Assert.False(AutostartEntry.IsCurrent("Chirpdesk.exe", @"C:\Program Files\Chirpdesk\Chirpdesk.exe"));
    }

    [Fact]
    public void An_empty_settings_file_has_an_empty_plan()
    {
        Assert.Empty(BootPlan.Select(new AppSettings()));
    }
}
