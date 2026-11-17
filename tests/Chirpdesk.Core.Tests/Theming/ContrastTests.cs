using Chirpdesk.Core.Theming;

namespace Chirpdesk.Core.Tests.Theming;

public sealed class ContrastTests
{
    [Fact]
    public void Black_on_white_is_the_maximum_ratio()
    {
        Assert.Equal(21.0, Contrast.Ratio(new Rgb(0, 0, 0), new Rgb(255, 255, 255)), 2);
    }

    [Fact]
    public void A_colour_against_itself_is_one()
    {
        Assert.Equal(1.0, Contrast.Ratio(new Rgb(0x62, 0x6B, 0x67), new Rgb(0x62, 0x6B, 0x67)), 6);
    }

    [Fact]
    public void The_ratio_is_symmetric()
    {
        var foreground = new Rgb(0x62, 0x6B, 0x67);
        var background = new Rgb(0xED, 0xEA, 0xE2);

        Assert.Equal(
            Contrast.Ratio(foreground, background),
            Contrast.Ratio(background, foreground),
            6);
    }

    [Fact]
    public void The_light_muted_token_pair_clears_AA()
    {
        var ratio = Contrast.Ratio(new Rgb(0x62, 0x6B, 0x67), new Rgb(0xED, 0xEA, 0xE2));

        Assert.True(ratio >= 4.5, $"expected >= 4.5, got {ratio:0.00}");
    }

    [Theory]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("ffffff", 255, 255, 255)]
    [InlineData(" #1D2422 ", 0x1D, 0x24, 0x22)]
    public void Hex_parsing_accepts_six_digit_colours(string text, byte r, byte g, byte b)
    {
        Assert.True(Rgb.TryParseHex(text, out var rgb));
        Assert.Equal(new Rgb(r, g, b), rgb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#F510171A")]
    [InlineData("#GGGGGG")]
    public void Hex_parsing_refuses_anything_else(string? text)
    {
        Assert.False(Rgb.TryParseHex(text, out _));
    }
}
