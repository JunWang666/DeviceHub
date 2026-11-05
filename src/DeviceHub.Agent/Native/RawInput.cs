using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DeviceHub.Contracts;
using Microsoft.Win32.SafeHandles;

namespace DeviceHub.Agent.Native;

/// <summary>
/// Raw Input：枚举键盘 / 鼠标，以及在一个仅消息窗口上接收 WM_INPUT，得知每次输入来自哪个设备。
/// 注意（实测）：低级钩子先于 Raw Input 执行，被钩子吞掉的输入不会产生 WM_INPUT。所以钩子做决定时拿不到本次输入的设备，
/// 只能用"最近一次有输入的键盘 / 鼠标"——鼠标按键前总有移动，判断很准；键盘组合键的修饰键先放行，也准；
/// 只有换了键盘后按下的第一个被映射的单键可能判错。
/// </summary>
internal static partial class RawInput
{
    public const uint WM_INPUT = 0x00FF;
    private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    private const uint RID_INPUT = 0x10000003, RID_HEADER = 0x10000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100;

    public delegate nint WndProc(nint h, uint m, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS { public uint style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public nint hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public nint hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICELIST { public nint hDevice; public uint dwType; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWMOUSE { public ushort usFlags, pad; public uint ulButtons, ulRawButtons; public int lLastX, lLastY; public uint ulExtraInformation; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint p);
    [DllImport("user32.dll")] public static extern nint DefWindowProc(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] r, uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint h, uint cmd, nint data, ref uint size, uint hdr);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint n, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(nint h, uint cmd, char[]? data, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint sa, uint disp, uint flags, nint tmpl);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(SafeFileHandle h, char[] buf, int len);

    /// <summary>在当前线程建一个仅消息窗口（之后的 WM_INPUT 由这个线程的消息循环分派）。</summary>
    public static nint CreateMessageWindow(WndProc proc)
    {
        var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), lpszClassName = "DeviceHub.RawInput" };
        RegisterClass(ref wc);
        return CreateWindowEx(0, "DeviceHub.RawInput", "", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, 0, 0);
    }

