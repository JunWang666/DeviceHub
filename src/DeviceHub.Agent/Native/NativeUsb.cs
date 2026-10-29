using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DeviceHub.Contracts;
using DeviceHub.Agent.Core;
using Microsoft.Win32.SafeHandles;

namespace DeviceHub.Agent.Native;

/// <summary>
/// USB 拓扑扫描（与 USBView 同一套做法）：枚举主控制器 → 根集线器，再逐个集线器用 hub IOCTL
/// 读端口属性、连接信息与描述符。只读：不打开设备本身，描述符请求经集线器发出，不需要管理员权限。
/// usbioctl.h 的结构体都是 1 字节对齐，这里按偏移量直接解析字节。
/// </summary>
public static class NativeUsb
{
    private static readonly bool Trace = Environment.GetEnvironmentVariable("DEVICEHUB_USB_TRACE") == "1";
    private static void T(string s) { if (Trace) Console.Error.WriteLine($"[usb] {s}"); }

    public static UsbTopology Scan()
    {
        T("scan start");
        var pnp = PnpIndex.Build();
        T($"pnp done {pnp.ByInstance.Count}");
        var topo = new UsbTopology { ScannedAt = DateTimeOffset.Now };
        var hubs = new Dictionary<string, UsbHubInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, instanceId) in EnumerateInterfaces(GUID_DEVINTERFACE_USB_HOST_CONTROLLER))
        {
            T($"controller {path}");
            var node = instanceId is null ? null : pnp.ByInstance.GetValueOrDefault(instanceId);
            var ctrl = new UsbControllerInfo { Name = node?.Name ?? "USB 主控制器", InstanceId = instanceId };
            topo.Controllers.Add(ctrl);
            using var h = OpenHub(path);
            if (h.IsInvalid) { ctrl.Error = $"无法打开控制器（错误 {Marshal.GetLastWin32Error()}）"; continue; }
            var rootName = ReadName(h, IOCTL_USB_GET_ROOT_HUB_NAME, null);
            if (rootName is null) { ctrl.Error = "读不到根集线器"; continue; }
            ctrl.RootHub = ScanHub(rootName, isRoot: true, pnp, hubs, depth: 0);
        }
        topo.Controllers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

        MergeCompanions(hubs);
        foreach (var hub in hubs.Values)
            foreach (var port in hub.Ports)
                if (!port.MergedIntoCompanion && port.Device is { } d) UsbDiagnostics.Analyze(d, port, hub, hubs);
        return topo;
    }

    // —— 集线器 ——

    private static UsbHubInfo ScanHub(string name, bool isRoot, PnpIndex pnp, Dictionary<string, UsbHubInfo> hubs, int depth)
    {
        var hub = new UsbHubInfo { Name = name, IsRoot = isRoot };
        hubs[Key(name)] = hub;
        T($"hub {name}");
        using var h = OpenHub(@"\\.\" + name);
        if (h.IsInvalid || depth > 8) return hub;

        int portCount = 0;
        var nodeInfo = Ioctl(h, IOCTL_USB_GET_NODE_INFORMATION, new byte[76]);
        if (nodeInfo is { Length: >= 76 })
        {
            portCount = nodeInfo[6];
            if (!isRoot) hub.IsBusPowered = nodeInfo[75] != 0;
        }
        // HighestPortNumber 比 bNumberOfPorts 可靠（xHCI 根集线器的 USB 2/3 端口是分开编号的）
        var ex = Ioctl(h, IOCTL_USB_GET_HUB_INFORMATION_EX, new byte[77]);
        if (ex is { Length: >= 6 })
        {
            int type = BinaryPrimitives.ReadInt32LittleEndian(ex);
            hub.IsUsb3 = type == 3; // UsbRootHub=1, Usb20Hub=2, Usb30Hub=3
            portCount = Math.Max(portCount, BinaryPrimitives.ReadUInt16LittleEndian(ex.AsSpan(4)));
        }

        for (int i = 1; i <= portCount; i++)
        {
            var port = ScanPort(h, i, hub, pnp, hubs, depth);
            if (port != null) hub.Ports.Add(port);
        }
        // xHCI 根集线器的类型是 UsbRootHub，看端口协议判断是不是 USB 3
        if (isRoot) hub.IsUsb3 = hub.Ports.Any(p => p.SupportsUsb3);
        return hub;
    }

    private static UsbPortInfo? ScanPort(SafeFileHandle h, int index, UsbHubInfo hub, PnpIndex pnp,
        Dictionary<string, UsbHubInfo> hubs, int depth)
    {
        var port = new UsbPortInfo { Number = index };
        T($"  port {index}");

        // 端口属性：Type-C、是否外部可插拔、伴随端口
        var probe = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(probe, (uint)index);
        if (Ioctl(h, IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES, probe) is { Length: >= 16 } pc)
        {
            int need = (int)BinaryPrimitives.ReadUInt32LittleEndian(pc.AsSpan(4));
            if (need > 18)
            {
                var full = new byte[need];
                BinaryPrimitives.WriteUInt32LittleEndian(full, (uint)index);
                pc = Ioctl(h, IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES, full) ?? pc;
            }
            uint props = BinaryPrimitives.ReadUInt32LittleEndian(pc.AsSpan(8));
            port.IsUserConnectable = (props & 1) != 0;
            port.IsDebugCapable = (props & 2) != 0;
            port.IsTypeC = (props & 8) != 0;
            int companionPort = BinaryPrimitives.ReadUInt16LittleEndian(pc.AsSpan(14));
            var companionHub = pc.Length > 16 ? Encoding.Unicode.GetString(pc, 16, pc.Length - 16).TrimEnd('\0') : "";
            if (companionPort > 0)
            {
                port.CompanionPort = companionPort;
                port.CompanionHub = companionHub.Length > 0 ? Strip(companionHub) : hub.Name;
            }
        }

        // V2：端口支持的协议 + 设备是否"能更快"
        var v2in = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(v2in, (uint)index);
        BinaryPrimitives.WriteUInt32LittleEndian(v2in.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(v2in.AsSpan(8), 0b111); // 我们认识 1.1/2.0/3.x
        uint v2flags = 0;
        bool hasV2 = false;
        if (Ioctl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2, v2in) is { Length: >= 16 } v2)
        {
            hasV2 = true;
            uint protocols = BinaryPrimitives.ReadUInt32LittleEndian(v2.AsSpan(8));
            port.SupportsUsb2 = (protocols & 0b011) != 0;
            port.SupportsUsb3 = (protocols & 0b100) != 0;
            v2flags = BinaryPrimitives.ReadUInt32LittleEndian(v2.AsSpan(12));
        }
        if (!hasV2) { port.SupportsUsb3 = hub.IsUsb3; port.SupportsUsb2 = !hub.IsUsb3; }

        var ciIn = new byte[35 + 11 * 32];
        BinaryPrimitives.WriteUInt32LittleEndian(ciIn, (uint)index);
        var ci = Ioctl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, ciIn);
        if (ci is not { Length: >= 35 }) return port;
        port.Status = (UsbConnectionStatus)BinaryPrimitives.ReadInt32LittleEndian(ci.AsSpan(31));
        if (port.Status == UsbConnectionStatus.NoDevice) return port;

        var dd = ci.AsSpan(4, 18);
        var dev = new UsbDeviceInfo
        {
            BcdUsb = BinaryPrimitives.ReadUInt16LittleEndian(dd[2..]),
            DeviceClass = dd[4],
            Vid = BinaryPrimitives.ReadUInt16LittleEndian(dd[8..]),
            Pid = BinaryPrimitives.ReadUInt16LittleEndian(dd[10..]),
            BcdDevice = BinaryPrimitives.ReadUInt16LittleEndian(dd[12..]),
            IsHub = ci[24] != 0,
            Address = BinaryPrimitives.ReadUInt16LittleEndian(ci.AsSpan(25)),
        };
        byte iManufacturer = dd[14], iProduct = dd[15], iSerial = dd[16];
        port.Device = dev;

        dev.Speed = ci[23] switch { 0 => UsbSpeed.Low, 1 => UsbSpeed.Full, 2 => UsbSpeed.High, 3 => UsbSpeed.Super, _ => UsbSpeed.Unknown };
        if (dev.Speed == UsbSpeed.Super && (v2flags & 4) != 0) dev.Speed = UsbSpeed.SuperPlus;
        dev.SpeedMbps = dev.Speed switch { UsbSpeed.Low => 1, UsbSpeed.Full => 12, UsbSpeed.High => 480, UsbSpeed.Super => 5000, UsbSpeed.SuperPlus => 10000, _ => 0 };
        if (dev.Speed == UsbSpeed.SuperPlus) ReadSuperSpeedPlus(h, index, dev);

        // 枚举失败时描述符都是空的，不再往下问
        if (port.Status == UsbConnectionStatus.Connected)
        {
            ushort lang = ReadLangId(h, index);
            if (lang != 0)
            {
                dev.Manufacturer = ReadString(h, index, iManufacturer, lang);
                dev.Product = ReadString(h, index, iProduct, lang);
                dev.Serial = ReadString(h, index, iSerial, lang);
            }
            ReadConfiguration(h, index, dev);
            if (dev.BcdUsb >= 0x0201) ReadBos(h, index, dev, lang);
            // 以全速运行的 USB 2.0 设备：能取到设备限定符说明它支持高速
            if (dev.Speed == UsbSpeed.Full && dev.BcdUsb >= 0x0200 && GetDescriptor(h, index, 0x06, 0, 0, 10) is { Length: >= 10 })
                dev.CapableMbps = Math.Max(dev.CapableMbps, 480);
            if ((v2flags & 8) != 0) dev.CapableMbps = Math.Max(dev.CapableMbps, 10000);
            else if ((v2flags & 2) != 0) dev.CapableMbps = Math.Max(dev.CapableMbps, 5000);
        }
        dev.CapableMbps = Math.Max(dev.CapableMbps, dev.SpeedMbps);

        if (ReadName(h, IOCTL_USB_GET_NODE_CONNECTION_DRIVERKEY_NAME, index) is { } driverKey
            && pnp.ByDriver.TryGetValue(driverKey, out var node))
        {
            dev.InstanceId = node.InstanceId;
            dev.DriverDescription = node.Name;
            dev.ProblemCode = node.ProblemCode;
            dev.Functions = pnp.DescendantNames(node);
            dev.Product ??= node.BusReportedName;
        }
        dev.Name = dev.Product ?? dev.DriverDescription ?? $"USB 设备 {dev.Vid:X4}:{dev.Pid:X4}";

        if (dev.IsHub && ReadName(h, IOCTL_USB_GET_NODE_CONNECTION_NAME, index) is { } childHub)
            dev.Hub = ScanHub(childHub, isRoot: false, pnp, hubs, depth + 1);
        return port;
    }

    /// <summary>
    /// 一个物理 USB 3 接口在系统里是两个端口：USB 3 端口 + USB 2 伴随端口（外置 USB 3 集线器同理是两个集线器）。
    /// 把 USB 2 那一半并到 USB 3 端口上，界面上一个物理接口只显示一次。
    /// </summary>
    private static void MergeCompanions(Dictionary<string, UsbHubInfo> hubs)
    {
        foreach (var hub in hubs.Values)
            foreach (var p in hub.Ports)
            {
                if (!p.SupportsUsb3 || p.CompanionPort is not { } cp || p.CompanionHub is not { } ch) continue;
                if (!hubs.TryGetValue(Key(ch), out var other)) continue;
                var q = other.Ports.FirstOrDefault(x => x.Number == cp);
                if (q is null || q == p || q.SupportsUsb3 || q.MergedIntoCompanion) continue;
                q.MergedIntoCompanion = true;
                p.IsTypeC |= q.IsTypeC;
                p.IsUserConnectable |= q.IsUserConnectable;
                p.SupportsUsb2 = true;
                if (q.Device is null) continue;
                if (p.Device is null) { p.Device = q.Device; p.Status = q.Status; }
                else if (p.Device.IsHub && q.Device.IsHub) p.Device.Usb2Twin = q.Device.Hub;
                else { q.MergedIntoCompanion = false; continue; } // 两边各有一个非集线器设备（不应出现）：伴随口单独显示
                q.Device = null; // 已移到 USB 3 端口，避免同一设备在结果里出现两次
            }
    }

    // —— 描述符 ——

    private static void ReadConfiguration(SafeFileHandle h, int index, UsbDeviceInfo dev)
    {
        if (GetDescriptor(h, index, 0x02, 0, 0, 9) is not { Length: >= 9 } head) return;
        int total = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
        var cfg = total > 9 ? GetDescriptor(h, index, 0x02, 0, 0, Math.Min(total, 4096)) ?? head : head;
        byte attr = cfg[7];
        dev.SelfPowered = (attr & 0x40) != 0;
        dev.RemoteWakeup = (attr & 0x20) != 0;
        // bMaxPower 单位：USB 2 是 2 mA，SuperSpeed 是 8 mA
        dev.MaxPowerMa = cfg[8] * (dev.Speed >= UsbSpeed.Super ? 8 : 2);

        var classes = new List<string>();
        for (int o = 0; o + 2 <= cfg.Length && cfg[o] >= 2; o += cfg[o])
            if (cfg[o + 1] == 0x04 && o + 9 <= cfg.Length && cfg[o + 3] == 0) // 接口描述符，只取 alternate 0
                classes.Add(UsbNames.InterfaceClass(cfg[o + 5], cfg[o + 6], cfg[o + 7]));
        if (classes.Count == 0 && dev.DeviceClass != 0) classes.Add(UsbNames.InterfaceClass(dev.DeviceClass, 0, 0));
        dev.InterfaceClasses = classes.Distinct().ToList();
    }

    private static void ReadBos(SafeFileHandle h, int index, UsbDeviceInfo dev, ushort lang)
    {
        if (GetDescriptor(h, index, 0x0F, 0, 0, 5) is not { Length: >= 5 } head) return;
        int total = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
        if (total <= 5 || GetDescriptor(h, index, 0x0F, 0, 0, Math.Min(total, 4096)) is not { } bos) return;

        for (int o = 5; o + 3 <= bos.Length && bos[o] >= 3; o += bos[o])
        {
            var cap = bos.AsSpan(o, Math.Min(bos[o], bos.Length - o));
            switch (cap[2])
            {
                case 0x02 when cap.Length >= 7:
                    if ((BinaryPrimitives.ReadUInt32LittleEndian(cap[3..]) & 2) != 0) dev.Capabilities.Add("USB 2.0 LPM 省电");
                    break;
                case 0x03 when cap.Length >= 6:
                    dev.Capabilities.Add("SuperSpeed 5 Gbps");
                    dev.CapableMbps = Math.Max(dev.CapableMbps, 5000);
                    break;
                case 0x0A when cap.Length >= 12:
                {
                    int count = (int)(BinaryPrimitives.ReadUInt32LittleEndian(cap[4..]) & 0x1F) + 1;
                    int best = 0;
                    for (int k = 0; k < count && 12 + k * 4 + 4 <= cap.Length; k++)
                        best = Math.Max(best, LaneMbps(BinaryPrimitives.ReadUInt32LittleEndian(cap[(12 + k * 4)..])));
                    if (best > 0)
                    {
                        dev.Capabilities.Add($"SuperSpeed+ {best / 1000} Gbps/通道");
                        dev.CapableMbps = Math.Max(dev.CapableMbps, best);
                    }
                    break;
                }
                case 0x05: dev.Capabilities.Add(UsbNames.Platform(cap)); break;
                case 0x06: dev.Capabilities.Add("USB Power Delivery"); break;
                case 0x07: dev.Capabilities.Add("电池信息"); break;
                case 0x08: dev.Capabilities.Add("PD 受电端口"); break;
                case 0x09: dev.Capabilities.Add("PD 供电端口"); break;
                case 0x0D when cap.Length >= 44: ReadBillboard(h, index, cap, dev, lang); break;
            }
        }
    }

    /// <summary>
    /// Billboard 能力：Type-C 设备（扩展坞、视频转接头）进不了备用模式时，会以 Billboard 设备出现并说明原因。
    /// 每个模式 2 bit：0 未指定、1 未尝试或已退出、2 失败、3 成功。
    /// </summary>
    private static void ReadBillboard(SafeFileHandle h, int index, ReadOnlySpan<byte> cap, UsbDeviceInfo dev, ushort lang)
    {
        dev.Capabilities.Add("Billboard（备用模式状态）");
        int modes = cap[4];
        for (int m = 0; m < modes && 44 + m * 4 + 4 <= cap.Length; m++)
        {
            ushort svid = BinaryPrimitives.ReadUInt16LittleEndian(cap[(44 + m * 4)..]);
            int state = (cap[8 + m / 4] >> (m % 4 * 2)) & 3;
            byte iString = cap[44 + m * 4 + 3];
            var name = (lang != 0 && iString != 0 ? ReadString(h, index, iString, lang) : null) ?? UsbNames.Svid(svid);
            dev.AltModes.Add(new UsbAltMode(svid, name, (UsbAltModeState)state));
        }
    }

    private static int LaneMbps(uint attr)
    {
        int exp = (int)(attr >> 4) & 3, mantissa = (int)(attr >> 16);
        return exp switch { 3 => mantissa * 1000, 2 => mantissa, 1 => mantissa / 1000, _ => 0 };
    }

    /// <summary>SuperSpeed+ 的实际速率与通道数（Gen 2x1 = 10G×1，Gen 2x2 = 10G×2）。旧系统没有这个 IOCTL。</summary>
    private static void ReadSuperSpeedPlus(SafeFileHandle h, int index, UsbDeviceInfo dev)
    {
        var buf = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)index);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 24);
        if (Ioctl(h, IOCTL_USB_GET_NODE_CONNECTION_SUPERSPEEDPLUS_INFORMATION, buf) is not { Length: >= 24 } r) return;
        int lane = LaneMbps(BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(8)));
        int lanes = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(12));
        if (lane <= 0 || lanes is <= 0 or > 4) return;
        dev.Lanes = lanes;
        dev.SpeedMbps = lane * lanes;
        if (dev.SpeedMbps >= 20000) dev.Speed = UsbSpeed.SuperPlus20;
    }

    private static ushort ReadLangId(SafeFileHandle h, int index)
    {
        var d = GetDescriptor(h, index, 0x03, 0, 0, 255);
        if (d is not { Length: >= 4 }) return 0;
        // 有英文就用英文，否则用设备的第一种语言
        for (int o = 2; o + 2 <= d.Length; o += 2)
            if (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)) == 0x0409) return 0x0409;
        return BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
    }

    private static string? ReadString(SafeFileHandle h, int index, byte iString, ushort lang)
    {
        if (iString == 0) return null;
        var d = GetDescriptor(h, index, 0x03, iString, lang, 255);
        if (d is not { Length: > 2 } || d[1] != 0x03) return null;
        var s = Encoding.Unicode.GetString(d, 2, Math.Min(d[0], d.Length) - 2 & ~1).Trim('\0', ' ');
        return s.Length == 0 ? null : s;
    }

    /// <summary>经集线器发标准 GET_DESCRIPTOR（USB_DESCRIPTOR_REQUEST：ConnectionIndex + 8 字节 setup 包 + 数据）。</summary>
    private static byte[]? GetDescriptor(SafeFileHandle h, int index, byte type, byte descIndex, ushort wIndex, int length)
    {
        var buf = new byte[12 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)index);
        buf[4] = 0x80; buf[5] = 0x06;
        buf[6] = descIndex; buf[7] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), wIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), (ushort)length);
        T($"    desc type={type:X2} idx={descIndex} len={length}");
        var r = Ioctl(h, IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION, buf);
        return r is { Length: > 12 } ? r[12..] : null;
    }

    // —— IOCTL 工具 ——

    /// <summary>读 USB_NODE_CONNECTION_NAME / DRIVERKEY_NAME（index 非空）或 USB_ROOT_HUB_NAME（index 为空）。</summary>
    private static string? ReadName(SafeFileHandle h, uint code, int? index)
    {
        int head = index is null ? 4 : 8;
        var buf = new byte[head + 512];
        if (index is { } i) BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)i);
        var r = Ioctl(h, code, buf);
        if (r is null || r.Length <= head) return null;
        var s = Encoding.Unicode.GetString(r, head, r.Length - head).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    /// <summary>METHOD_BUFFERED：输入输出同一块缓冲区，返回实际写回的部分；失败为 null。</summary>
    private static byte[]? Ioctl(SafeFileHandle h, uint code, byte[] buf)
    {
        var outBuf = (byte[])buf.Clone();
        if (!DeviceIoControl(h, code, buf, buf.Length, outBuf, outBuf.Length, out int n, nint.Zero) || n <= 0) return null;
        return n == outBuf.Length ? outBuf : outBuf[..n];
    }

    private static SafeFileHandle OpenHub(string path) =>
        CreateFile(path, GENERIC_WRITE, FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);

    private static string Strip(string name) =>
        name.StartsWith(@"\\?\") || name.StartsWith(@"\\.\") ? name[4..] : name;

    private static string Key(string name) => Strip(name);

    private static IEnumerable<(string Path, string? InstanceId)> EnumerateInterfaces(Guid guid)
    {
        var list = new List<(string, string?)>();
        nint set = SetupDiGetClassDevs(ref guid, null, nint.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE) return list;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, nint.Zero, ref guid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref ifData, nint.Zero, 0, out uint need, nint.Zero);
                if (need == 0) continue;
                nint detail = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(detail, 8);
                    var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, need, out _, ref devInfo)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Length > 0) list.Add((path, PnpIndex.InstanceId(set, ref devInfo)));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    /// <summary>系统里所有在线设备节点，按驱动键（hub IOCTL 给的 DRIVERKEY_NAME）和实例 Id 索引。</summary>
    private sealed class PnpIndex
    {
        public sealed record Node(string InstanceId, string? Name, string? BusReportedName, int ProblemCode, string[] Children);

        public Dictionary<string, Node> ByDriver { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Node> ByInstance { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static PnpIndex Build()
        {
            var idx = new PnpIndex();
            nint set = SetupDiGetClassDevsAll(nint.Zero, null, nint.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == INVALID_HANDLE) return idx;
            try
            {
                var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref dev); i++)
                {
                    var id = InstanceId(set, ref dev);
                    if (id is null) continue;
                    var node = new Node(id,
                        RegString(set, ref dev, SPDRP_FRIENDLYNAME) ?? RegString(set, ref dev, SPDRP_DEVICEDESC),
                        PropString(set, ref dev, DEVPKEY_Device_BusReportedDeviceDesc),
                        PropInt(set, ref dev, DEVPKEY_Device_ProblemCode),
                        PropStrings(set, ref dev, DEVPKEY_Device_Children));
                    idx.ByInstance[id] = node;
                    if (RegString(set, ref dev, SPDRP_DRIVER) is { } drv) idx.ByDriver[drv] = node;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return idx;
        }

        /// <summary>设备下各功能节点的名称（复合设备的接口、HID 集合等），去重、去掉泛称。</summary>
        public List<string> DescendantNames(Node root)
        {
            var names = new List<string>();
            var queue = new Queue<(string Id, int Depth)>(root.Children.Select(c => (c, 1)));
            while (queue.Count > 0 && names.Count < 24)
            {
                var (id, depth) = queue.Dequeue();
                if (!ByInstance.TryGetValue(id, out var n)) continue;
                if (n.Name is { } name && !names.Contains(name)) names.Add(name);
                if (depth < 3) foreach (var c in n.Children) queue.Enqueue((c, depth + 1));
            }
            return names;
        }

        public static string? InstanceId(nint set, ref SP_DEVINFO_DATA dev)
        {
            var sb = new StringBuilder(512);
            return SetupDiGetDeviceInstanceId(set, ref dev, sb, sb.Capacity, out _) ? sb.ToString() : null;
        }

        private static string? RegString(nint set, ref SP_DEVINFO_DATA dev, uint prop)
        {
            var buf = new byte[1024];
            if (!SetupDiGetDeviceRegistryProperty(set, ref dev, prop, out _, buf, buf.Length, out int n) || n < 2) return null;
            var s = Encoding.Unicode.GetString(buf, 0, n).TrimEnd('\0');
            return s.Length == 0 ? null : s;
        }

        private static byte[]? Prop(nint set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key)
        {
            var buf = new byte[4096];
            return SetupDiGetDeviceProperty(set, ref dev, ref key, out _, buf, (uint)buf.Length, out uint n, 0) ? buf[..(int)n] : null;
        }

        private static string? PropString(nint set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key)
        {
            var s = Prop(set, ref dev, key) is { } b ? Encoding.Unicode.GetString(b).TrimEnd('\0') : null;
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static string[] PropStrings(nint set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key) =>
            Prop(set, ref dev, key) is { } b
                ? Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();

        private static int PropInt(nint set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key) =>
            Prop(set, ref dev, key) is { Length: >= 4 } b ? BinaryPrimitives.ReadInt32LittleEndian(b) : 0;
    }

    // —— P/Invoke ——
    private static readonly Guid GUID_DEVINTERFACE_USB_HOST_CONTROLLER = new("3abf6f2d-71c4-462a-8a92-1e6861e6af27");
    public static readonly Guid GUID_DEVINTERFACE_USB_DEVICE = new("a5dcbf10-6530-11d2-901f-00c04fb951ed");
    public static readonly Guid GUID_DEVINTERFACE_USB_HUB = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    // CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IOCTL_USB_GET_ROOT_HUB_NAME = 0x220408;                 // HCD_GET_ROOT_HUB_NAME 258（发给控制器）
    private const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408;              // 258（发给集线器）
    private const uint IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION = 0x220410; // 260
    private const uint IOCTL_USB_GET_NODE_CONNECTION_NAME = 0x220414;          // 261
    private const uint IOCTL_USB_GET_NODE_CONNECTION_DRIVERKEY_NAME = 0x220420; // 264
    private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448; // 274
    private const uint IOCTL_USB_GET_HUB_INFORMATION_EX = 0x220454;            // 277
    private const uint IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES = 0x220458;     // 278
    private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x22045C; // 279
    private const uint IOCTL_USB_GET_NODE_CONNECTION_SUPERSPEEDPLUS_INFORMATION = 0x220460; // 280

    private static readonly nint INVALID_HANDLE = -1;
    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_WRITE = 0x40000000, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint SPDRP_DEVICEDESC = 0x00, SPDRP_DRIVER = 0x09, SPDRP_FRIENDLYNAME = 0x0C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static readonly DEVPROPKEY DEVPKEY_Device_ProblemCode = new() { fmtid = new("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 3 };
    private static readonly DEVPROPKEY DEVPKEY_Device_Children = new() { fmtid = new("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 9 };
    private static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new() { fmtid = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, nint ov);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint sec, uint disp, uint flags, nint tmpl);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid g, string? enumerator, nint hwnd, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern nint SetupDiGetClassDevsAll(nint g, string? enumerator, nint hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SP_DEVICE_INTERFACE_DATA data, nint detail, uint size, out uint required, nint devInfo);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SP_DEVICE_INTERFACE_DATA data, nint detail, uint size, out uint required, ref SP_DEVINFO_DATA devInfo);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint set, ref SP_DEVINFO_DATA dev, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref SP_DEVINFO_DATA dev, uint prop, out uint type, byte[] buf, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    private static extern bool SetupDiGetDeviceProperty(nint set, ref SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
