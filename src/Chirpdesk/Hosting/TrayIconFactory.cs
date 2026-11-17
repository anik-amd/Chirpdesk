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

    public static ImageSource CreateIcon(bool unread)
    {
        var name = unread ? "chirpdesk-tray-unread.ico" : "chirpdesk-tray.ico";
        var uri = new Uri(
            $"pack://application:,,,/Chirpdesk;component/Assets/{name}",
            UriKind.Absolute);
        var decoder = BitmapDecoder.Create(
            uri,
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        // The largest frame scales cleanly to the 16-32 px tray at every DPI.
        var frame = decoder.Frames.OrderByDescending(item => item.PixelWidth).First();
        frame.Freeze();
        return frame;
    }
}
