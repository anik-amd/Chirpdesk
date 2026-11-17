using System.Globalization;

namespace Chirpdesk.Core.Theming;

/// <summary>An sRGB colour, one byte per channel. Pure data, no WPF dependency.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Accepts six-digit hex with or without a leading <c>#</c>; alpha is not a token colour.</summary>
    public static bool TryParseHex(string? text, out Rgb rgb)
    {
        rgb = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length != 6
            || !byte.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(value[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        rgb = new Rgb(r, g, b);
        return true;
    }
}

/// <summary>
/// WCAG 2 contrast maths for the token dictionaries. The gate asserts AA (4.5:1 for normal text)
/// instead of trusting that a palette tweak kept the pairs readable.
/// </summary>
public static class Contrast
{
    public static double Luminance(Rgb color) =>
        0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    public static double Ratio(Rgb foreground, Rgb background)
    {
        var first = Luminance(foreground);
        var second = Luminance(background);
        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Channel(byte value)
    {
        var channel = value / 255.0;
        return channel <= 0.03928
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
