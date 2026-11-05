using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceHub.Agent.Native;

/// <summary>
/// USB 复合设备接口（实例 Id 形如 USB\VID_xxxx&amp;PID_xxxx&amp;MI_xx\…）的枚举，供串口总线用：
/// COM 口（Ports 类）、网卡（Net 类，带 NetCfgInstanceId）、以及没装上驱动的接口。
/// 每个接口带 PnP ContainerId，用于把同一物理设备的多个接口聚合成一台设备。
/// </summary>
public static partial class NativeSerial
{
    public enum InterfaceKind { Serial, Net, NoDriver }

    public sealed record UsbInterfaceInfo(string InstanceId, ushort Vid, ushort Pid, int Interface, InterfaceKind Kind,
        string? PortName, string? NetCfgInstanceId, int ProblemCode, string? FriendlyName, string? Product, Guid? ContainerId);

    /// <summary>PnP 对内置/无法归属的设备使用的"空容器"，不能用来聚合。</summary>
    private static readonly Guid NoContainer = new("00000000-0000-0000-ffff-ffffffffffff");
    private static readonly Guid GUID_DEVCLASS_PORTS = new("4d36e978-e325-11ce-bfc1-08002be10318");
    private static readonly Guid GUID_DEVCLASS_NET = new("4d36e972-e325-11ce-bfc1-08002be10318");

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})&MI_([0-9A-F]{2})\\", RegexOptions.IgnoreCase)]
    private static partial Regex UsbInterfaceId();

    /// <summary>当前在线的 USB 接口里的 COM 口、网卡和缺驱动的接口（其他已正常工作的接口，如 HID，不在其列）。</summary>
    public static IReadOnlyList<UsbInterfaceInfo> Enumerate()
    {
        var result = new List<UsbInterfaceInfo>();
        nint set = SetupDiGetClassDevsAll(nint.Zero, "USB", nint.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE) return result;
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref dev); i++)
            {
                var id = InstanceId(set, ref dev);
                if (id is null) continue;
                var m = UsbInterfaceId().Match(id);
                if (!m.Success) continue;

                int problem = PropInt(set, ref dev, DEVPKEY_Device_ProblemCode);
                InterfaceKind kind;
                string? port = null, netId = null;
                if (dev.ClassGuid == GUID_DEVCLASS_PORTS && problem == 0 && (port = PortName(set, ref dev)) is { } p
                    && p.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    kind = InterfaceKind.Serial;
                else if (dev.ClassGuid == GUID_DEVCLASS_NET && problem == 0)
                {
                    kind = InterfaceKind.Net;
                    netId = DriverValue(set, ref dev, "NetCfgInstanceId");
                }
                else if (problem != 0 && PropString(set, ref dev, DEVPKEY_Device_Service) is null)
                    kind = InterfaceKind.NoDriver; // 有问题且没有绑定服务 = 没装上驱动
                else continue;

                var container = PropGuid(set, ref dev, DEVPKEY_Device_ContainerId);
                result.Add(new UsbInterfaceInfo(
                    id, Convert.ToUInt16(m.Groups[1].Value, 16), Convert.ToUInt16(m.Groups[2].Value, 16),
                    Convert.ToInt32(m.Groups[3].Value, 16), kind, port, netId, problem,
                    RegString(set, ref dev, SPDRP_FRIENDLYNAME),
                    PropString(set, ref dev, DEVPKEY_Device_BusReportedDeviceDesc),
                    container == NoContainer ? null : container));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    /// <summary>设备管理器问题代码的中文说明。</summary>
    public static string ProblemText(int code) => code switch
    {
        28 => "未安装驱动",
        1 or 18 => "驱动配置不正确（代码 " + code + "）",
        10 => "驱动无法启动（代码 10）",
        31 => "驱动无法加载（代码 31）",
        52 => "驱动签名无法验证（代码 52）",
        _ => "设备有问题（代码 " + code + "）",
    };

    private static string? InstanceId(nint set, ref SP_DEVINFO_DATA dev)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref dev, sb, sb.Capacity, out _) ? sb.ToString() : null;
    }

    /// <summary>端口名在设备硬件键 Device Parameters\PortName 里。</summary>
    private static string? PortName(nint set, ref SP_DEVINFO_DATA dev) => RegValue(set, ref dev, DIREG_DEV, "PortName");

    /// <summary>驱动键（Class\{guid}\NNNN）里的值，如网卡的 NetCfgInstanceId。</summary>
    private static string? DriverValue(nint set, ref SP_DEVINFO_DATA dev, string name) => RegValue(set, ref dev, DIREG_DRV, name);

    private static string? RegValue(nint set, ref SP_DEVINFO_DATA dev, uint keyType, string name)
    {
        nint key = SetupDiOpenDevRegKey(set, ref dev, DICS_FLAG_GLOBAL, 0, keyType, KEY_READ);
        if (key == INVALID_HANDLE) return null;
        try
        {
            var buf = new byte[512];
            int size = buf.Length;
            if (RegQueryValueEx(key, name, nint.Zero, out _, buf, ref size) != 0) return null;
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
        var s = Encoding.Unicode.GetString(buf, 0, (int)need).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static int PropInt(nint set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key)
    {
        var buf = new byte[4];
        return SetupDiGetDeviceProperty(set, ref dev, ref key, out _, buf, 4, out _, 0) ? BitConverter.ToInt32(buf) : 0;
    }

    private static Guid? PropGuid(nint set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key)
    {
        var buf = new byte[16];
        if (!SetupDiGetDeviceProperty(set, ref dev, ref key, out uint type, buf, 16, out _, 0) || type != DEVPROP_TYPE_GUID) return null;
        return new Guid(buf);
    }

    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;
    private const uint DICS_FLAG_GLOBAL = 1, DIREG_DEV = 1, DIREG_DRV = 2, KEY_READ = 0x20019;
    private const uint DEVPROP_TYPE_GUID = 0x0D;
    private static readonly nint INVALID_HANDLE = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new() { fmtid = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
    private static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new() { fmtid = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
    private static readonly DEVPROPKEY DEVPKEY_Device_ProblemCode = new() { fmtid = new("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 3 };
    private static readonly DEVPROPKEY DEVPKEY_Device_Service = new() { fmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 6 };

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern nint SetupDiGetClassDevsAll(nint g, string? enumerator, nint hwnd, uint flags);
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
