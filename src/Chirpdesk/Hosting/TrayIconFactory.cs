using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;

namespace Chirpdesk.Hosting;

/// <summary>Tray icon and its menu. The brand mark ships as a multi-size ICO asset.</summary>
internal static class TrayIconFactory
{
    public static TaskbarIcon Create(Action open, Action hide, Action settings, Action quit)
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, _) => open();
        menu.Items.Add(openItem);

        var hideItem = new MenuItem { Header = "Hide" };
        hideItem.Click += (_, _) => hide();
        menu.Items.Add(hideItem);

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => settings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => quit();
        menu.Items.Add(quitItem);

        var tray = new TaskbarIcon
        {
            ToolTipText = "Chirpdesk",
            IconSource = CreateIcon(unread: false),
            ContextMenu = menu,
        };

        tray.TrayLeftMouseUp += (_, _) => open();
        return tray;
    }

    private static BitmapSource? _normal;
    private static RenderTargetBitmap? _unread;

    public static ImageSource CreateIcon(bool unread)
    {
        if (unread)
        {
            return _unread ??= CreateUnreadIcon();
        }

        return _normal ??= LoadIcon("chirpdesk-tray.ico");
    }

    private static RenderTargetBitmap CreateUnreadIcon()
    {
        var baseIcon = LoadIcon("chirpdesk-tray.ico");
        const int size = 32;
        const double badgeRadius = 6;
        const double badgeCenterX = size - 9;
        const double badgeCenterY = 9;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(baseIcon, new Rect(0, 0, size, size));

            // A white ring keeps the badge legible on both light and dark taskbars.
            context.DrawEllipse(
                Brushes.White,
                null,
                new Point(badgeCenterX, badgeCenterY),
                badgeRadius + 1.5,
                badgeRadius + 1.5);

            context.DrawEllipse(
                new SolidColorBrush(Color.FromRgb(0xE4, 0x57, 0x4C)),
                null,
                new Point(badgeCenterX, badgeCenterY),
                badgeRadius,
                badgeRadius);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapFrame LoadIcon(string name)
    {
        var uri = new Uri(
            $"pack://application:,,,/Chirpdesk;component/Assets/{name}",
            UriKind.Absolute);
        var decoder = BitmapDecoder.Create(
            uri,
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        var frame = decoder.Frames.OrderByDescending(item => item.PixelWidth).First();
        frame.Freeze();
        return frame;
    }
}
