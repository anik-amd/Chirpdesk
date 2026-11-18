using Chirpdesk.Interop;

namespace Chirpdesk.Hosting;

/// <summary>
/// Read-only frame probes used by diagnostics. The window is created as a normal app window
/// and the taskbar button is hidden through <see cref="TaskbarList"/>; toggling
/// <c>WS_EX_TOOLWINDOW</c> here used to suppress the native DWM transition (ADR 0015).
/// </summary>
internal static class WindowChrome
{
    public static bool IsToolWindow(nint hwnd) =>
        (NativeMethods.GetExtendedStyle(hwnd) & NativeMethods.WsExToolWindow) != 0;

    public static bool IsAppWindow(nint hwnd) =>
        (NativeMethods.GetExtendedStyle(hwnd) & NativeMethods.WsExAppWindow) != 0;
}
