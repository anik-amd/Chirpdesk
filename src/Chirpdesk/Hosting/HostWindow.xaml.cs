using System.Windows;
using System.Windows.Interop;

namespace Chirpdesk.Hosting;

/// <summary>
/// The only window. Created off-screen and out of the taskbar at boot so Chromium's composition
/// target stays alive; moved on-screen only because the user asked for it.
/// </summary>
public partial class HostWindow : Window
{
    public HostWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => MaximizeButton.Content =
            WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Hosts non-visual framework elements (the tray icon) so they get a Loaded event.</summary>
    public void Attach(System.Windows.UIElement element) => ContentRoot.Children.Add(element);

    /// <summary>Puts one webview in the window. The caller owns its lifetime.</summary>
    public void SetServiceContent(System.Windows.UIElement element)
    {
        ServiceLayer.Children.Clear();
        ServiceLayer.Children.Add(element);
    }

    public void ClearServiceContent() => ServiceLayer.Children.Clear();

    /// <summary>
    /// One palette, two ends. The window background is the only chrome that exists before the
    /// title bar lands, so it is also the visible proof that the theme was applied.
    /// </summary>
    public void SetDark(bool dark)
    {
        Background = new System.Windows.Media.SolidColorBrush(
            dark
                ? System.Windows.Media.Color.FromRgb(0x11, 0x12, 0x14)
                : System.Windows.Media.Color.FromRgb(0xF6, 0xF5, 0xF3));

        var bar = dark
            ? System.Windows.Media.Color.FromRgb(0x1A, 0x17, 0x1C)
            : System.Windows.Media.Color.FromRgb(0xEA, 0xE7, 0xE3);
        var text = dark
            ? System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xCC)
            : System.Windows.Media.Color.FromRgb(0x33, 0x30, 0x36);

        TitleBar.Background = new System.Windows.Media.SolidColorBrush(bar);
        TitleText.Foreground = new System.Windows.Media.SolidColorBrush(text);
        MinimizeButton.Foreground = new System.Windows.Media.SolidColorBrush(text);
        MaximizeButton.Foreground = new System.Windows.Media.SolidColorBrush(text);
        CloseButton.Foreground = new System.Windows.Media.SolidColorBrush(text);
    }

    /// <summary>
    /// User-requested show: normal app window, taskbar button, Alt+Tab entry.
    /// Returns whether Windows actually granted the foreground, which is the part that
    /// silently fails when the request comes from the shell rather than from this process.
    /// </summary>
    public bool ShowForUser()
    {
        WindowChrome.MakeAppWindow(Handle);

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsOnScreen())
        {
            PlaceOnScreen();
        }

        Show();
        Activate();

        // Activate() alone loses to Windows' foreground lock when the request came from a
        // toast; asking directly is the only lever a desktop app has.
        return Interop.NativeMethods.SetForegroundWindow(Handle);
    }

    /// <summary>Hide without tearing anything down: the webviews keep running.</summary>
    public void HideToBackground()
    {
        Hide();
        WindowChrome.MakeToolWindow(Handle);
    }

    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>Close means hide: the product is a tray app and the webviews must survive.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnSourceInitialized(object? sender, EventArgs e) => WindowChrome.MakeToolWindow(Handle);

    private bool IsOnScreen() => Left > -10_000 && Top > -10_000;

    /// <summary>Compact by requirement: the app is used at a small desk.</summary>
    private void PlaceOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(980, area.Width - 80);
        Height = Math.Min(720, area.Height - 80);
        Left = area.Left + ((area.Width - Width) / 2);
        Top = area.Top + ((area.Height - Height) / 2);
    }
}
