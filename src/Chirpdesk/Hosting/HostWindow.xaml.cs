using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Chirpdesk.Interop;
using Chirpdesk.Settings;
using Chirpdesk.Ui;

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
        StateChanged += (_, _) => OnStateChanged();
        SizeChanged += (_, _) => UpdateBarLayout();

        _railTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _railTimer.Tick += (_, _) =>
        {
            _railTimer.Stop();
            if (_railMode == ServiceRailMode.Hover && !RailPanel.IsMouseOver)
            {
                HideRail(animate: true);
            }
        };

        RailPanel.MouseEnter += (_, _) => _railTimer.Stop();
        RailPanel.MouseLeave += (_, _) => ScheduleRailHide();

        _railHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _railHoverTimer.Tick += (_, _) => CheckRailHover();
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    private readonly DispatcherTimer _railHoverTimer;

    /// <summary>Rebuilds the service rail from the warm instances and the current active tab.</summary>
    public void SetServiceRail(IReadOnlyList<ServiceRailEntry> entries, string? activeId)
    {
        RailServices.Children.Clear();

        foreach (var entry in entries)
        {
            var button = new Button
            {
                Style = (Style)FindResource("RailButton"),
                ToolTip = entry.Name,
                Background = string.Equals(entry.InstanceId, activeId, StringComparison.OrdinalIgnoreCase)
                    ? Palette("CdPaletteSelection")
                    : Brushes.Transparent,
            };
            AutomationProperties.SetName(button, entry.Name);

            var content = new Grid { Width = 40, Height = 40 };
            var icon = ServiceIcons.Create(entry.DefinitionId, entry.Name, entry.Accent, 26);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);

            if (entry.ShowOrdinal)
            {
                var badge = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(8),
                    Background = Palette("CdPaletteAccent"),
                    BorderBrush = Palette("CdPaletteRaised"),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                badge.Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(entry.Name)
                        ? entry.Ordinal.ToString(CultureInfo.InvariantCulture)
                        : char.ToUpperInvariant(entry.Name.Trim()[0]).ToString(),
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                    Foreground = Palette("CdPaletteSurface"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                content.Children.Add(badge);
            }

            button.Content = content;
            button.Width = 40;
            button.Height = 40;
            button.Margin = new Thickness(0);
            button.Click += (_, _) => RailServiceSelected?.Invoke(this, entry.InstanceId);

            var close = new Button
            {
                Content = "\u00D7",
                Style = (Style)FindResource("RailCloseButton"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Close account",
                Visibility = Visibility.Collapsed,
            };
            close.Click += (_, _) => RailServiceCloseRequested?.Invoke(this, entry.InstanceId);
            AutomationProperties.SetName(close, $"Close {entry.Name}");

            var container = new Grid
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 0, 8),
            };
            container.Children.Add(button);
            container.Children.Add(close);
            container.MouseEnter += (_, _) => close.Visibility = Visibility.Visible;
            container.MouseLeave += (_, _) => close.Visibility = Visibility.Collapsed;
            RailServices.Children.Add(container);
        }

        // The add button belongs with the tabs, not at the bottom of the sidebar.
        RailServices.Children.Add(RailAction("plus", "Add a chat", (_, _) => RailAddRequested?.Invoke(this, EventArgs.Empty), accent: true));

        RailActions.Children.Clear();
        RailActions.Children.Add(RailAction("search", "Command palette (Ctrl+K)", (_, _) => RailSearchRequested?.Invoke(this, EventArgs.Empty)));
        RailActions.Children.Add(RailAction("plugins", "Plugins & services", (_, _) => RailPluginsRequested?.Invoke(this, EventArgs.Empty)));
        RailActions.Children.Add(RailAction("settings", "Settings", (_, _) => RailSettingsRequested?.Invoke(this, EventArgs.Empty)));
        RailActions.Children.Add(RailAction("info", "About Chirpdesk", (_, _) => RailAboutRequested?.Invoke(this, EventArgs.Empty)));
    }

    public void SetRailMode(string? mode)
    {
        _railMode = mode?.Trim().ToLowerInvariant() switch
        {
            "hidden" => ServiceRailMode.Hidden,
            "hover" => ServiceRailMode.Hover,
            _ => ServiceRailMode.Always,
        };

        ApplyRailMode();

        if (_railMode == ServiceRailMode.Hover)
        {
            _railHoverTimer.Start();
        }
        else
        {
            _railHoverTimer.Stop();
        }
    }

    private void ApplyRailMode()
    {
        switch (_railMode)
        {
            case ServiceRailMode.Always:
                RailColumn.Width = GridLength.Auto;
                RailPanel.Visibility = Visibility.Visible;
                RailPanel.Width = 62;
                RailPanel.Opacity = 1;
                _railVisible = true;
                break;

            case ServiceRailMode.Hover:
                RailColumn.Width = GridLength.Auto;
                HideRail(animate: false);
                break;

            default:
                RailColumn.Width = new GridLength(0);
                RailPanel.BeginAnimation(WidthProperty, null);
                RailPanel.Width = 0;
                RailPanel.Opacity = 0;
                RailPanel.Visibility = Visibility.Collapsed;
                _railVisible = false;
                break;
        }
    }

    private void ShowRail(bool animate)
    {
        if (_railMode == ServiceRailMode.Hidden)
        {
            return;
        }

        _railVisible = true;
        RailPanel.Visibility = Visibility.Visible;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            RailPanel.BeginAnimation(WidthProperty, null);
            RailPanel.Width = 62;
            RailPanel.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var width = new DoubleAnimation(RailPanel.ActualWidth, 62, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = ease,
        };
        width.Completed += (_, _) =>
        {
            RailPanel.BeginAnimation(WidthProperty, null);
            RailPanel.Width = 62;
        };
        RailPanel.BeginAnimation(WidthProperty, width);

        RailPanel.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            RailPanel.BeginAnimation(OpacityProperty, null);
            RailPanel.Opacity = 1;
        };
        RailPanel.BeginAnimation(OpacityProperty, fade);
    }

    private void HideRail(bool animate)
    {
        if (_railMode == ServiceRailMode.Always)
        {
            return;
        }

        _railVisible = false;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            RailPanel.BeginAnimation(WidthProperty, null);
            RailPanel.Width = 0;
            RailPanel.Opacity = 0;
            RailPanel.Visibility = Visibility.Collapsed;

            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var width = new DoubleAnimation(RailPanel.ActualWidth, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = ease,
        };
        width.Completed += (_, _) =>
        {
            if (_railVisible || _railMode != ServiceRailMode.Hover)
            {
                return;
            }

            RailPanel.BeginAnimation(WidthProperty, null);
            RailPanel.Width = 0;
            RailPanel.BeginAnimation(OpacityProperty, null);
            RailPanel.Opacity = 0;
            RailPanel.Visibility = Visibility.Collapsed;
        };
        RailPanel.BeginAnimation(WidthProperty, width);

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
        RailPanel.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// WebView2 is an HWND, so it swallows mouse input at the left edge. Poll the real cursor
    /// against the window bounds instead: no reserved gutter, no invisible strip.
    /// </summary>
    private void CheckRailHover()
    {
        if (_railMode != ServiceRailMode.Hover || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        System.Windows.Point origin;
        double bottom;

        try
        {
            origin = PointToScreen(new System.Windows.Point(0, 0));
            bottom = PointToScreen(new System.Windows.Point(0, ActualHeight)).Y;
        }
        catch (InvalidOperationException)
        {
            _railHoverTimer.Stop();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var left = origin.X;
        var insideVertical = cursor.Y >= origin.Y && cursor.Y <= bottom;

        if (_railVisible)
        {
            var right = left + 62 * dpi.DpiScaleX;
            if (!insideVertical || cursor.X < left || cursor.X > right)
            {
                ScheduleRailHide();
            }

            return;
        }

        var threshold = left + 8 * dpi.DpiScaleX;
        if (insideVertical && cursor.X >= left && cursor.X <= threshold)
        {
            _railTimer.Stop();
            ShowRail(animate: true);
        }
    }

    private void ScheduleRailHide()
    {
        if (_railMode == ServiceRailMode.Hover && !_railTimer.IsEnabled)
        {
            _railTimer.Start();
        }
    }

    private Button RailAction(string icon, string tooltip, RoutedEventHandler click, bool accent = false)
    {
        var button = new Button
        {
            Style = (Style)FindResource("RailButton"),
            ToolTip = tooltip,
            Content = UiIcons.Create(icon, Palette(accent ? "CdPaletteAccent" : "CdPaletteText"), 20),
        };
        AutomationProperties.SetName(button, tooltip);
        button.Click += click;
        return button;
    }

    private Brush Palette(string key) => (Brush)FindResource(key);

    private readonly DispatcherTimer _railTimer;
    private ServiceRailMode _railMode = ServiceRailMode.Always;
    private bool _railVisible;

    /// <summary>The row of open services on the left edge, plus the three global actions.</summary>
    public enum ServiceRailMode
    {
        Always,
        Hover,
        Hidden,
    }

    public sealed record ServiceRailEntry(
        string InstanceId,
        string Name,
        string? DefinitionId,
        string? Accent,
        int Ordinal,
        bool ShowOrdinal);

    public event EventHandler<string>? RailServiceSelected;

    /// <summary>Raised when the user closes (cools) one account from the sidebar.</summary>
    public event EventHandler<string>? RailServiceCloseRequested;
    public event EventHandler? RailSearchRequested;
    public event EventHandler? RailAddRequested;
    public event EventHandler? RailPluginsRequested;
    public event EventHandler? RailSettingsRequested;
    public event EventHandler? RailAboutRequested;

    /// <summary>Set from settings; minimize hides to the tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Set from settings; close hides to the tray instead of quitting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Raised when the minimize button should hide to the tray.</summary>
    public event EventHandler? HideToTrayRequested;

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
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        fade.Completed += (_, _) =>
        {
            PageLayer.BeginAnimation(OpacityProperty, null);
            PageLayer.Opacity = 1;
        };

        PageLayer.BeginAnimation(OpacityProperty, fade);

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
        BarBackdrop.UpdateLayout();

        BarLayer.Visibility = System.Windows.Visibility.Visible;
        UpdateBarLayout();

        _barHideToken++;

        if (!SystemParameters.ClientAreaAnimation)
        {
            BarLayer.Opacity = 1;
            return;
        }

        BarLayer.Opacity = 1;
        BarLayer.BeginAnimation(OpacityProperty, null);

        var scale = new ScaleTransform(0.985, 0.985);
        var slide = new TranslateTransform(0, -10);
        BarHost.RenderTransformOrigin = new Point(0.5, 0.5);
        BarHost.RenderTransform = new TransformGroup { Children = { scale, slide } };

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(210);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, duration) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, duration) { EasingFunction = ease });

        BarHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
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

    private void UpdateBarLayout()
    {
        if (!IsCommandBarOpen)
        {
            return;
        }

        var height = ActualHeight > 0 ? ActualHeight : Height;
        var top = Math.Max(24, height * 0.12);
        BarHost.Margin = new Thickness(24, top, 24, 0);
        BarHost.MaxHeight = Math.Max(180, height - top - 24);
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
        TaskbarList.Add(Handle);

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
        Opacity = 1;

        if (_railMode == ServiceRailMode.Hover)
        {
            _railHoverTimer.Start();
        }

        // The startup show is deliberately non-activating so boot cannot steal focus. A
        // user-requested show is the opposite: activate from the first frame or Windows
        // skips the native DWM transition on the very window that is being revealed.
        ShowActivated = true;
        Show();
        Activate();

        // Activate() alone loses to Windows' foreground lock when the request came from a
        // toast; asking directly is the only lever a desktop app has.
        return Interop.NativeMethods.SetForegroundWindow(Handle);
    }

    /// <summary>Hide without tearing anything down: the webviews keep running.</summary>
    public void HideToBackground()
    {
        _railHoverTimer.Stop();
        TaskbarList.Remove(Handle);

        Hide();
        Opacity = 1;
        BeginAnimation(OpacityProperty, null);
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

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (MinimizeToTray)
        {
            HideToTrayRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>Close means hide by default; settings can turn it into a real quit.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A user-facing maximize that animates the window bounds instead of snapping. The shell
    /// still fixes OS maximize through WM_GETMINMAXINFO; this path keeps the title bar and its
    /// buttons inside the work area when the taskbar is at the top.
    /// </summary>
    public void ToggleMaximizeForTest() => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeGlyph() =>
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    private void OnStateChanged()
    {
        UpdateMaximizeGlyph();

        // The 4 px gutter on the right and bottom is what makes those edges resizable while a
        // WebView2 child covers the client. A maximized window must not show a gap.
        if (ContentFrame is not null)
        {
            ContentFrame.Margin = WindowState == WindowState.Maximized
                ? new Thickness(0)
                : new Thickness(0, 0, 4, 4);
        }
    }

    /// <summary>A 2 px indeterminate bar along the title bar; pure WPF, never over the webview.</summary>
    public void SetBusy(bool busy)
    {
        if (busy)
        {
            BusyTrack.Visibility = Visibility.Visible;

            if (!IsVisible || !SystemParameters.ClientAreaAnimation)
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
        ApplyNativeFrameStyle();
        TaskbarList.Remove(Handle);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }

        EnableRoundedCorners();
        UpdateMaximizeGlyph();
    }

    /// <summary>
    /// <c>WindowStyle=None</c> leaves the HWND without <c>WS_CAPTION</c>, which is the style
    /// Windows uses to decide that a top-level window participates in the normal caption
    /// minimise/maximise/restore transitions. Add the caption and popup bits without letting
    /// the native caption paint: WM_NCCALCSIZE below makes the client cover the frame.
    /// </summary>
    private void ApplyNativeFrameStyle()
    {
        var style = NativeMethods.GetStyle(Handle);
        style |= NativeMethods.WsPopup
            | NativeMethods.WsCaption
            | NativeMethods.WsThickFrame
            | NativeMethods.WsSysMenu
            | NativeMethods.WsMinimizeBox
            | NativeMethods.WsMaximizeBox;

        NativeMethods.SetStyle(Handle, style);
        _ = NativeMethods.SetWindowPos(
            Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove
                | NativeMethods.SwpNoSize
                | NativeMethods.SwpNoZorder
                | NativeMethods.SwpNoActivate
                | NativeMethods.SwpFrameChanged);
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

            // Windows 11 otherwise paints the active accent colour around the frame. The
            // border is not part of our custom chrome, so remove it rather than leave a
            // thick blue edge above the title bar.
            var border = NativeMethods.DwmColorNone;
            _ = NativeMethods.DwmSetWindowAttribute(
                Handle,
                NativeMethods.DwmwaBorderColor,
                ref border,
                sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: the square frame is the correct fallback.
        }
    }

    /// <summary>
    /// Resize edges for the borderless frame. Returning HTCLIENT everywhere else keeps the
    /// custom title-bar buttons and WPF drag handling in charge.
    /// </summary>
    private nint HitTest(nint lParam, ref bool handled)
    {
        if (WindowState == WindowState.Maximized)
        {
            return nint.Zero;
        }

        var value = lParam.ToInt64();
        var screenX = (short)(value & 0xFFFF);
        var screenY = (short)((value >> 16) & 0xFFFF);

        if (!NativeMethods.GetWindowRect(Handle, out var rect))
        {
            return nint.Zero;
        }

        var border = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var left = screenX >= rect.Left && screenX < rect.Left + border;
        var right = screenX < rect.Right && screenX >= rect.Right - border;
        var top = screenY >= rect.Top && screenY < rect.Top + border;
        var bottom = screenY < rect.Bottom && screenY >= rect.Bottom - border;

        var hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => NativeMethods.HtTopLeft,
            (_, true, true, _) => NativeMethods.HtTopRight,
            (true, _, _, true) => NativeMethods.HtBottomLeft,
            (_, true, _, true) => NativeMethods.HtBottomRight,
            (true, _, _, _) => NativeMethods.HtLeft,
            (_, true, _, _) => NativeMethods.HtRight,
            (_, _, true, _) => NativeMethods.HtTop,
            (_, _, _, true) => NativeMethods.HtBottom,
            _ => NativeMethods.HtClient,
        };

        if (hit == NativeMethods.HtClient)
        {
            return nint.Zero;
        }

        handled = true;
        return (nint)hit;
    }

    /// <summary>
    /// A borderless window maximises to the full monitor unless it is told otherwise, which is
    /// how a top taskbar ends up covering the caption buttons. Always answer with the monitor
    /// work area, in the physical pixels WM_GETMINMAXINFO expects.
    /// </summary>
    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmNcCalcSize && wParam != IntPtr.Zero)
        {
            // The caption and thick-frame bits stay in the style so Windows keeps its
            // window-management behaviour, but the client fills the frame. When maximized,
            // the window rect overhangs the work area by the frame margin; pin the client
            // rect to the work area so the page reaches every screen edge.
            if (WindowState == WindowState.Maximized)
            {
                var sizeMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
                var sizeInfo = new NativeMethods.MonitorInfo
                {
                    Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
                };

                if (sizeMonitor != nint.Zero && NativeMethods.GetMonitorInfo(sizeMonitor, ref sizeInfo))
                {
                    var calc = Marshal.PtrToStructure<NativeMethods.NcCalcSizeParams>(lParam);
                    calc.Rgrc0 = sizeInfo.Work;
                    Marshal.StructureToPtr(calc, lParam, fDeleteOld: false);
                }
            }

            handled = true;
            return nint.Zero;
        }

        if (message == NativeMethods.WmNcHitTest)
        {
            return HitTest(lParam, ref handled);
        }

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
