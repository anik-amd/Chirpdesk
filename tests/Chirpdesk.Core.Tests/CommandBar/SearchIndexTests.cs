using Chirpdesk.Core.CommandBar;

namespace Chirpdesk.Core.Tests.CommandBar;

public class SearchIndexTests
{
    private static SearchCandidate Warm(string id, string name, params string[] aliases) =>
        new(id, name, SearchKind.Warm, aliases);

    private static SearchCandidate Cold(string id, string name, params string[] aliases) =>
        new(id, name, SearchKind.Cold, aliases);

    [Fact]
    public void Prefix_outranks_substring()
    {
        var candidates = new[]
        {
            Warm("mine", "My Chat"),
            Warm("chatter", "Chatter"),
        };

        var ranked = SearchIndex.Rank("chat", candidates);

        Assert.Equal("chatter", ranked[0].Id);
        Assert.Equal("mine", ranked[1].Id);
    }

    [Fact]
    public void Warm_outranks_cold_at_equal_score()
    {
        var candidates = new[]
        {
            Cold("telethon", "Telethon"),
            Warm("telegram", "Telegram"),
        };

        var ranked = SearchIndex.Rank("tel", candidates);

        Assert.Equal("telegram", ranked[0].Id);
        Assert.Equal("telethon", ranked[1].Id);
    }

    [Fact]
    public void An_alias_matches_like_the_name()
    {
        var candidates = new[]
        {
            Warm("whatsapp", "WhatsApp", "wa"),
            Warm("work", "Work Chat"),
        };

        var ranked = SearchIndex.Rank("wa", candidates);

        Assert.Equal("whatsapp", ranked[0].Id);
    }

    [Fact]
    public void A_legacy_custom_instance_matches_by_host()
    {
        var candidates = new[]
        {
            new SearchCandidate("work", "Work", SearchKind.Cold, null, "chat.example.com"),
            Cold("other", "Other"),
        };

        var ranked = SearchIndex.Rank("example", candidates);

        Assert.Single(ranked);
        Assert.Equal("work", ranked[0].Id);
    }

    [Fact]
    public void An_empty_query_lists_warm_then_cold_then_commands_in_input_order()
    {
        var candidates = new[]
        {
            new SearchCandidate("cmd", "Quit", SearchKind.Command),
            Cold("cold", "Cold Chat"),
            Warm("b", "Bravo"),
            Warm("a", "Alpha"),
        };

        var ranked = SearchIndex.Rank("", candidates);

        Assert.Equal(["b", "a", "cold", "cmd"], ranked.Select(r => r.Id));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var ranked = SearchIndex.Rank("TELE", [Warm("telegram", "Telegram")]);

        Assert.Single(ranked);
    }

    [Fact]
    public void No_match_returns_nothing()
    {
        var ranked = SearchIndex.Rank("zzz", [Warm("telegram", "Telegram")]);

        Assert.Empty(ranked);
    }

    [Fact]
    public void An_exact_name_beats_a_prefix()
    {
        var candidates = new[]
        {
            Warm("chatter", "Chatter"),
            Warm("chat", "Chat"),
        };

        var ranked = SearchIndex.Rank("chat", candidates);

        Assert.Equal("chat", ranked[0].Id);
    }
}
