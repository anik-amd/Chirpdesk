using System.Runtime.InteropServices;

namespace Chirpdesk.Interop;

/// <summary>Every P/Invoke lives here. Nothing else in the app declares one.</summary>
internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x0000_0080L;
    internal const long WsExAppWindow = 0x0004_0000L;

    private const int VkControl = 0x11;
    private const int VkShift = 0x10;

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

    [DllImport("user32.dll")]
    internal static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    internal const int AwHide = 0x0001_0000;
    internal const int AwBlend = 0x0002_0000;
    internal const uint SwpNoZorder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AnimateWindow(nint hWnd, int dwTime, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    internal const int WmGetMinMaxInfo = 0x0024;
    internal const uint MonitorDefaultToNearest = 0x0000_0002;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmWindowCornerRound = 2;

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public WindowRect Monitor;
        public WindowRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Used by the Command Bar to commit on Ctrl release; WebView2 never delivers that key-up
    /// (ADR 0002). Polled, not hooked, so no input goes through the app.
    /// </summary>
    internal static bool IsControlDown() => IsKeyDown(VkControl);

    internal static bool IsShiftDown() => IsKeyDown(VkShift);

    internal static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

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
