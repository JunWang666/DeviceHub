using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// 只读 HID 探针：枚举 VID_1A2C 的 HID 集合，打印能力并读取 Feature Report。
// 不做任何写入，避免改动真实键盘配置。
internal static class Program
{
    static void Main(string[] args)
    {
        if (args.Contains("--led")) { SafeLedBlink(); return; }
        if (args.Contains("--listen")) { Listen(); return; }
        if (args.Contains("--capture")) { Capture(args); return; }
        if (args.Contains("--panel")) { SendPanel(args); return; }
        HidD_GetHidGuid(out Guid hidGuid);
        nint set = SetupDiGetClassDevs(ref hidGuid, null, nint.Zero, 0x12);
        var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

        for (uint i = 0; SetupDiEnumDeviceInterfaces(set, nint.Zero, ref hidGuid, i, ref ifData); i++)
        {
            SetupDiGetDeviceInterfaceDetail(set, ref ifData, nint.Zero, 0, out uint need, nint.Zero);
            if (need == 0) continue;
            nint detail = Marshal.AllocHGlobal((int)need);
            try
            {
                Marshal.WriteInt32(detail, 8);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, need, out _, nint.Zero)) continue;
                string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                if (path.Length == 0 || path.IndexOf("vid_1a2c", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Probe(path);
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        SetupDiDestroyDeviceInfoList(set);
    }

    static void Probe(string path)
    {
        using var h = CreateFile(path, 0, 3, nint.Zero, 3, 0, nint.Zero);
        if (h.IsInvalid) { Console.WriteLine($"[skip] open-fail {Short(path)}"); return; }

        var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        HidD_GetAttributes(h, ref attr);
        if (!HidD_GetPreparsedData(h, out nint pp)) { Console.WriteLine($"[skip] no-preparsed {Short(path)}"); return; }
        try
        {
            HidP_GetCaps(pp, out var caps);
            Console.WriteLine($"--- {Short(path)}");
            Console.WriteLine($"    VID={attr.VendorID:X4} PID={attr.ProductID:X4} Ver={attr.VersionNumber:X4}");
            Console.WriteLine($"    UsagePage=0x{caps.UsagePage:X4} Usage=0x{caps.Usage:X2}  In={caps.InputReportByteLength} Out={caps.OutputReportByteLength} Feat={caps.FeatureReportByteLength}");

            if (caps.FeatureReportByteLength > 0)
            {
                using var rw = CreateFile(path, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
                if (!rw.IsInvalid)
                    for (int id = 0; id <= 0x0F; id++)
                    {
                        var buf = new byte[caps.FeatureReportByteLength];
                        buf[0] = (byte)id;
                        if (HidD_GetFeature(rw, buf, buf.Length))
                        {
                            bool trivial = buf.Skip(1).Distinct().Count() <= 1;
                            Console.WriteLine($"    GetFeature id=0x{id:X2}: {Hex(buf, 20)}{(trivial ? "  (trivial)" : "")}");
                        }
                    }
            }
            var sb = new byte[256];
            if (HidD_GetProductString(h, sb, sb.Length)) Console.WriteLine($"    Product='{Str(sb)}'");
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    /// <summary>找到匹配路径（子串）的 HID 接口完整路径。</summary>
    static string? FindPath(string contains)
    {
        HidD_GetHidGuid(out Guid g);
        nint set = SetupDiGetClassDevs(ref g, null, nint.Zero, 0x12);
        var d = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
        try
        {
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, nint.Zero, ref g, i, ref d); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref d, nint.Zero, 0, out uint need, nint.Zero);
                if (need == 0) continue;
                nint det = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(det, 8);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref d, det, need, out _, nint.Zero)) continue;
                    string p = Marshal.PtrToStringUni(det + 4) ?? "";
                    if (p.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return p;
                }
                finally { Marshal.FreeHGlobal(det); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    /// <summary>安全地给键盘写 LED 输出报文（全 0，无害可逆），用于验证 ETW 能抓到输出报文负载。</summary>
    static void SafeLedBlink()
    {
        var path = FindPath("vid_1a2c&pid_7fff&mi_00");
        if (path == null) { Console.WriteLine("mi_00 not found"); return; }
        using var h = CreateFile(path, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
        if (h.IsInvalid) { Console.WriteLine("open mi_00 failed " + Marshal.GetLastWin32Error()); return; }
        var rep = new byte[] { 0x00, 0x00 }; // report id 0, LED bitmap 0 = all off
        bool ok = HidD_SetOutputReport(h, rep, rep.Length);
        Console.WriteLine($"LED output write ok={ok} err={Marshal.GetLastWin32Error()}");
    }

    /// <summary>只读监听厂商输入通道，看设备是否主动上报状态。</summary>
    static void Listen()
    {
        foreach (var frag in new[] { "mi_02#", "mi_01&col04" })
        {
            var path = FindPath("vid_1a2c&pid_7fff&" + frag);
            if (path == null) { Console.WriteLine($"{frag}: not found"); continue; }
            using var h = CreateFile(path, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
            if (h.IsInvalid) { Console.WriteLine($"{frag}: open failed {Marshal.GetLastWin32Error()}"); continue; }
            HidD_GetPreparsedData(h, out nint pp); HidP_GetCaps(pp, out var caps); HidD_FreePreparsedData(pp);
            int len = Math.Max((int)caps.InputReportByteLength, 2);
            var buf = new byte[len];
            Console.WriteLine($"{frag}: listening {len}B for 1.5s...");
            var done = new ManualResetEventSlim();
            var th = new Thread(() => { if (ReadFile(h, buf, (uint)len, out uint read, nint.Zero)) Console.WriteLine($"  IN({read}): {Hex(buf, len)}"); done.Set(); }) { IsBackground = true };
            th.Start();
            if (!done.Wait(1500)) Console.WriteLine("  (no input in 1.5s)");
        }
    }

    /// <summary>并发监听 mi_02 输入报文（设备回包会广播给所有句柄），捕获官方软件操作时设备的应答。</summary>
    static void Capture(string[] args)
    {
        int secs = 20;
        var si = Array.IndexOf(args, "--capture");
        if (si >= 0 && si + 1 < args.Length && int.TryParse(args[si + 1], out var s)) secs = s;

        var path = FindPath("vid_1a2c&pid_7fff&mi_02#");
        if (path == null) { Console.WriteLine("mi_02 not found"); return; }
        var h = CreateFile(path, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
        if (h.IsInvalid) { Console.WriteLine("open mi_02 failed " + Marshal.GetLastWin32Error()); return; }
        HidD_GetPreparsedData(h, out nint pp); HidP_GetCaps(pp, out var caps); HidD_FreePreparsedData(pp);
        int len = caps.InputReportByteLength;

        Console.WriteLine($"capturing mi_02 input ({len}B) for {secs}s — 现在去官方软件里操作...");
        var start = DateTime.Now;
        new Thread(() => { Thread.Sleep(secs * 1000); Console.WriteLine("done."); Environment.Exit(0); }) { IsBackground = true }.Start();
        string? last = null; int n = 0;
        while (true)
        {
            var buf = new byte[len];
            if (!ReadFile(h, buf, (uint)len, out uint read, nint.Zero)) { Thread.Sleep(10); continue; }
            var hex = Hex(buf, (int)read);
            if (hex == last) continue; // 去重连续相同
            last = hex;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] IN: {hex}");
            if (++n > 2000) { Console.WriteLine("(capped)"); break; }
        }
    }

    /// <summary>
    /// 写一帧已核实的面板灯效（与官方软件实抓格式逐字节一致）：--panel 模式 亮度 速度 [--yes]。
    /// 只能拼出 BB AA 99 88 AA 帧；不带 --yes 只打印不写。
    /// </summary>
    static void SendPanel(string[] args)
    {
        var i = Array.IndexOf(args, "--panel");
        if (i + 3 >= args.Length || !int.TryParse(args[i + 1], out var mode) || !int.TryParse(args[i + 2], out var bright) || !int.TryParse(args[i + 3], out var speed)
            || mode is < 0 or > 19 or 1 || bright is < 0 or > 5 || speed is < 0 or > 4)
        { Console.WriteLine("usage: --panel <mode 0,2..19> <brightness 0..5> <speed 0..4> [--yes]"); return; }

        var frame = new byte[65];
        new byte[] { 0x00, 0xBB, 0xAA, 0x99, 0x88, 0xAA, (byte)mode, (byte)bright, (byte)speed }.CopyTo(frame, 0);
        Console.WriteLine("frame: " + Hex(frame, 65));
        if (!args.Contains("--yes")) { Console.WriteLine("(dry run, add --yes to write)"); return; }

        var path = FindPath("vid_1a2c&pid_7fff&mi_02#");
        if (path == null) { Console.WriteLine("mi_02 not found"); return; }
        using var h = CreateFile(path, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
        if (h.IsInvalid) { Console.WriteLine("open mi_02 failed " + Marshal.GetLastWin32Error()); return; }
        bool ok = WriteFile(h, frame, (uint)frame.Length, out uint n, nint.Zero);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] WriteFile ok={ok} written={n} err={Marshal.GetLastWin32Error()}");
    }

    static string Short(string p) { var m = p.Split('#'); return m.Length >= 3 ? m[1] + "#" + m[2] : p; }
    static string Hex(byte[] b, int n) => string.Join(" ", b.Take(Math.Min(n, b.Length)).Select(x => x.ToString("X2")));
    static string Str(byte[] b) => System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid g; public uint Flags; public nint R; }
    [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out nint pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(nint pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(nint pp, out HIDP_CAPS caps);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll")] static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] b, int len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, nint ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] b, uint n, out uint read, nint ov);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern nint SetupDiGetClassDevs(ref Guid g, string? e, nint w, uint f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(nint s, nint d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(nint s, ref SP_DEVICE_INTERFACE_DATA d, nint detail, uint size, out uint req, nint di);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(nint s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, nint sec, uint d, uint f, nint t);
}
