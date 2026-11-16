using System.IO;
using Chirpdesk.Interop;

namespace Chirpdesk.Notifications;

/// <summary>
/// The identity Windows needs in order to show a toast on behalf of an unpackaged app:
/// a Start Menu shortcut whose <c>System.AppUserModel.ID</c> matches the process AUMID.
/// </summary>
internal static class Aumid
{
    public const string AppId = "Chirpdesk";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Chirpdesk.lnk");

    public static void SetProcessAppUserModelId()
    {
        var hr = NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppId);
        if (hr < 0)
        {
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>
    /// Re-created on every launch: the target is the running executable, and a dev or portable
    /// build can launch from a different path than the previous one. The file is tiny.
    /// </summary>
    public static void EnsureShortcut(string executablePath)
    {
        ShellLink.Create(ShortcutPath, executablePath, AppId);
    }

    /// <summary>Reads back the process AUMID the shell actually accepted.</summary>
    public static string? CurrentProcessAppUserModelId()
    {
        var hr = NativeMethods.GetCurrentProcessExplicitAppUserModelID(out var pointer);
        if (hr < 0 || pointer == nint.Zero)
        {
            return null;
        }

        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer);
        }
    }
}
