namespace Chirpdesk.Core.Storage;

/// <summary>One disposed profile as the trim planner sees it. Bytes are cache only.</summary>
public sealed record ProfileCache(
    string ProfileId,
    string Directory,
    long CacheBytes,
    bool Disposed,
    DateTimeOffset LastUsed);

public sealed record TrimCandidate(string ProfileId, string Directory, long Bytes);

public sealed record TrimPlan(IReadOnlyList<TrimCandidate> Candidates)
{
    public long ReclaimedBytes => Candidates.Sum(candidate => candidate.Bytes);
}

/// <summary>
/// Chooses cache folders to delete. Warm profiles are never candidates; per-profile excess is
/// reclaimed first, then the oldest disposed profiles until the global budget is met. Session
/// data is not in <see cref="ProfileCache.CacheBytes"/>, so it cannot be selected here.
/// </summary>
public static class TrimPlanner
{
    public static TrimPlan Plan(
        IEnumerable<ProfileCache> profiles,
        long perProfileBudget,
        long globalBudget)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var perProfile = Math.Max(0, perProfileBudget);
        var global = Math.Max(0, globalBudget);

        var ordered = profiles
            .Where(profile => profile.Disposed && profile.CacheBytes > 0)
            .OrderBy(profile => profile.LastUsed)
            .ThenBy(profile => profile.ProfileId, StringComparer.Ordinal)
            .ToList();

        var reclaim = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var profile in ordered)
        {
            var excess = profile.CacheBytes - perProfile;
            if (excess > 0)
            {
                reclaim[profile.ProfileId] = excess;
            }
        }

        var remaining = ordered.Sum(profile => profile.CacheBytes - reclaim.GetValueOrDefault(profile.ProfileId));

        foreach (var profile in ordered)
        {
            if (remaining <= global)
            {
                break;
            }

            var current = reclaim.GetValueOrDefault(profile.ProfileId);
            var left = profile.CacheBytes - current;
            if (left <= 0)
            {
                continue;
            }

            reclaim[profile.ProfileId] = current + left;
            remaining -= left;
        }

        var candidates = ordered
            .Where(profile => reclaim.ContainsKey(profile.ProfileId))
            .Select(profile => new TrimCandidate(profile.ProfileId, profile.Directory, reclaim[profile.ProfileId]))
            .ToList();

        return new TrimPlan(candidates);
    }
}
