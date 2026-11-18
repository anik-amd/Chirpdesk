using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chirpdesk.Settings;

namespace Chirpdesk.Notifications;

/// <summary>
/// Renders the service's vector brand glyph to a small PNG for the toast's app-logo slot.
/// Windows toasts need a file URI, so the image is cached under LocalAppData.
/// </summary>
internal static class ToastIcons
{
    private const int Size = 64;

    public static string? ForService(string? definitionId, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(definitionId))
        {
            return null;
        }

        var source = ServiceIcons.Image(definitionId, null);

        if (source is null)
        {
            return null;
        }

        try
        {
            var folder = Path.Combine(AppPaths.LocalRoot, "toast-icons");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{definitionId}.png");

            if (File.Exists(path))
            {
                return path;
            }

            var visual = new DrawingVisual();

            using (var context = visual.RenderOpen())
            {
                context.DrawImage(source, new Rect(0, 0, Size, Size));
            }

            var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
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
}
