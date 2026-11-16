using Chirpdesk.Interop;

namespace Chirpdesk.Hosting;

/// <summary>
/// Show/hide is done with extended window styles only.
/// <c>ShowInTaskbar</c> must never change after creation: WPF recreates the HWND when it does,
/// which would destroy every hosted WebView2. A window created with <c>ShowInTaskbar=false</c>
/// also gets a hidden owner that keeps suppressing the taskbar button afterwards, so the host
/// is created with it enabled and hidden purely by <c>WS_EX_TOOLWINDOW</c>.
/// </summary>
internal static class WindowChrome
{
    public static void MakeToolWindow(nint hwnd)
    {
        var style = NativeMethods.GetExtendedStyle(hwnd);
        style |= NativeMethods.WsExToolWindow;
        style &= ~NativeMethods.WsExAppWindow;
        NativeMethods.SetExtendedStyle(hwnd, style);
    }

    public static void MakeAppWindow(nint hwnd)
    {
        var style = NativeMethods.GetExtendedStyle(hwnd);
        style &= ~NativeMethods.WsExToolWindow;
        style |= NativeMethods.WsExAppWindow;
        NativeMethods.SetExtendedStyle(hwnd, style);
    }

    public static bool IsToolWindow(nint hwnd) =>
        (NativeMethods.GetExtendedStyle(hwnd) & NativeMethods.WsExToolWindow) != 0;

    public static bool IsAppWindow(nint hwnd) =>
        (NativeMethods.GetExtendedStyle(hwnd) & NativeMethods.WsExAppWindow) != 0;
}
