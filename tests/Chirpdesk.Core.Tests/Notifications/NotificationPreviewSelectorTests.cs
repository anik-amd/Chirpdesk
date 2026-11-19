using Chirpdesk.Core.Notifications;

namespace Chirpdesk.Core.Tests.Notifications;

public sealed class NotificationPreviewSelectorTests
{
    private static PreviewCandidate Candidate(string? name, string? body) => new(name, body);

    [Fact]
    public void Prefers_the_row_whose_name_matches_the_notification_title()
    {
        var candidates = new[]
        {
            Candidate("Other chat", "Later message"),
            Candidate("Anik", "Hello there"),
        };

        Assert.Equal("Hello there", NotificationPreviewSelector.Select("Anik", candidates));
    }

    [Fact]
    public void Compares_names_without_case_or_extra_spaces()
    {
        var candidates = new[] { Candidate("Anik   Ahmed", "Hi") };

        Assert.Equal("Hi", NotificationPreviewSelector.Select("anik ahmed", candidates));
    }

    [Fact]
    public void Accepts_a_partial_name_match()
    {
        var candidates = new[] { Candidate("Anik Ahmed", "Hi") };

        Assert.Equal("Hi", NotificationPreviewSelector.Select("Anik", candidates));
    }

    [Fact]
    public void Falls_back_to_the_first_unread_row_when_no_name_matches()
    {
        var candidates = new[]
        {
            Candidate("Work group", "Alice: standup in 5"),
            Candidate("Bob", "Unrelated"),
        };

        Assert.Equal("Alice: standup in 5", NotificationPreviewSelector.Select("Someone else", candidates));
    }

    [Fact]
    public void Returns_null_without_candidates()
    {
        Assert.Null(NotificationPreviewSelector.Select("Anik", Array.Empty<PreviewCandidate>()));
    }

    [Fact]
    public void Skips_rows_without_a_body_and_returns_null_when_none_remain()
    {
        var candidates = new[] { Candidate("Anik", "   "), Candidate("Bob", null) };

        Assert.Null(NotificationPreviewSelector.Select("Anik", candidates));
    }

    [Fact]
    public void Skips_an_empty_first_row_when_a_later_row_has_text()
    {
        var candidates = new[] { Candidate("Anik", " "), Candidate("Bob", "Yo") };

        Assert.Equal("Yo", NotificationPreviewSelector.Select("Anik", candidates));
    }

    [Fact]
    public void Collapses_whitespace_so_the_toast_stays_one_line()
    {
        var candidates = new[] { Candidate("Anik", "hello\n\nworld\tagain") };

        Assert.Equal("hello world again", NotificationPreviewSelector.Select("Anik", candidates));
    }

    [Fact]
    public void Caps_a_long_preview()
    {
        var candidates = new[] { Candidate("Anik", new string('x', NotificationPreviewSelector.MaxBodyLength + 40)) };

        var selected = NotificationPreviewSelector.Select("Anik", candidates);

        Assert.NotNull(selected);
        Assert.Equal(NotificationPreviewSelector.MaxBodyLength, selected!.Length);
    }

    [Fact]
    public void Uses_the_first_row_when_the_title_is_empty()
    {
        var candidates = new[] { Candidate("Anik", "Hi"), Candidate("Bob", "Yo") };

        Assert.Equal("Hi", NotificationPreviewSelector.Select(null, candidates));
    }
}
