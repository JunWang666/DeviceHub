using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeviceHub.Agent.Native;

/// <summary>HID 设备枚举与报文 I/O 的 Win32 封装。设备发现用 SetupDi，报文用 HidD_*。</summary>
public static class NativeHid
{
    /// <summary>
    /// HID 集合。Interface 取自路径中的 mi_XX（无则 -1）；报告长度含 report id 字节。
    /// ContainerId 是 PnP 给同一物理设备所有功能节点的公共 Id（"设备和打印机"按它分组），用于聚合多个集合。
    /// </summary>
    public sealed record HidPath(string Path, ushort Vid, ushort Pid, string? Product, string? Serial,
        ushort UsagePage, ushort Usage, int Interface, int InputLength, int OutputLength, int FeatureLength)
    {
        public Guid? ContainerId { get; init; }
    }

    /// <summary>PnP 对内置/无法归属的设备使用的"空容器"，不能用来聚合。</summary>
    private static readonly Guid NoContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    public static IReadOnlyList<HidPath> Enumerate()
    {
        var result = new List<HidPath>();
        HidD_GetHidGuid(out Guid hidGuid);
        nint set = SetupDiGetClassDevs(ref hidGuid, null, nint.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE) return result;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, nint.Zero, ref hidGuid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref ifData, nint.Zero, 0, out uint need, nint.Zero);
                if (need == 0) continue;
                nint detail = Marshal.AllocHGlobal((int)need);
                try
                {
                    // cbSize = 8 on x64 (DWORD + first WCHAR), per Win32 convention.
                    Marshal.WriteInt32(detail, 8);
                    var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, need, out _, ref devInfo)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Length == 0) continue;
                    var info = Probe(path);
                    if (info != null) result.Add(info with { ContainerId = GetContainerId(set, ref devInfo) });
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static HidPath? Probe(string path)
    {
        using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero,
            OPEN_EXISTING, 0, nint.Zero);
        if (h.IsInvalid) return null;
        var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr)) return null;
        string? product = GetString((b, l) => HidD_GetProductString(h, b, l));
        string? serial = GetString((b, l) => HidD_GetSerialNumberString(h, b, l));
        ushort page = 0, usage = 0; int inLen = 0, outLen = 0, featLen = 0;
        if (HidD_GetPreparsedData(h, out nint pp))
        {
            try
            {
                if (HidP_GetCaps(pp, out var caps) == HIDP_STATUS_SUCCESS)
                {
                    page = caps.UsagePage; usage = caps.Usage;
                    inLen = caps.InputReportByteLength; outLen = caps.OutputReportByteLength; featLen = caps.FeatureReportByteLength;
                }
            }
            finally { HidD_FreePreparsedData(pp); }
        }
        return new HidPath(path, attr.VendorID, attr.ProductID, product, serial,
            page, usage, ParseInterface(path), inLen, outLen, featLen);
    }

    private static Guid? GetContainerId(nint set, ref SP_DEVINFO_DATA devInfo)
    {
        var key = DEVPKEY_Device_ContainerId;
        var buf = new byte[16];
        if (!SetupDiGetDeviceProperty(set, ref devInfo, ref key, out uint type, buf, (uint)buf.Length, out _, 0)
            || type != DEVPROP_TYPE_GUID) return null;
        var g = new Guid(buf);
        return g == NoContainer || g == Guid.Empty ? null : g;
    }

    /// <summary>从 "\\?\hid#vid_1a2c&amp;pid_7fff&amp;mi_02#..." 中取接口号。</summary>
    private static int ParseInterface(string path)
    {
        var i = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 6 <= path.Length && int.TryParse(path.AsSpan(i + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : -1;
    }

    private static string? GetString(Func<byte[], int, bool> fn)
    {
        var buf = new byte[256];
        return fn(buf, buf.Length) ? System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0') : null;
    }

    /// <summary>打开设备用于读写（供 Broker 持有句柄）。</summary>
    public static SafeFileHandle Open(string path) =>
        CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nint.Zero);

    public static byte[]? GetFeature(SafeFileHandle h, byte reportId, int length)
    {
        var buf = new byte[length];
        buf[0] = reportId;
        return HidD_GetFeature(h, buf, buf.Length) ? buf : null;
    }

    public static bool SetFeature(SafeFileHandle h, byte[] report) => HidD_SetFeature(h, report, report.Length);

    /// <summary>
    /// 写 Output 报告（WriteFile → 中断 OUT 端点）。report[0] 为 report id（无 id 的集合填 0），
    /// 不足 outputLength 时补零。WriteFile 要求缓冲区恰好等于 OutputReportByteLength。
    /// </summary>
    public static bool WriteOutput(SafeFileHandle h, byte[] report, int outputLength, int timeoutMs = 1000)
    {
        if (outputLength <= 0 || report.Length > outputLength) return false;
        var buf = new byte[outputLength];
        Array.Copy(report, buf, report.Length);
        return Overlapped(h, buf, write: true, timeoutMs) == outputLength;
    }

    /// <summary>读一个 Input 报告；超时返回 null（设备不主动上报时不会阻塞调用方）。</summary>
    public static byte[]? ReadInput(SafeFileHandle h, int inputLength, int timeoutMs)
    {
        if (inputLength <= 0) return null;
        var buf = new byte[inputLength];
        var n = Overlapped(h, buf, write: false, timeoutMs);
        return n > 0 ? buf[..n] : null;
    }

    /// <summary>
    /// 读一个输入报告，等待期间不占线程：重叠 I/O 的完成事件交给线程池等待（插件轮询时一次可能等几十到几千毫秒）。
    /// 超时 / 失败返回 null。缓冲区和 OVERLAPPED 放在非托管内存里，I/O 结束前句柄被引用住、不会真正关闭。
    /// </summary>
    public static Task<byte[]?> ReadInputAsync(SafeFileHandle h, int inputLength, int timeoutMs)
    {
        if (inputLength <= 0) return Task.FromResult<byte[]?>(null);
        var op = new PendingRead(h, inputLength);
        if (!op.Start()) { op.Dispose(); return Task.FromResult<byte[]?>(null); }
        // 已有缓存报告（同步完成）或只是想取走缓冲区里的报告（超时极短）：直接在当前线程等完，
        // 省掉线程池等待的一个系统时钟周期（约 15ms）
        if (timeoutMs <= 2 || op.Event.WaitOne(0)) return Task.FromResult(op.Finish(!op.Event.WaitOne(timeoutMs)));
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = ThreadPool.RegisterWaitForSingleObject(op.Event, (_, timedOut) => tcs.TrySetResult(op.Finish(timedOut)), null, timeoutMs, executeOnlyOnce: true);
        tcs.Task.ContinueWith(_ => reg.Unregister(null), TaskScheduler.Default);
        return tcs.Task;
    }

    private sealed unsafe class PendingRead : IDisposable
    {
        private readonly SafeFileHandle _h;
        private readonly bool _ref;
        private readonly int _len;
        private byte* _buf;
        private NativeOverlapped* _ov;
        public readonly ManualResetEvent Event = new(false);

        public PendingRead(SafeFileHandle h, int len)
        {
            _h = h;
            _len = len;
            h.DangerousAddRef(ref _ref);
            _buf = (byte*)NativeMemory.Alloc((nuint)len);
            _ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            _ov->EventHandle = Event.SafeWaitHandle.DangerousGetHandle();
        }

        /// <summary>发起读；同步完成时事件同样会被置位。</summary>
        public bool Start() => ReadFile(_h, _buf, (uint)_len, out _, _ov) || Marshal.GetLastWin32Error() == ERROR_IO_PENDING;

        public byte[]? Finish(bool timedOut)
        {
            try
            {
                if (timedOut) CancelIoEx(_h, _ov);
                // 超时时等取消完成（确保内核不再写缓冲区）；恰好在取消前读到的报告照样返回
                return GetOverlappedResult(_h, _ov, out uint n, timedOut) && n > 0 ? new ReadOnlySpan<byte>(_buf, (int)n).ToArray() : null;
            }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            if (_buf is null) return;
            NativeMemory.Free(_buf); _buf = null;
            NativeMemory.Free(_ov); _ov = null;
            Event.Dispose();
            if (_ref) _h.DangerousRelease();
        }
    }

    /// <summary>重叠 I/O + 超时取消。句柄须以 FILE_FLAG_OVERLAPPED 打开。返回传输字节数，失败/超时为 -1。</summary>
    private static unsafe int Overlapped(SafeFileHandle h, byte[] buf, bool write, int timeoutMs)
    {
        using var evt = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* p = buf)
        {
            bool ok = write ? WriteFile(h, p, (uint)buf.Length, out _, &ov)
                            : ReadFile(h, p, (uint)buf.Length, out _, &ov);
            if (!ok && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return -1;
            if (!evt.WaitOne(timeoutMs))
            {
                CancelIoEx(h, &ov);
                GetOverlappedResult(h, &ov, out _, true); // 等取消完成，确保缓冲区不再被内核引用
                return -1;
            }
            return GetOverlappedResult(h, &ov, out uint n, false) ? (int)n : -1;
        }
    }

    // —— P/Invoke ——
    private static readonly nint INVALID_HANDLE = -1;
    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997, HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new() { fmtid = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
    private const uint DEVPROP_TYPE_GUID = 0x0D;
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out nint pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(nint pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint pp, out HIDP_CAPS caps);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool WriteFile(SafeFileHandle h, byte* buf, uint n, out uint written, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool ReadFile(SafeFileHandle h, byte* buf, uint n, out uint read, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out uint n, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid g, string? enumerator, nint hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SP_DEVICE_INTERFACE_DATA data, nint detail, uint size, out uint required, nint devInfo);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SP_DEVICE_INTERFACE_DATA data, nint detail, uint size, out uint required, ref SP_DEVINFO_DATA devInfo);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    private static extern bool SetupDiGetDeviceProperty(nint set, ref SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint sec, uint disp, uint flags, nint tmpl);
}
