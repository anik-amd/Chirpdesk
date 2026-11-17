using System.Runtime.InteropServices;

namespace Chirpdesk.Interop;

/// <summary>Every P/Invoke lives here. Nothing else in the app declares one.</summary>
internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x0000_0080L;
    internal const long WsExAppWindow = 0x0004_0000L;

    private const int VkControl = 0x11;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int GetCurrentProcessExplicitAppUserModelID(out nint appUserModelId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>
    /// Used by the Command Bar to commit on Ctrl release; WebView2 never delivers that key-up
    /// (ADR 0002). Polled, not hooked, so no input goes through the app.
    /// </summary>
    internal static bool IsControlDown() => (GetAsyncKeyState(VkControl) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal static long GetExtendedStyle(nint hwnd) =>
        nint.Size == 8
            ? GetWindowLongPtr64(hwnd, GwlExStyle)
            : GetWindowLong32(hwnd, GwlExStyle);

    internal static void SetExtendedStyle(nint hwnd, long style)
    {
        if (nint.Size == 8)
        {
            _ = SetWindowLongPtr64(hwnd, GwlExStyle, (nint)style);
        }
        else
        {
            _ = SetWindowLong32(hwnd, GwlExStyle, unchecked((int)style));
        }
    }
}
