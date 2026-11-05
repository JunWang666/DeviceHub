using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceHub.Agent.Native;

/// <summary>
/// USB 串口（Ports 类设备，COM 口）的枚举。只收 USB 复合设备的接口（实例 Id 形如 USB\VID_xxxx&amp;PID_xxxx&amp;MI_xx\…），
/// 每个接口带端口名、接口号和 PnP ContainerId，用于把同一物理设备的多个串口聚合成一台设备。
/// </summary>
public static partial class NativeSerial
{
    public sealed record SerialPortInfo(string InstanceId, string PortName, ushort Vid, ushort Pid, int Interface,
        string? FriendlyName, string? Product, Guid? ContainerId);

    /// <summary>PnP 对内置/无法归属的设备使用的"空容器"，不能用来聚合。</summary>
    private static readonly Guid NoContainer = new("00000000-0000-0000-ffff-ffffffffffff");
    private static readonly Guid GUID_DEVCLASS_PORTS = new("4d36e978-e325-11ce-bfc1-08002be10318");

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&MI_([0-9A-F]{2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex UsbId();

    public static IReadOnlyList<SerialPortInfo> Enumerate()
    {
        var result = new List<SerialPortInfo>();
        var cls = GUID_DEVCLASS_PORTS;
        nint set = SetupDiGetClassDevs(ref cls, null, nint.Zero, DIGCF_PRESENT);
        if (set == INVALID_HANDLE) return result;
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref dev); i++)
            {
                var id = InstanceId(set, ref dev);
                if (id is null) continue;
                var m = UsbId().Match(id);
                if (!m.Success) continue;
                var port = PortName(set, ref dev);
                if (port is null || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;
                var container = PropGuid(set, ref dev, DEVPKEY_Device_ContainerId);
                result.Add(new SerialPortInfo(
                    id, port,
                    Convert.ToUInt16(m.Groups[1].Value, 16), Convert.ToUInt16(m.Groups[2].Value, 16),
                    m.Groups[3].Success ? Convert.ToInt32(m.Groups[3].Value, 16) : -1,
                    RegString(set, ref dev, SPDRP_FRIENDLYNAME),
                    PropString(set, ref dev, DEVPKEY_Device_BusReportedDeviceDesc),
                    container == NoContainer ? null : container));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static string? InstanceId(nint set, ref SP_DEVINFO_DATA dev)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref dev, sb, sb.Capacity, out _) ? sb.ToString() : null;
    }

    /// <summary>端口名在设备硬件键 Device Parameters\PortName 里。</summary>
    private static string? PortName(nint set, ref SP_DEVINFO_DATA dev)
    {
        nint key = SetupDiOpenDevRegKey(set, ref dev, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (key == INVALID_HANDLE) return null;
        try
        {
            var buf = new byte[256];
            int size = buf.Length;
            if (RegQueryValueEx(key, "PortName", nint.Zero, out _, buf, ref size) != 0) return null;
            return Encoding.Unicode.GetString(buf, 0, size).TrimEnd('\0');
        }
        finally { RegCloseKey(key); }
    }

    private static string? RegString(nint set, ref SP_DEVINFO_DATA dev, uint prop)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref dev, prop, out _, buf, buf.Length, out int need) || need < 2) return null;
        return Encoding.Unicode.GetString(buf, 0, need).TrimEnd('\0');
    }

    private static string? PropString(nint set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDeviceProperty(set, ref dev, ref key, out _, buf, (uint)buf.Length, out uint need, 0) || need < 2) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)need).TrimEnd('\0');
    }

    private static Guid? PropGuid(nint set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key)
    {
        var buf = new byte[16];
        if (!SetupDiGetDeviceProperty(set, ref dev, ref key, out uint type, buf, 16, out _, 0) || type != DEVPROP_TYPE_GUID) return null;
        return new Guid(buf);
    }

    private const uint DIGCF_PRESENT = 0x2;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;
    private const uint DICS_FLAG_GLOBAL = 1, DIREG_DEV = 1, KEY_READ = 0x20019;
    private const uint DEVPROP_TYPE_GUID = 0x0D;
    private static readonly nint INVALID_HANDLE = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new() { fmtid = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
    private static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new() { fmtid = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid g, string? enumerator, nint hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint set, ref SP_DEVINFO_DATA dev, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref SP_DEVINFO_DATA dev, uint prop, out uint type, byte[] buf, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    private static extern bool SetupDiGetDeviceProperty(nint set, ref SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiOpenDevRegKey(nint set, ref SP_DEVINFO_DATA dev, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(nint key, string name, nint reserved, out uint type, byte[] data, ref int size);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(nint key);
}
