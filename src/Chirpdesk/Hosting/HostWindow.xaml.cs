using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Chirpdesk.Interop;

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
        StateChanged += (_, _) => UpdateMaximizeGlyph();
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

    public void ShowPage()
    {
        PageLayer.Visibility = System.Windows.Visibility.Visible;

        if (!SystemParameters.ClientAreaAnimation)
        {
            PageLayer.Opacity = 1;
            return;
        }

        PageLayer.Opacity = 0;
        PageLayer.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        var slide = PageHost.RenderTransform as TranslateTransform ?? new TranslateTransform();
        PageHost.RenderTransform = slide;
        slide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    public void HidePage()
    {
        PageLayer.BeginAnimation(OpacityProperty, null);
        PageLayer.Opacity = 1;
        PageHost.RenderTransform = null;
        PageLayer.Visibility = System.Windows.Visibility.Collapsed;
    }

    /// <summary>Shows the overlay at 12 % from the top, horizontally centred (§12 budget).</summary>
    public void ShowCommandBar(System.Windows.Media.ImageSource? backdrop = null)
    {
        BarBackdrop.Source = backdrop;

        var height = ActualHeight > 0 ? ActualHeight : Height;
        var top = Math.Max(24, height * 0.12);
        BarHost.Margin = new Thickness(24, top, 24, 0);
        BarHost.MaxHeight = Math.Max(180, height - top - 24);
        BarLayer.Visibility = System.Windows.Visibility.Visible;

        _barHideToken++;

        if (!SystemParameters.ClientAreaAnimation)
        {
            BarLayer.Opacity = 1;
            return;
        }

        BarLayer.Opacity = 0;
        BarLayer.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        var slide = BarHost.RenderTransform as TranslateTransform ?? new TranslateTransform();
        BarHost.RenderTransform = slide;
        slide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    public void HideCommandBar()
    {
        if (!IsCommandBarOpen)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            CollapseCommandBar();
            return;
        }

        var token = ++_barHideToken;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        fade.Completed += (_, _) =>
        {
            if (token == _barHideToken)
            {
                CollapseCommandBar();
            }
        };

        BarLayer.BeginAnimation(OpacityProperty, fade);
    }

    private int _barHideToken;

    private void CollapseCommandBar()
    {
        BarLayer.BeginAnimation(OpacityProperty, null);
        BarLayer.Opacity = 1;
        BarLayer.Visibility = System.Windows.Visibility.Collapsed;
        BarBackdrop.Source = null;
        BarHost.RenderTransform = null;
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

        _visibilityToken++;
        BeginAnimation(OpacityProperty, null);

        if (SystemParameters.ClientAreaAnimation)
        {
            Opacity = 0;
        }
        else
        {
            Opacity = 1;
        }

        Show();
        Activate();

        if (SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        // Activate() alone loses to Windows' foreground lock when the request came from a
        // toast; asking directly is the only lever a desktop app has.
        return Interop.NativeMethods.SetForegroundWindow(Handle);
    }

    /// <summary>Hide without tearing anything down: the webviews keep running.</summary>
    public void HideToBackground()
    {
        if (!SystemParameters.ClientAreaAnimation || !IsVisible)
        {
            Hide();
            WindowChrome.MakeToolWindow(Handle);
            return;
        }

        var token = ++_visibilityToken;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        fade.Completed += (_, _) =>
        {
            if (token != _visibilityToken)
            {
                return;
            }

            Hide();
            Opacity = 1;
            BeginAnimation(OpacityProperty, null);
            WindowChrome.MakeToolWindow(Handle);
        };

        BeginAnimation(OpacityProperty, fade);
    }

    private int _visibilityToken;

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
    /// The shell owns maximize so taskbar placement, animation and restore bounds are native.
    /// WM_GETMINMAXINFO pins the window to the monitor work area, which keeps the title bar and
    /// caption buttons below a top taskbar instead of under it.
    /// </summary>
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void UpdateMaximizeGlyph() =>
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    /// <summary>A 2 px indeterminate bar along the title bar; pure WPF, never over the webview.</summary>
    public void SetBusy(bool busy)
    {
        if (busy)
        {
            BusyTrack.Visibility = Visibility.Visible;

            if (!SystemParameters.ClientAreaAnimation)
            {
                BusyShift.X = 0;
                return;
            }

            var travel = Math.Max(ActualWidth, 240) + 96;
            BusyShift.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(-96, travel, TimeSpan.FromMilliseconds(1100))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            return;
        }

        BusyShift.BeginAnimation(TranslateTransform.XProperty, null);
        BusyShift.X = 0;
        BusyTrack.Visibility = Visibility.Collapsed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowChrome.MakeToolWindow(Handle);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }

        EnableRoundedCorners();
        UpdateMaximizeGlyph();
    }

    /// <summary>Windows 11 rounds the frame itself; Windows 10 simply ignores the attribute.</summary>
    private void EnableRoundedCorners()
    {
        try
        {
            var preference = NativeMethods.DwmWindowCornerRound;
            _ = NativeMethods.DwmSetWindowAttribute(
                Handle,
                NativeMethods.DwmwaWindowCornerPreference,
                ref preference,
                sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: the square frame is the correct fallback.
        }
    }

    /// <summary>
    /// A borderless window maximises to the full monitor unless it is told otherwise, which is
    /// how a top taskbar ends up covering the caption buttons. Always answer with the monitor
    /// work area, in the physical pixels WM_GETMINMAXINFO expects.
    /// </summary>
    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NativeMethods.WmGetMinMaxInfo)
        {
            return nint.Zero;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return nint.Zero;
        }

        var info = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return nint.Zero;
        }

        var bounds = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
        bounds.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        bounds.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        bounds.MaxSize.X = info.Work.Right - info.Work.Left;
        bounds.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        bounds.MaxTrackSize.X = Math.Max(bounds.MaxTrackSize.X, bounds.MaxSize.X);
        bounds.MaxTrackSize.Y = Math.Max(bounds.MaxTrackSize.Y, bounds.MaxSize.Y);
        Marshal.StructureToPtr(bounds, lParam, fDeleteOld: true);

        handled = true;
        return nint.Zero;
    }

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
