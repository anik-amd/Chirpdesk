using Chirpdesk.Core.Storage;

namespace Chirpdesk.Core.Tests.Storage;

public class TrimPlannerTests
{
    private static ProfileCache Profile(
        string id,
        long cacheBytes,
        bool disposed = true,
        int minutesOld = 0) =>
        new(id, Path.Combine("C:\\profiles", id), cacheBytes, disposed, DateTimeOffset.UtcNow.AddMinutes(-minutesOld));

    [Fact]
    public void A_profile_over_its_budget_reclaims_the_excess()
    {
        var plan = TrimPlanner.Plan([Profile("whatsapp", 100)], perProfileBudget: 60, globalBudget: 1000);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(40, candidate.Bytes);
    }

    [Fact]
    public void A_warm_profile_is_never_selected()
    {
        var plan = TrimPlanner.Plan([Profile("whatsapp", 500, disposed: false)], perProfileBudget: 60, globalBudget: 0);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void Profiles_under_both_budgets_produce_no_work()
    {
        var plan = TrimPlanner.Plan(
            [Profile("whatsapp", 50), Profile("telegram", 40, minutesOld: 10)],
            perProfileBudget: 60,
            globalBudget: 180);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void The_global_budget_takes_the_oldest_profile_first()
    {
        var plan = TrimPlanner.Plan(
            [
                Profile("young", 50, minutesOld: 1),
                Profile("old", 50, minutesOld: 60),
            ],
            perProfileBudget: 60,
            globalBudget: 60);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal("old", candidate.ProfileId);
        Assert.Equal(50, candidate.Bytes);
    }

    [Fact]
    public void Per_profile_and_global_reclaims_add_up_without_double_counting()
    {
        var plan = TrimPlanner.Plan(
            [
                Profile("old", 100, minutesOld: 60),
                Profile("young", 100, minutesOld: 1),
            ],
            perProfileBudget: 60,
            globalBudget: 100);

        Assert.Equal(140, plan.ReclaimedBytes);
        Assert.Equal(100, plan.Candidates.Single(c => c.ProfileId == "old").Bytes);
        Assert.Equal(40, plan.Candidates.Single(c => c.ProfileId == "young").Bytes);
    }

    [Fact]
    public void Zero_budgets_are_treated_as_zero_not_negative()
    {
        var plan = TrimPlanner.Plan([Profile("whatsapp", 10)], perProfileBudget: -5, globalBudget: -1);

        Assert.Equal(10, Assert.Single(plan.Candidates).Bytes);
    }

    [Fact]
    public void An_empty_profile_list_is_a_no_op()
    {
        Assert.Empty(TrimPlanner.Plan([], 60, 180).Candidates);
    }
}
