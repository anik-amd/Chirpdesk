using System.Runtime.InteropServices;

namespace Chirpdesk.Interop;

/// <summary>Every P/Invoke lives here. Nothing else in the app declares one.</summary>
internal static class NativeMethods
{
    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x0000_0080L;
    internal const long WsExAppWindow = 0x0004_0000L;

    internal const long WsPopup = 0x8000_0000L;
    internal const long WsCaption = 0x00C0_0000L;
    internal const long WsSysMenu = 0x0008_0000L;
    internal const long WsThickFrame = 0x0004_0000L;
    internal const long WsMinimizeBox = 0x0002_0000L;
    internal const long WsMaximizeBox = 0x0001_0000L;

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
    internal static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    internal const int SwRestore = 9;
    internal const uint SwpShowWindow = 0x0040;
    internal static readonly nint HwndTop = 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out WindowRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZorder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;

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

    internal const int WhKeyboardLl = 13;
    internal const int WmKeyDown = 0x0100;
    internal const int WmSysKeyDown = 0x0104;

    internal const int WmGetMinMaxInfo = 0x0024;
    internal const int WmNcCalcSize = 0x0083;
    internal const int WmNcHitTest = 0x0084;
    internal const uint MonitorDefaultToNearest = 0x0000_0002;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmWindowCornerRound = 2;
    internal const int DwmwaBorderColor = 34;
    internal const int DwmColorNone = unchecked((int)0xFFFF_FFFE);

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
    internal struct NcCalcSizeParams
    {
        public WindowRect Rgrc0;
        public WindowRect Rgrc1;
        public WindowRect Rgrc2;
        public WindowPos WindowPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPos
    {
        public nint Hwnd;
        public nint HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    internal const int HtClient = 1;
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;

    /// <summary>
    /// Used by the Command Bar to commit on Ctrl release; WebView2 never delivers that key-up
    /// (ADR 0002). The modifier watcher polls these; the Command Bar hotkey is the one
    /// keyboard hook, installed in App.
    /// </summary>
    internal static bool IsControlDown() => IsKeyDown(VkControl);

    internal static bool IsShiftDown() => IsKeyDown(VkShift);

    internal static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// A user-requested show that has to win against Windows' foreground lock. The request
    /// often comes from another process (tray, PowerToys, a toast), so this process did not
    /// receive the last input and plain <see cref="SetForegroundWindow"/> is denied. Attach to
    /// the foreground thread for the call, raise the window to the top of the normal
    /// z-order, ask for foreground and then detach.
    /// </summary>
    internal static bool ForceForeground(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
        }

        try
        {
            _ = ShowWindow(hwnd, SwRestore);
            _ = SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            _ = BringWindowToTop(hwnd);
            var granted = SetForegroundWindow(hwnd);
            _ = SetFocus(hwnd);
            return granted || GetForegroundWindow() == hwnd;
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

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

    internal static long GetStyle(nint hwnd) =>
        nint.Size == 8
            ? GetWindowLongPtr64(hwnd, GwlStyle)
            : GetWindowLong32(hwnd, GwlStyle);

    internal static void SetStyle(nint hwnd, long style)
    {
        if (nint.Size == 8)
        {
            _ = SetWindowLongPtr64(hwnd, GwlStyle, (nint)style);
        }
        else
        {
            _ = SetWindowLong32(hwnd, GwlStyle, unchecked((int)style));
        }
    }
}