    /// <summary>注册 / 取消（hwnd = 0）接收键盘和鼠标的 Raw Input（后台也收）。</summary>
    public static bool Register(nint hwnd)
    {
        uint flags = hwnd == 0 ? RIDEV_REMOVE : RIDEV_INPUTSINK;
        var rid = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = flags, hwndTarget = hwnd },
            new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = flags, hwndTarget = hwnd },
        };
        return RegisterRawInputDevices(rid, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>WM_INPUT 的来源：(设备句柄, 是否键盘)；注入的输入句柄为 0。</summary>
    public static (nint Device, bool Keyboard) Header(nint lParam)
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_HEADER, buf, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == unchecked((uint)-1)) return (0, false);
            var h = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            return (h.hDevice, h.dwType == RIM_TYPEKEYBOARD);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>鼠标 WM_INPUT 的相对位移（绝对坐标设备返回 0）。</summary>
    public static int MouseMove(nint lParam)
    {
        uint size = 0, hdr = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(lParam, RID_INPUT, 0, ref size, hdr);
        if (size == 0) return 0;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, hdr) == unchecked((uint)-1)) return 0;
            if (Marshal.PtrToStructure<RAWINPUTHEADER>(buf).dwType != RIM_TYPEMOUSE) return 0;
            var m = Marshal.PtrToStructure<RAWMOUSE>(buf + (int)hdr);
            return (m.usFlags & 1) != 0 ? 0 : Math.Abs(m.lLastX) + Math.Abs(m.lLastY);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static readonly ConcurrentDictionary<nint, InputDeviceInfo?> Cache = new();

    /// <summary>设备句柄 → 设备信息（缓存；系统虚拟设备、没有 VID 的返回 null）。</summary>
    public static InputDeviceInfo? Resolve(nint hDevice, bool keyboard) =>
        hDevice == 0 ? null : Cache.GetOrAdd(hDevice, h => Describe(h, keyboard ? InputDeviceKind.Keyboard : InputDeviceKind.Mouse));

    /// <summary>当前连接的键盘和鼠标，按 (种类, Id) 合并。</summary>
    public static IReadOnlyList<InputDeviceInfo> List()
    {
        uint n = 0, sz = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref n, sz);
        var arr = new RAWINPUTDEVICELIST[n];
        if (n == 0 || GetRawInputDeviceList(arr, ref n, sz) == unchecked((uint)-1)) return [];
        return arr.Take((int)n)
            .Where(d => d.dwType is RIM_TYPEKEYBOARD or RIM_TYPEMOUSE)
            .Select(d => Describe(d.hDevice, d.dwType == RIM_TYPEKEYBOARD ? InputDeviceKind.Keyboard : InputDeviceKind.Mouse))
            .OfType<InputDeviceInfo>()
            .GroupBy(d => (d.Kind, d.Id))
            // 同一设备的多个集合名字可能不同（如"USB Receiver"与具体型号），取最长的
            .Select(g => g.OrderByDescending(d => d.Name.Length).First())
            .OrderBy(d => d.Kind).ThenBy(d => d.Name)
            .ToList();
    }

    private static InputDeviceInfo? Describe(nint h, InputDeviceKind kind)
    {
        uint size = 0;
        GetRawInputDeviceInfo(h, RIDI_DEVICENAME, null, ref size);
        if (size == 0) return null;
        var buf = new char[size];
        if (GetRawInputDeviceInfo(h, RIDI_DEVICENAME, buf, ref size) == unchecked((uint)-1)) return null;
        var path = new string(buf).TrimEnd('\0');
        // 远程桌面的虚拟键盘 / 鼠标常驻系统，不列出
        if (path.Contains("RDP_KBD", StringComparison.OrdinalIgnoreCase) || path.Contains("RDP_MOU", StringComparison.OrdinalIgnoreCase)) return null;
        string what = kind == InputDeviceKind.Keyboard ? "键盘" : "鼠标";
        // USB："VID_046D&PID_C52B"；蓝牙："_VID&0002046d_PID&b023"（前 4 位是 VID 来源）
        var m = VidPid().Match(path);
        if (m.Success)
        {
            ushort vid = Convert.ToUInt16(m.Groups[1].Value, 16), pid = Convert.ToUInt16(m.Groups[2].Value, 16);
            var name = Product(path);
            if (string.IsNullOrWhiteSpace(name)) name = $"{what} {vid:X4}:{pid:X4}";
            return new InputDeviceInfo { Kind = kind, Id = $"{vid:X4}:{pid:X4}", Vid = vid, Pid = pid, Name = name };
        }
        // 没有 VID 的（笔记本内置 PS/2 键盘、触控板）：用实例路径 "\\?\ACPI#PNP0303#4&xxx&0#{guid}" 去掉前后缀
        var id = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        int g = id.LastIndexOf("#{", StringComparison.Ordinal);
        if (g > 0) id = id[..g];
        bool acpi = id.StartsWith("ACPI#", StringComparison.OrdinalIgnoreCase);
        return new InputDeviceInfo { Kind = kind, Id = id, Name = acpi ? $"内置{what}（PS/2）" : $"{what}（{id.Split('#')[0]}）" };
    }

    private static string? Product(string path)
    {
        try
        {
            using var f = CreateFile(path, 0, 3, 0, 3 /* OPEN_EXISTING */, 0, 0);
            if (f.IsInvalid) return null;
            var b = new char[128];
            return HidD_GetProductString(f, b, b.Length * 2) ? new string(b).TrimEnd('\0').Trim() : null;
        }
        catch { return null; }
    }

    [GeneratedRegex(@"VID[_&](?:[0-9A-F]{4})?([0-9A-F]{4})[_&]PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();
}
