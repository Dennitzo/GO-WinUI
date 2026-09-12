using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GoWinUI.Core.Coding;

/// <summary>
/// Owns only the command's process job. Closing it terminates assigned processes and their descendants.
/// Assignment happens immediately after Process.Start; this small launch window means this is lifecycle
/// cleanup, not an operating-system sandbox. No breakaway flag or inheritable job handle is enabled.
/// </summary>
internal sealed partial class WindowsProcessJob : IDisposable
{
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformation = 9;
    private readonly SafeJobHandle _handle;

    private WindowsProcessJob(SafeJobHandle handle) => _handle = handle;

    public static WindowsProcessJob Create()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Process jobs require Windows.");
        var handle = CreateJobObjectW(0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Der Coding-Prozessjob konnte nicht erstellt werden.");
        }
        var information = new JobExtendedLimitInformation
        {
            BasicLimitInformation = new JobBasicLimitInformation { LimitFlags = KillOnJobClose },
        };
        if (SetInformationJobObject(handle, ExtendedLimitInformation, in information, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Das automatische Beenden des Coding-Prozessjobs konnte nicht aktiviert werden.");
        }
        return new WindowsProcessJob(handle);
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (AssignProcessToJobObject(_handle, process.SafeHandle) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Der Coding-Prozess konnte keinem verwalteten Prozessjob zugeordnet werden.");
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeJobHandle CreateJobObjectW(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetInformationJobObject(SafeJobHandle job, int informationClass, in JobExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CloseHandle(nint handle);

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle) != 0;
    }

    // Layout follows the installed Windows SDK winnt.h JOBOBJECT_* and IO_COUNTERS definitions.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
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
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
