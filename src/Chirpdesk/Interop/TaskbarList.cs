using System.Runtime.InteropServices;

namespace Chirpdesk.Interop;

/// <summary>
/// Removes and restores the taskbar button without changing the window's extended style.
/// Toggling <c>WS_EX_TOOLWINDOW</c> suppressed the native DWM maximize/minimize transition on
/// this window; the shell's taskbar list does the same job and leaves the frame alone.
/// </summary>
internal static class TaskbarList
{
    private static readonly HashSet<nint> Hidden = [];

    public static bool IsHidden(nint hwnd) => Hidden.Contains(hwnd);

    public static void Add(nint hwnd)
    {
        try
        {
            Instance.AddTab(hwnd);
            Hidden.Remove(hwnd);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
        {
            // The shell is not ready yet; ShowWindow will still put the button back.
        }
    }

    public static void Remove(nint hwnd)
    {
        try
        {
            Instance.DeleteTab(hwnd);
            Hidden.Add(hwnd);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
        {
            // Fail open: leave the button alone rather than touching the window style.
        }
    }

    private static ITaskbarList? _instance;

    private static ITaskbarList Instance
    {
        get
        {
            if (_instance is null)
            {
                var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
                var type = Type.GetTypeFromCLSID(clsid)
                    ?? throw new InvalidOperationException("taskbar list CLSID is unavailable");
                _instance = (ITaskbarList)Activator.CreateInstance(type)!;
                _instance.HrInit();
            }

            return _instance;
        }
    }

    [ComImport]
    [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);
    }
}
