using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;

namespace Chirpdesk.Hosting;

/// <summary>Tray icon and its menu. Icons are drawn, not shipped as assets.</summary>
internal static class TrayIconFactory
{
    public static TaskbarIcon Create(Action open, Action hide, Action quit)
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, _) => open();
        menu.Items.Add(openItem);

        var hideItem = new MenuItem { Header = "Hide" };
        hideItem.Click += (_, _) => hide();
        menu.Items.Add(hideItem);

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

    public static ImageSource CreateIcon(bool unread)
    {
        var group = new DrawingGroup();

        using (var context = group.Open())
        {
            var body = new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDC));
            context.DrawGeometry(body, null, new EllipseGeometry(new Point(8, 8), 6.5, 6.5));

            if (unread)
            {
                var dot = new SolidColorBrush(Color.FromRgb(0xF2, 0x5C, 0x4B));
                context.DrawGeometry(dot, null, new EllipseGeometry(new Point(12.5, 3.5), 3, 3));
            }
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
