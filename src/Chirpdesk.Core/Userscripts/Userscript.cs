namespace Chirpdesk.Core.Userscripts;

/// <summary>
/// One parsed userscript: the metadata block plus the body. Scripts are third-party code the
/// user explicitly installs; nothing here is signed, so the host shows the source, matches,
/// grants and hash before enabling one.
/// </summary>
public sealed class Userscript
{
    public string Name { get; init; } = "Untitled script";

    public string? Namespace { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Matches { get; init; } = [];

    public IReadOnlyList<string> Includes { get; init; } = [];

    public IReadOnlyList<string> Grants { get; init; } = [];

    /// <summary><c>document-start</c> or <c>document-idle</c> (the default).</summary>
    public string RunAt { get; init; } = "document-idle";

    public string? UpdateUrl { get; init; }

    public string? DownloadUrl { get; init; }

    public string Body { get; init; } = string.Empty;

    public bool AppliesTo(string url) =>
        Matches.Any(pattern => UserscriptMatcher.Match(pattern, url))
        || Includes.Any(pattern => UserscriptMatcher.Include(pattern, url));
}
