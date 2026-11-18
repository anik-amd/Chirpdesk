using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chirpdesk.Settings;

namespace Chirpdesk.Notifications;

/// <summary>
/// Renders the service's brand glyph on its accent circle for the toast's app-logo slot. The
/// padded, coloured circle reads correctly at the small size Windows uses and still works at
/// high DPI; unknown brands fall back to a white initial.
/// </summary>
internal static class ToastIcons
{
    private const int Canvas = 64;
    private const double Circle = 40;
    private const double GlyphSize = 24;

    public static string? ForService(string? definitionId, string serviceName, string? accent)
    {
        if (string.IsNullOrWhiteSpace(definitionId))
        {
            return null;
        }

        var color = ParseColour(accent);
        var key = accent is { Length: > 1 } ? accent.TrimStart('#') : "default";

        try
        {
            var folder = Path.Combine(AppPaths.LocalRoot, "toast-icons");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"v4-{definitionId}-{key}.png");

            if (File.Exists(path))
            {
                return path;
            }

            var visual = new DrawingVisual();

            using (var context = visual.RenderOpen())
            {
                context.DrawEllipse(
                    new SolidColorBrush(color),
                    null,
                    new Point(Canvas / 2d, Canvas / 2d),
                    Circle / 2d,
                    Circle / 2d);

                var glyph = ServiceIcons.Image(definitionId, Brushes.White);

                if (glyph is not null)
                {
                    var inset = (Canvas - GlyphSize) / 2;
                    context.DrawImage(glyph, new Rect(inset, inset, GlyphSize, GlyphSize));
                }
                else
                {
                    var initial = string.IsNullOrWhiteSpace(serviceName)
                        ? "?"
                        : char.ToUpperInvariant(serviceName.Trim()[0]).ToString();
                    var text = new FormattedText(
                        initial,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Semibold"),
                        18,
                        Brushes.White,
                        1.0);
                    context.DrawText(
                        text,
                        new Point((Canvas - text.Width) / 2, (Canvas - text.Height) / 2));
                }
            }

            var bitmap = new RenderTargetBitmap(Canvas, Canvas, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static Color ParseColour(string? accent)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(accent)
                && ColorConverter.ConvertFromString(accent) is Color color)
            {
                return color;
            }
        }
        catch (FormatException)
        {
        }

        return Color.FromRgb(0x6C, 0x6B, 0x62);
    }
}
