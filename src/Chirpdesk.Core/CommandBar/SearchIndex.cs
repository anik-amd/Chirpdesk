namespace Chirpdesk.Core.CommandBar;

/// <summary>Where a search row comes from. Warm beats cold beats a command at equal match.</summary>
public enum SearchKind
{
    Warm,
    Cold,
    Command,
}

/// <summary>
/// One searchable row. A service carries its display name, optional definition aliases and,
/// for a legacy instance, its host; a command carries only a name for now.
/// </summary>
public sealed record SearchCandidate(
    string Id,
    string Name,
    SearchKind Kind,
    IReadOnlyList<string>? Aliases = null,
    string? Host = null,
    string? State = null,
    string? Accent = null,
    string? Initial = null,
    string? DefinitionId = null);

/// <summary>
/// Ranking for the Command Bar: prefix beats substring, warm beats cold beats command at equal
/// score, and input order (MRU for warm rows) breaks the remaining ties. Case-insensitive.
/// </summary>
public static class SearchIndex
{
    private const int NameExact = 400;
    private const int NamePrefix = 300;
    private const int NameSubstring = 100;

    private const int AliasExact = 350;
    private const int AliasPrefix = 250;
    private const int AliasSubstring = 90;

    private const int HostExact = 200;
    private const int HostPrefix = 150;
    private const int HostSubstring = 60;

    public static IReadOnlyList<SearchCandidate> Rank(
        string? query,
        IReadOnlyList<SearchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (string.IsNullOrWhiteSpace(query))
        {
            return candidates
                .OrderBy(candidate => Tier(candidate.Kind))
                .ToList();
        }

        var term = query.Trim();

        return candidates
            .Select((candidate, index) => (candidate, index, score: Score(candidate, term)))
            .Where(row => row.score > 0)
            .OrderByDescending(row => row.score)
            .ThenBy(row => Tier(row.candidate.Kind))
            .ThenBy(row => row.index)
            .Select(row => row.candidate)
            .ToList();
    }

    private static int Score(SearchCandidate candidate, string term)
    {
        var best = Match(candidate.Name, term, NameExact, NamePrefix, NameSubstring);

        foreach (var alias in candidate.Aliases ?? [])
        {
            best = Math.Max(best, Match(alias, term, AliasExact, AliasPrefix, AliasSubstring));
        }

        if (!string.IsNullOrWhiteSpace(candidate.Host))
        {
            best = Math.Max(best, Match(candidate.Host, term, HostExact, HostPrefix, HostSubstring));
        }

        return best;
    }

    private static int Match(string? value, string term, int exact, int prefix, int substring)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var text = value.Trim();

        if (text.Equals(term, StringComparison.OrdinalIgnoreCase))
        {
            return exact;
        }

        if (text.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            return prefix;
        }

        return text.Contains(term, StringComparison.OrdinalIgnoreCase) ? substring : 0;
    }

    private static int Tier(SearchKind kind) => kind switch
    {
        SearchKind.Warm => 0,
        SearchKind.Cold => 1,
        _ => 2,
    };
}
