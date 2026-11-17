namespace Chirpdesk.Core.Blocking;

/// <summary>
/// One <c>filters.json</c> pack. Rules are data only; the host compiles and evaluates them.
/// Invalid entries are skipped by <see cref="RequestMatcher"/> and counted, never fatal.
/// </summary>
public sealed class FilterPack
{
    public string Id { get; set; } = string.Empty;

    public List<string> BlockHosts { get; set; } = [];

    public List<string> BlockPaths { get; set; } = [];

    /// <summary>Extra allow hosts. A pack may add here; it can never remove a permanent entry.</summary>
    public List<string> AllowHosts { get; set; } = [];

    public List<string> Cosmetic { get; set; } = [];
}
