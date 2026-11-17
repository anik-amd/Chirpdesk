using System.IO;
using System.Runtime.InteropServices;
using Chirpdesk.Core.Logging;

namespace Chirpdesk.Interop;

/// <summary>User data goes to the Recycle Bin, never a hard delete (invariant 2).</summary>
internal static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public static bool Send(string path, ILog log)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            log.Warn("recycle refused: path is not absolute");
            return false;
        }

        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + '\0',
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };

        try
        {
            var result = SHFileOperation(ref operation);
            if (result != 0)
            {
                log.Warn($"recycle failed for '{Path.GetFileName(path)}': code {result}");
                return false;
            }

            log.Info($"moved to Recycle Bin: {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log.Error("recycle failed: shell API unavailable", ex);
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }
}
