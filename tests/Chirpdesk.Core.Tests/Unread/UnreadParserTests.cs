using Chirpdesk.Core.Unread;

namespace Chirpdesk.Core.Tests.Unread;

public class UnreadParserTests
{
    [Theory]
    [InlineData("(3) WhatsApp", @"\((\d+)\)", 3)]
    [InlineData("WhatsApp (12)", @"\((\d+)\)", 12)]
    [InlineData("WhatsApp", @"\((\d+)\)", 0)]
    [InlineData("(1) Telegram (9)", @"\((\d+)\)", 1)]
    [InlineData("99+ unread", @"(\d+)\+", 99)]
    [InlineData(null, @"\((\d+)\)", 0)]
    [InlineData("(abc) x", @"\((\d+)\)", 0)]
    public void Parses_first_capture_group(string? title, string pattern, int expected)
        => Assert.Equal(expected, UnreadParser.Parse(title, pattern));

    [Theory]
    [InlineData("(5) WhatsApp", "")]          // empty pattern
    [InlineData("(5) WhatsApp", null)]        // null pattern
    [InlineData("(5) WhatsApp", "(\\d+")]     // malformed regex: fail open, never throw
    [InlineData("(5) WhatsApp", @"\(\d+\)")]  // no capture group
    public void Returns_zero_instead_of_throwing(string? title, string? pattern)
        => Assert.Equal(0, UnreadParser.Parse(title, pattern));

    [Fact]
    public void Clamps_absurd_counts_to_zero_rather_than_overflowing()
        => Assert.Equal(0, UnreadParser.Parse("(99999999999999999999) x", @"\((\d+)\)"));
}
