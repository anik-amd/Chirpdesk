using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Chirpdesk.Interop;

/// <summary>
/// A Job Object that kills every assigned process when its last handle closes. The helper is
/// assigned right after launch, so a Chirpdesk crash cannot leave an orphan behind.
/// </summary>
internal static class JobObject
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    public static SafeJobHandle? CreateKillOnClose()
    {
        var handle = CreateJobObject(nint.Zero, null);

        if (handle.IsInvalid)
        {
            return null;
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(information, pointer, fDeleteOld: false);

            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, pointer, (uint)length))
            {
                handle.Dispose();
                return null;
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static bool Assign(SafeJobHandle job, nint processHandle) =>
        AssignProcessToJobObject(job, processHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        nint information,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, nint process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
