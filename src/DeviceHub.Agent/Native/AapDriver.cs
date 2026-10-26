using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeviceHub.Agent.Native;

/// <summary>
/// 一条到 AirPods 的 AAP（L2CAP PSM 0x1001）链路。Windows 用户态打不开任意 L2CAP 通道，
/// 只能经第三方 KMDF 驱动转发；这里只做传输，不解析 AAP 内容（协议归插件）。
/// </summary>
public interface IAapLink : IDisposable
{
    string DriverName { get; }
    void Send(byte[] frame);
    /// <summary>收一帧；timeoutMs 内无数据返回 0，链路断开抛异常。</summary>
    int Receive(byte[] buffer, int timeoutMs);
}

/// <summary>
/// 探测并打开已安装的 AAP 驱动。支持两种开源驱动（接口互不兼容）：
/// <list type="bullet">
/// <item>NTPodsAAP（github.com/arctumn/ntpods）：CONNECT 带蓝牙地址 + PSM，RECEIVE 带超时。</item>
/// <item>PodBridgeAAP（github.com/bhemsen/PodBridge）：驱动绑定在设备上，CONNECT 无参数，RECEIVE 一直挂起直到有帧。</item>
/// </list>
/// 两者都只有测试签名，需要用户自己开启测试模式安装；没装时 AirPods 只有 continuity 通道。
/// </summary>
public static class AapDriver
{
    /// <summary>{C0FFEE00-1337-4A5B-9E6F-A1B2C3D4E5F6}，见 ntpods windows/daemon/src/driver.rs。</summary>
    private static readonly Guid NtPodsInterface = new("c0ffee00-1337-4a5b-9e6f-a1b2c3d4e5f6");
    /// <summary>{A5FD3D2B-12A0-40AF-AE19-EF110C488DFF}，见 PodBridge driver/PodBridgeAAP/Public.h。</summary>
    private static readonly Guid PodBridgeInterface = new("a5fd3d2b-12a0-40af-ae19-ef110c488dff");

    /// <summary>当前装了哪种驱动（没有则 null）。只查设备接口列表，不打开设备、不碰蓝牙链路。</summary>
    public static string? Installed() =>
        FirstInterface(NtPodsInterface) is not null ? "NTPodsAAP"
        : FirstInterface(PodBridgeInterface) is not null ? "PodBridgeAAP"
        : null;

    /// <summary>打开驱动并建立到指定设备的 AAP 通道。没有驱动返回 null。</summary>
    public static IAapLink? Open(ulong bluetoothAddress, ushort psm)
    {
        if (FirstInterface(NtPodsInterface) is { } nt) return NtPodsLink.Open(nt, bluetoothAddress, psm);
        if (FirstInterface(PodBridgeInterface) is { } pb) return PodBridgeLink.Open(pb);
        return null;
    }

    private static string? FirstInterface(Guid g)
    {
        if (CM_Get_Device_Interface_List_SizeW(out var len, ref g, nint.Zero, 0) != 0 || len < 2) return null;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, nint.Zero, buf, len, 0) != 0) return null;
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static SafeFileHandle OpenFile(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);
        if (h.IsInvalid) { var err = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(err); }
        return h;
    }

    private static int Ioctl(SafeFileHandle h, uint code, byte[]? input, byte[]? output)
    {
        if (!DeviceIoControl(h, code, input, (uint)(input?.Length ?? 0), output, (uint)(output?.Length ?? 0), out var n, nint.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (int)n;
    }

    private static uint CtlCode(uint function, uint access) => (0x22u << 16) | (access << 14) | (function << 2);

    /// <summary>NTPods：同一个句柄收发，RECEIVE 自带超时，所以 Send 最多等一个接收周期。</summary>
    private sealed class NtPodsLink : IAapLink
    {
        private const uint IoctlConnect = 0x8000_2000, IoctlSend = 0x8000_2008, IoctlReceive = 0x8000_200C;
        private readonly SafeFileHandle _h;
        private NtPodsLink(SafeFileHandle h) => _h = h;
        public string DriverName => "NTPodsAAP";

        public static NtPodsLink Open(string path, ulong address, ushort psm)
        {
            var h = OpenFile(path);
            try
            {
                var input = new byte[10];
                BitConverter.TryWriteBytes(input.AsSpan(0, 8), address);
                BitConverter.TryWriteBytes(input.AsSpan(8, 2), psm);
                var output = new byte[8];
                Ioctl(h, IoctlConnect, input, output);
                if (BitConverter.ToUInt32(output, 0) == 0) throw new IOException("驱动未能建立 L2CAP 通道（AirPods 未连接？）");
                return new NtPodsLink(h);
            }
            catch { h.Dispose(); throw; }
        }

        public void Send(byte[] frame) => Ioctl(_h, IoctlSend, frame, null);

        public int Receive(byte[] buffer, int timeoutMs)
        {
            try { return Ioctl(_h, IoctlReceive, BitConverter.GetBytes((uint)timeoutMs), buffer); }
            catch (Win32Exception ex) when (ex.NativeErrorCode is ERROR_SEM_TIMEOUT or ERROR_TIMEOUT) { return 0; }
        }

        public void Dispose() => _h.Dispose();
    }

    /// <summary>
    /// PodBridge：RECEIVE 会一直挂起，而同步句柄上的请求是串行的，所以收发各用一个句柄
    /// （驱动把通道状态放在设备上下文里，两个句柄共用一条通道）。超时靠 CancelIoEx 取消挂起的 RECEIVE。
    /// </summary>
    private sealed class PodBridgeLink : IAapLink
    {
        private static readonly uint IoctlConnect = CtlCode(0x800, 3), IoctlSend = CtlCode(0x801, 2), IoctlReceive = CtlCode(0x802, 1);
        private readonly SafeFileHandle _control, _receive;
        private PodBridgeLink(SafeFileHandle c, SafeFileHandle r) { _control = c; _receive = r; }
        public string DriverName => "PodBridgeAAP";

        public static PodBridgeLink Open(string path)
        {
            var c = OpenFile(path);
            SafeFileHandle r;
            try { r = OpenFile(path); Ioctl(c, IoctlConnect, null, null); }
            catch { c.Dispose(); throw; }
            return new PodBridgeLink(c, r);
        }

        public void Send(byte[] frame) => Ioctl(_control, IoctlSend, frame, null);

        public int Receive(byte[] buffer, int timeoutMs)
        {
            using var timer = new Timer(_ => { if (!_receive.IsClosed) CancelIoEx(_receive, nint.Zero); }, null, timeoutMs, Timeout.Infinite);
            try { return Ioctl(_receive, IoctlReceive, null, buffer); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_OPERATION_ABORTED) { return 0; }
        }

        public void Dispose()
        {
            if (!_receive.IsClosed) CancelIoEx(_receive, nint.Zero);
            _receive.Dispose();
            _control.Dispose();
        }
    }

    // —— P/Invoke ——
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const int ERROR_OPERATION_ABORTED = 995, ERROR_SEM_TIMEOUT = 121, ERROR_TIMEOUT = 1460;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, nint deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid g, nint deviceId, [Out] char[] buf, uint len, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint sec, uint disp, uint flags, nint tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, uint inLen, byte[]? outBuf, uint outLen, out uint returned, nint ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, nint ov);
}
