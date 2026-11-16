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

    /// <summary>Puts one webview in the window. The caller owns its lifetime.</summary>
    public void SetServiceContent(System.Windows.UIElement element)
    {
        ServiceLayer.Children.Clear();
        ServiceLayer.Children.Add(element);
    }

    public void ClearServiceContent() => ServiceLayer.Children.Clear();

    /// <summary>User-requested show: normal app window, taskbar button, Alt+Tab entry.</summary>
    public void ShowForUser()
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
        _ = Interop.NativeMethods.SetForegroundWindow(Handle);
    }

    /// <summary>Hide without tearing anything down: the webviews keep running.</summary>
    public void HideToBackground()
    {
        Hide();
        WindowChrome.MakeToolWindow(Handle);
    }

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
