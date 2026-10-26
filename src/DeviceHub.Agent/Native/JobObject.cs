using System.Runtime.InteropServices;

namespace DeviceHub.Agent.Native;

/// <summary>
/// 把插件进程放进 Job Object：Agent 退出时自动杀光插件（KILL_ON_JOB_CLOSE），
/// 并限制单进程内存，体现"进程管理"能力。
/// </summary>
public sealed class JobObject : IDisposable
{
    private readonly nint _handle;

    public JobObject(long memoryLimitBytes)
    {
        _handle = CreateJobObject(nint.Zero, null);
        if (_handle == 0) throw new InvalidOperationException("CreateJobObject failed");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_PROCESS_MEMORY;
        info.ProcessMemoryLimit = (nuint)memoryLimitBytes;

        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        nint p = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, p, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, p, (uint)len))
                throw new InvalidOperationException("SetInformationJobObject failed");
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public void Assign(nint processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
            throw new InvalidOperationException("AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_handle != 0) CloseHandle(_handle);
    }

    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint a, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int cls, nint info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint h);
}
