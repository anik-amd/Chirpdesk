using System.IO;
using System.Runtime.InteropServices;

namespace Chirpdesk.Diagnostics;

/// <summary>
/// A WinExe has no console of its own; attaching to the parent's lets <c>--selftest</c> print
/// where the user ran it. When there is no parent console the report file is the output.
/// </summary>
internal static class ConsoleBridge
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void Write(string text)
    {
        try
        {
            if (NativeConsole.AttachConsole(AttachParentProcess))
            {
                Console.Out.WriteLine(text);
                Console.Out.Flush();
            }
        }
        catch (IOException)
        {
            // No console to write to.
        }
    }

    private static class NativeConsole
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(uint processId);
    }
}
