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
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Hosts non-visual framework elements (the tray icon) so they get a Loaded event.</summary>
    public void Attach(System.Windows.UIElement element) => ContentRoot.Children.Add(element);

    /// <summary>
    /// Puts one webview in the window, replacing whatever was there. The caller owns its
    /// lifetime; the replaced view stays alive, it is only detached. Already being the only
    /// child is a no-op, so re-showing the active service does not reload its page.
    /// </summary>
    public void SetServiceContent(System.Windows.UIElement element)
    {
        if (ServiceLayer.Children.Count == 1 && ReferenceEquals(ServiceLayer.Children[0], element))
        {
            return;
        }

        ServiceLayer.Children.Clear();
        ServiceLayer.Children.Add(element);
    }

    /// <summary>Detaches one webview without touching another instance's content.</summary>
    public void RemoveServiceContent(System.Windows.UIElement element) => ServiceLayer.Children.Remove(element);

    /// <summary>Mounts the Command Bar once; the caller owns its lifetime.</summary>
    public void SetCommandBar(System.Windows.UIElement element) => BarHost.Content = element;

    public bool IsCommandBarOpen => BarLayer.Visibility == System.Windows.Visibility.Visible;

    /// <summary>A page (settings/plugins) occupies the content area instead of the webview.</summary>
    public bool IsPageOpen => PageLayer.Visibility == System.Windows.Visibility.Visible;

    public void SetPage(System.Windows.UIElement page) => PageHost.Content = page;

    public void ShowPage() => PageLayer.Visibility = System.Windows.Visibility.Visible;

    public void HidePage() => PageLayer.Visibility = System.Windows.Visibility.Collapsed;

    /// <summary>Shows the overlay at 12 % from the top, horizontally centred (§12 budget).</summary>
    public void ShowCommandBar(System.Windows.Media.ImageSource? backdrop = null)
    {
        BarBackdrop.Source = backdrop;

        var height = ActualHeight > 0 ? ActualHeight : Height;
        BarHost.Margin = new Thickness(0, Math.Max(24, height * 0.12), 0, 0);
        BarLayer.Visibility = System.Windows.Visibility.Visible;
    }

    public void HideCommandBar()
    {
        BarLayer.Visibility = System.Windows.Visibility.Collapsed;
        BarBackdrop.Source = null;
    }

    public bool IsDark { get; private set; }

    /// <summary>
    /// One palette, two ends. The window background is the only chrome that exists before the
    /// title bar lands, so it is also the visible proof that the theme was applied.
    /// </summary>
    public void SetDark(bool dark)
    {
        IsDark = dark;

        Background = (System.Windows.Media.Brush)FindResource("CdPalette");

        var bar = (System.Windows.Media.Brush)FindResource("CdPaletteRaised");
        var text = (System.Windows.Media.Brush)FindResource("CdPaletteText");

        TitleBar.Background = bar;
        TitleText.Foreground = text;
        MinimizeButton.Foreground = text;
        MaximizeButton.Foreground = text;
        CloseButton.Foreground = text;
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

    /// <summary>
    /// Sizes the window to the work area instead of letting the shell maximise a
    /// <c>WindowStyle=None</c> window, which pushes the custom title bar off-screen. Bounds are
    /// remembered so restoring returns to exactly where the user had it.
    /// </summary>
    private void ToggleMaximize()
    {
        if (_restore is null)
        {
            _restore = new Rect(Left, Top, Width, Height);

            var area = SystemParameters.WorkArea;
            Left = area.Left;
            Top = area.Top;
            Width = area.Width;
            Height = area.Height;
            MaximizeButton.Content = "\uE923";
        }
        else
        {
            var bounds = _restore.Value;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            _restore = null;
            MaximizeButton.Content = "\uE922";
        }
    }

    private Rect? _restore;

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
