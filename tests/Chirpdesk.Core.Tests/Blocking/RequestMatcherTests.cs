using Chirpdesk.Core.Blocking;

namespace Chirpdesk.Core.Tests.Blocking;

public sealed class RequestMatcherTests
{
    private static FilterPack Pack(
        string[]? blockHosts = null,
        string[]? blockPaths = null,
        string[]? allowHosts = null,
        string[]? cosmetic = null) => new()
    {
        Id = "test",
        BlockHosts = blockHosts?.ToList() ?? [],
        BlockPaths = blockPaths?.ToList() ?? [],
        AllowHosts = allowHosts?.ToList() ?? [],
        Cosmetic = cosmetic?.ToList() ?? [],
    };

    [Fact]
    public void An_exact_blocked_host_is_blocked()
    {
        var matcher = new RequestMatcher("https://web.whatsapp.com/", [Pack(["ads.example.com"])]);

        Assert.Equal(RequestVerdict.Block, matcher.Evaluate("https://ads.example.com/pixel.js"));
    }

    [Fact]
    public void A_wildcard_host_matches_subdomains_and_the_base_domain()
    {
        var matcher = new RequestMatcher("https://web.whatsapp.com/", [Pack(["*.fbcdn.net"])]);

        Assert.Equal(RequestVerdict.Block, matcher.Evaluate("https://scontent.fbcdn.net/a.png"));
        Assert.Equal(RequestVerdict.Block, matcher.Evaluate("https://fbcdn.net/a.png"));
        Assert.Equal(RequestVerdict.Allow, matcher.Evaluate("https://notfbcdn.net/a.png"));
    }

    [Fact]
    public void Host_matching_ignores_case_and_port()
    {
        var matcher = new RequestMatcher("https://web.whatsapp.com/", [Pack(["ADS.EXAMPLE.COM"])]);

        Assert.Equal(RequestVerdict.Block, matcher.Evaluate("https://Ads.Example.com:8443/x"));
    }

    [Fact]
    public void A_path_glob_blocks_without_blocking_the_whole_host()
    {
        var matcher = new RequestMatcher("https://web.whatsapp.com/", [Pack(blockPaths: ["*/tr/"])]);

        Assert.Equal(RequestVerdict.Block, matcher.Evaluate("https://graph.facebook.com/ajax/tr/?id=1"));
        Assert.Equal(RequestVerdict.Allow, matcher.Evaluate("https://graph.facebook.com/ajax/ok/"));
    }

    [Fact]
    public void The_service_origin_is_allowlisted_before_packs_load()
    {
        var matcher = new RequestMatcher(
            "https://web.telegram.org/",
            [Pack(["web.telegram.org"], blockPaths: ["*"])]);

        Assert.Equal(RequestVerdict.Allow, matcher.Evaluate("https://web.telegram.org/a/"));
    }

    [Theory]
    [InlineData("https://web.whatsapp.com/ws")]
    [InlineData("https://web.whatsapp.com/ws/chat")]
    [InlineData("https://example.org/oauth/callback")]
    [InlineData("https://example.org/login?next=/")]
    public void The_permanent_allowlist_wins_over_every_pack(string url)
    {
        var matcher = new RequestMatcher(
            "https://web.whatsapp.com/",
            [Pack(["*"], ["*"])]);

        Assert.Equal(RequestVerdict.Allow, matcher.Evaluate(url));
    }

    [Fact]
    public void A_pack_can_add_allow_hosts_but_cannot_remove_the_permanent_ones()
    {
        var matcher = new RequestMatcher(
            "https://web.whatsapp.com/",
            [Pack(["web.whatsapp.com", "safe.example.com"], allowHosts: ["safe.example.com"])]);

        Assert.Equal(RequestVerdict.Allow, matcher.Evaluate("https://safe.example.com/x"));
        Assert.Equal(RequestVerdict.Allow, matcher.Evaluate("https://web.whatsapp.com/ws"));
    }

    [Fact]
    public void Invalid_host_patterns_are_skipped_and_counted_not_fatal()
    {
        var matcher = new RequestMatcher(
            "https://web.whatsapp.com/",
            [Pack(["bad host", "https://nope", "ads.example.com"])]);

        Assert.Equal(RequestVerdict.Block, matcher.Evaluate("https://ads.example.com/x"));
        Assert.True(matcher.SkippedRules >= 2);
    }

    [Fact]
    public void A_uri_without_a_host_is_allowed()
    {
        var matcher = new RequestMatcher("https://web.whatsapp.com/", [Pack(["*"])]);

        Assert.Equal(RequestVerdict.Allow, matcher.Evaluate("data:text/plain,hello"));
        Assert.Equal(RequestVerdict.Allow, matcher.Evaluate("not a uri"));
    }

    [Fact]
    public void Cosmetic_selectors_are_deduplicated()
    {
        var matcher = new RequestMatcher(
            "https://web.whatsapp.com/",
            [
                Pack(cosmetic: ["[data-testid='ad']", "  "]),
                Pack(cosmetic: ["[data-testid='ad']", ".sponsored"]),
            ]);

        Assert.Equal(2, matcher.CosmeticSelectors.Count);
    }
}
