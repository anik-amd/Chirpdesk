using Chirpdesk.Core.Userscripts;

namespace Chirpdesk.Core.Tests.Userscripts;

public sealed class UserscriptParserTests
{
    private const string Sample = """
        // ==UserScript==
        // @name         Dark chats
        // @namespace    chirpdesk.tests
        // @version      1.2.0
        // @description  Tweak the chat list
        // @match        https://web.whatsapp.com/*
        // @match        https://*.messenger.com/*
        // @include      https://example.com/chat*
        // @grant        GM_addStyle
        // @grant        GM_setValue
        // @run-at       document-start
        // @updateURL    https://example.com/dark.meta.js
        // ==/UserScript==

        (() => { document.body.dataset.dark = '1'; })();
        """;

    [Fact]
    public void Reads_the_tampermonkey_header()
    {
        var script = UserscriptParser.Parse(Sample);

        Assert.Equal("Dark chats", script.Name);
        Assert.Equal("1.2.0", script.Version);
        Assert.Equal("document-start", script.RunAt);
        Assert.Equal(2, script.Matches.Count);
        Assert.Single(script.Includes);
        Assert.Equal(["GM_addStyle", "GM_setValue"], script.Grants);
        Assert.Equal("https://example.com/dark.meta.js", script.UpdateUrl);
    }

    [Fact]
    public void Keeps_the_body_and_drops_the_header()
    {
        var script = UserscriptParser.Parse(Sample);

        Assert.Contains("dataset.dark", script.Body);
        Assert.DoesNotContain("==UserScript==", script.Body);
    }

    [Fact]
    public void Falls_back_to_a_name_when_the_header_is_missing()
    {
        var script = UserscriptParser.Parse("console.log('hi');");

        Assert.Equal("Untitled script", script.Name);
        Assert.Empty(script.Matches);
        Assert.Contains("console.log", script.Body);
    }

    [Fact]
    public void Defaults_to_document_idle()
    {
        var script = UserscriptParser.Parse("""
            // ==UserScript==
            // @name Idle
            // @match https://example.com/*
            // ==/UserScript==
            console.log('x');
            """);

        Assert.Equal("document-idle", script.RunAt);
    }

    [Fact]
    public void Rejects_an_empty_source()
    {
        Assert.Throws<FormatException>(() => UserscriptParser.Parse("  "));
    }

    [Fact]
    public void Applies_only_when_a_pattern_matches()
    {
        var script = UserscriptParser.Parse(Sample);

        Assert.True(script.AppliesTo("https://web.whatsapp.com/"));
        Assert.True(script.AppliesTo("https://www.messenger.com/"));
        Assert.True(script.AppliesTo("https://example.com/chat/42"));
        Assert.False(script.AppliesTo("https://example.org/"));
    }
}

public sealed class UserscriptMatcherTests
{
    [Theory]
    [InlineData("https://example.com/*", "https://example.com/", true)]
    [InlineData("https://example.com/*", "https://example.com/a/b", true)]
    [InlineData("https://example.com/a/*", "https://example.com/a/b", true)]
    [InlineData("https://example.com/a/*", "https://example.com/b", false)]
    [InlineData("*://example.com/*", "http://example.com/x", true)]
    [InlineData("https://*.example.com/*", "https://example.com/", true)]
    [InlineData("https://*.example.com/*", "https://a.example.com/", true)]
    [InlineData("https://*.example.com/*", "https://badexample.com/", false)]
    [InlineData("https://example.com/*", "http://example.com/", false)]
    [InlineData("not a pattern", "https://example.com/", false)]
    public void Matches_tampermonkey_patterns(string pattern, string url, bool expected)
    {
        Assert.Equal(expected, UserscriptMatcher.Match(pattern, url));
    }

    [Theory]
    [InlineData("https://example.com/*", "https://example.com/chat", true)]
    [InlineData("*example.com*", "https://example.com/chat", true)]
    [InlineData("/^https:\\/\\/example\\.com\\//", "https://example.com/chat", true)]
    [InlineData("/[unclosed/", "https://example.com/chat", false)]
    public void Matches_include_globs_and_regex(string pattern, string url, bool expected)
    {
        Assert.Equal(expected, UserscriptMatcher.Include(pattern, url));
    }
}
