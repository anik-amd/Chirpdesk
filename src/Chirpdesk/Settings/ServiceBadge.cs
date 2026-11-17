using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Chirpdesk.Settings;

/// <summary>A service's identity mark until real plugin artwork is installed: a coloured
/// monogram from the definition's accent.</summary>
internal static class ServiceBadge
{
    public static Border Create(string name, string? accent, double size = 28)
    {
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = BrushFor(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = Initial(name),
                Foreground = Brushes.White,
                FontSize = size * 0.42,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    public static string Initial(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.TrimStart()[0]).ToString();

    public static Brush BrushFor(string? accent)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(accent)
                && ColorConverter.ConvertFromString(accent) is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch (FormatException)
        {
            // Neutral fallback below.
        }

        return new SolidColorBrush(Color.FromRgb(0x6C, 0x6B, 0x62));
    }
}
