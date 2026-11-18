using Chirpdesk.Core.Updates;

namespace Chirpdesk.Core.Tests.Updates;

public class GitHubReleaseTests
{
    private const string Stable = """
        {
          "tag_name": "v1.3.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "Chirpdesk-Setup.exe", "browser_download_url": "https://example.test/Chirpdesk-Setup.exe" },
            { "name": "update.json", "browser_download_url": "https://example.test/update.json" }
          ]
        }
        """;

    [Fact]
    public void A_stable_release_with_update_json_is_accepted()
    {
        Assert.True(GitHubReleaseParser.TryReadLatest(Stable, out var release, out var error), error);
        Assert.NotNull(release);
        Assert.Equal("v1.3.0", release.Tag);
        Assert.Equal("https://example.test/update.json", release.ManifestUrl);
    }

    [Fact]
    public void Draft_releases_are_refused()
    {
        var json = Stable.Replace("\"draft\": false", "\"draft\": true", StringComparison.Ordinal);

        Assert.False(GitHubReleaseParser.TryReadLatest(json, out _, out var error));
        Assert.Contains("draft", error);
    }

    [Fact]
    public void Prereleases_are_refused()
    {
        var json = Stable.Replace("\"prerelease\": false", "\"prerelease\": true", StringComparison.Ordinal);

        Assert.False(GitHubReleaseParser.TryReadLatest(json, out _, out var error));
        Assert.Contains("prerelease", error);
    }

    [Fact]
    public void A_release_without_the_manifest_asset_is_refused()
    {
        var json = Stable.Replace("\"name\": \"update.json\"", "\"name\": \"notes.txt\"", StringComparison.Ordinal);

        Assert.False(GitHubReleaseParser.TryReadLatest(json, out _, out var error));
        Assert.Contains("update.json", error);
    }

    [Fact]
    public void A_non_https_manifest_asset_is_refused()
    {
        var json = Stable.Replace("https://example.test/update.json", "http://example.test/update.json", StringComparison.Ordinal);

        Assert.False(GitHubReleaseParser.TryReadLatest(json, out _, out var error));
        Assert.Contains("https", error);
    }
}
