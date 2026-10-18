using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenLancer.Editor.Play;

/// <summary>
/// A Windows job object that ends its process when the job closes (decision D53). Only the object that started the
/// process holds the job. So the process ends at <see cref="Dispose"/>. It also ends when the editor process ends in any
/// way, for example by a crash or a kill, because Windows then closes the handle. Play in OpenLancer and Play in the game
/// use it.
/// </summary>
internal sealed partial class ProcessJob : IDisposable
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;

    private readonly SafeJobHandle handle;

    private ProcessJob(SafeJobHandle handle) => this.handle = handle;

    /// <summary>A job that ends the process when the job closes, or null when Windows does not give it.</summary>
    public static ProcessJob? For(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        if (!job.IsInvalid &&
            SetInformationJobObject(job, ExtendedLimitInformationClass, limits, Marshal.SizeOf<ExtendedLimitInformation>()) &&
            AssignProcessToJobObject(job, process.SafeHandle))
        {
            return new ProcessJob(job);
        }

        job.Dispose();
        return null;
    }

    /// <summary>Closes the job, which ends its process.</summary>
    public void Dispose() => handle.Dispose();

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>.</summary>
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

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass,
        in ExtendedLimitInformation info, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
