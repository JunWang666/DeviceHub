namespace DeviceHub.Plugin.Logitech;

/// <summary>
/// 罗技 HID++ 协议（1.0 寄存器 + 2.0 Feature）。帧格式与 Feature 定义参照开源实现：
/// Solaar（lib/logitech_receiver）、libratbag（src/hidpp10.c / hidpp20.c，Piper 的后端）、logiops。
///
/// 帧：[report id, 设备号, feature 索引 / 子 id, (function &lt;&lt; 4) | software id, 参数…]
/// · 0x10 短报告 7 字节、0x11 长报告 20 字节（均含 report id）。Windows 下二者在不同的顶层集合里
///   （0xFF00:0001 / 0xFF00:0002；蓝牙设备为 0xFF43:0202，只有长报告）。
/// · 设备号 0xFF = 接收器本身或直连（有线 / 蓝牙）设备；1–6 = 接收器上配对的设备。
/// · HID++ 2.0 请求一律用长报告（规范要求 2.0 设备必须支持长报告，部分设备不支持短报告）；
///   HID++ 1.0 寄存器读写用短报告。
/// </summary>
public static class Hidpp
{
    public const byte ShortId = 0x10, LongId = 0x11;
    public const int ShortLength = 7, LongLength = 20;
    public const byte Receiver = 0xFF;

    /// <summary>本插件的 software id（低 4 位），用来把回包与 G HUB / Options+ 等软件的请求区分开；0 为设备通知。</summary>
    public const byte SoftwareId = 0x0D;

    // —— HID++ 1.0 ——
    public const byte SetRegister = 0x80, GetRegister = 0x81, SetLongRegister = 0x82, GetLongRegister = 0x83;
    public const byte Error10 = 0x8F;
    public const byte RegNotifications = 0x00, RegReceiverFirmware = 0xF1, RegReceiverInfo = 0xB5;
    /// <summary>设备连接 / 断开通知（子 id）。</summary>
    public const byte DeviceConnection = 0x41;

    // —— HID++ 2.0 ——
    public const byte Error20 = 0xFF;

    /// <summary>一次请求：报文、用于匹配回包的头三字节、是否 1.0 寄存器访问。</summary>
    public sealed record Request(byte[] Data, byte Device, byte Sub, byte Address, string Note)
    {
        public bool IsReply(byte[] r) => r.Length >= 4 && r[1] == Device && r[2] == Sub && r[3] == Address;

        /// <summary>错误回包：1.0 为 [10 dev 8F sub addr code]，2.0 为 [11 dev FF feat fnsw code]。返回错误码。</summary>
        public byte? ErrorCode(byte[] r)
        {
            if (r.Length < 6 || r[1] != Device || r[3] != Sub || r[4] != Address) return null;
            return r[2] is Error10 or Error20 ? r[5] : null;
        }
    }

    /// <summary>
    /// 一次请求的结果：Data = 匹配的回包（含 report id，参数从 [4] 开始）；Error = 设备回的错误码；
    /// Failed = I/O 层失败（未授权、设备断开，原因已写入状态）；三者都没有即超时。
    /// </summary>
    public sealed record Reply(byte[]? Data, byte? Error, bool Is10Error, bool Failed)
    {
        public static readonly Reply Fail = new(null, null, false, true);
        public static readonly Reply Timeout = new(null, null, false, false);
    }

    /// <summary>HID++ 2.0 Feature 调用（长报告）。</summary>
    public static Request Call(byte device, byte featureIndex, int function, string note, params byte[] args)
    {
        var d = new byte[LongLength];
        d[0] = LongId; d[1] = device; d[2] = featureIndex;
        d[3] = (byte)((function << 4) | SoftwareId);
        Array.Copy(args, 0, d, 4, Math.Min(args.Length, LongLength - 4));
        return new Request(d, device, featureIndex, d[3], note);
    }

    /// <summary>HID++ 1.0 短寄存器读写（短报告，3 字节参数）。</summary>
    public static Request Register(byte device, byte sub, byte register, string note, params byte[] args)
    {
        var d = new byte[ShortLength];
        d[0] = ShortId; d[1] = device; d[2] = sub; d[3] = register;
        Array.Copy(args, 0, d, 4, Math.Min(args.Length, 3));
        return new Request(d, device, sub, register, note);
    }

    /// <summary>
    /// Root.getProtocolVersion（feature 0，function 1），带一个 ping 字节。
    /// 2.0 设备回 [major, minor, ping]；1.0 设备 / 接收器回 1.0 错误（invalid sub id）；离线设备回 1.0 错误 0x09。
    /// </summary>
    public static Request Ping(byte device) => Call(device, 0x00, 1, "探测协议版本", 0, 0, 0x5A);

    /// <summary>Root.getFeature（function 0）：feature id → [索引, 类型, 版本]，索引 0 表示不支持。</summary>
    public static Request GetFeature(byte device, ushort featureId) =>
        Call(device, 0x00, 0, $"查询 Feature 0x{featureId:X4}", (byte)(featureId >> 8), (byte)featureId);

    public static string Error10Text(byte code) => code switch
    {
        0x01 => "无效的子命令", 0x02 => "无效地址", 0x03 => "无效值", 0x04 => "连接请求失败",
        0x05 => "设备过多", 0x06 => "已存在", 0x07 => "忙", 0x08 => "未知设备（未配对）",
        0x09 => "设备未连接（休眠或关机）", 0x0A => "请求不可用", 0x0B => "参数无效", 0x0C => "PIN 错误",
        _ => $"错误 0x{code:X2}",
    };

    public static string Error20Text(byte code) => code switch
    {
        0x01 => "未知错误", 0x02 => "参数无效", 0x03 => "超出范围", 0x04 => "硬件错误",
        0x05 => "内部错误", 0x06 => "Feature 索引无效", 0x07 => "Function 无效", 0x08 => "设备忙",
        0x09 => "不支持", _ => $"错误 0x{code:X2}",
    };
}

/// <summary>插件用到的 HID++ 2.0 Feature（id 取自 Solaar hidpp20_constants / libratbag hidpp20.h）。</summary>
public static class Feature
{
    public const ushort Root = 0x0000;
    public const ushort FeatureSet = 0x0001;
    public const ushort FirmwareInfo = 0x0003;
    public const ushort DeviceName = 0x0005;
    public const ushort BatteryStatus = 0x1000;
    public const ushort BatteryVoltage = 0x1001;
    public const ushort UnifiedBattery = 0x1004;
    public const ushort ChangeHost = 0x1814;
    public const ushort ReprogControlsV4 = 0x1B04;
    public const ushort WirelessStatus = 0x1D4B;
    public const ushort SmartShift = 0x2110;
    public const ushort SmartShiftEnhanced = 0x2111;
    public const ushort HiResWheel = 0x2121;
    public const ushort AdjustableDpi = 0x2201;
    public const ushort ExtendedDpi = 0x2202;
    public const ushort PointerSpeed = 0x2205;
    public const ushort FnInversion = 0x40A0;
    public const ushort NewFnInversion = 0x40A2;
    public const ushort K375sFnInversion = 0x40A3;
    public const ushort ReportRate = 0x8060;
    public const ushort ExtendedReportRate = 0x8061;
    public const ushort ColorLedEffects = 0x8070;
    public const ushort RgbEffects = 0x8071;
    public const ushort OnboardProfiles = 0x8100;

    /// <summary>启动时逐个查询索引的 Feature（Root 固定为索引 0，不用查）。</summary>
    public static readonly ushort[] Probe =
    [
        FeatureSet, FirmwareInfo, DeviceName, BatteryStatus, BatteryVoltage, UnifiedBattery, ChangeHost,
        ReprogControlsV4, WirelessStatus, SmartShift, SmartShiftEnhanced, HiResWheel, AdjustableDpi, ExtendedDpi,
        PointerSpeed, FnInversion, NewFnInversion, K375sFnInversion, ReportRate, ExtendedReportRate,
        ColorLedEffects, RgbEffects, OnboardProfiles,
    ];

    public static string Name(ushort id) => id switch
    {
        FeatureSet => "Feature 列表", FirmwareInfo => "固件信息", DeviceName => "设备名称",
        BatteryStatus => "电池状态", BatteryVoltage => "电池电压", UnifiedBattery => "统一电池",
        ChangeHost => "多设备切换（Easy-Switch）", ReprogControlsV4 => "按键重映射 v4", WirelessStatus => "无线状态",
        SmartShift => "SmartShift 滚轮", SmartShiftEnhanced => "SmartShift 滚轮（增强）", HiResWheel => "高精度滚轮",
        AdjustableDpi => "DPI", ExtendedDpi => "扩展 DPI", PointerSpeed => "指针速度",
        FnInversion or NewFnInversion or K375sFnInversion => "Fn 键反转",
        ReportRate => "回报率", ExtendedReportRate => "扩展回报率",
        ColorLedEffects => "灯效", RgbEffects => "RGB 灯效", OnboardProfiles => "板载配置",
        _ => $"0x{id:X4}",
    };
}

/// <summary>回包解析（参数从 r[4] 开始）。</summary>
public static class HidppParse
{
    /// <summary>设备类型（DeviceName.getDeviceType）。</summary>
    public static string DeviceType(byte t) => t switch
    {
        0 => "keyboard", 1 => "remote", 2 => "numpad", 3 => "mouse", 4 => "touchpad", 5 => "trackball",
        6 => "presenter", 7 => "receiver", 8 => "headset", 9 => "webcam", 10 => "wheel", 11 => "joystick",
        12 => "gamepad", 13 => "dock", 14 => "speaker", 15 => "microphone", _ => "unknown",
    };

    /// <summary>接收器配对信息里的设备种类（低 4 位）。</summary>
    public static string PairedKind(int k) => k switch
    {
        1 => "keyboard", 2 => "mouse", 3 => "numpad", 4 => "presenter", 8 => "trackball", 9 => "touchpad",
        _ => "unknown",
    };

    /// <summary>FirmwareInfo.getFwInfo：[类型, 前缀×3, 版本(BCD), 修订(BCD), build(2)…] → "RQM 12.01.B0010"。</summary>
    public static string Firmware(byte[] r)
    {
        var prefix = System.Text.Encoding.ASCII.GetString(r, 5, 3).Trim('\0', ' ');
        var s = $"{r[8]:X2}.{r[9]:X2}.B{r[10]:X2}{r[11]:X2}";
        return prefix.Length > 0 ? $"{prefix} {s}" : s;
    }

    /// <summary>
    /// AdjustableDpi.getSensorDpiList：[sensor, u16…]，0 结束；0xE000 | step 表示"前一个值到后一个值之间按 step 递增"。
    /// 返回 (离散列表, 范围最小, 最大, 步进)；范围形式时离散列表为空。
    /// </summary>
    public static (List<int> List, int Min, int Max, int Step) DpiList(byte[] r)
    {
        var values = new List<int>();
        int step = 0;
        for (int i = 5; i + 1 < r.Length; i += 2)
        {
            int v = (r[i] << 8) | r[i + 1];
            if (v == 0) break;
            if (v >> 13 == 0b111) step = v & 0x1FFF;
            else values.Add(v);
        }
        if (step > 0 && values.Count >= 2) return ([], values[0], values[^1], step);
        return (values, values.Count > 0 ? values.Min() : 0, values.Count > 0 ? values.Max() : 0, 0);
    }

    /// <summary>
    /// 只有电压的设备（0x1001）用 Solaar 的放电曲线估算电量（mV → %）。
    /// </summary>
    public static int VoltageToPercent(int mv)
    {
        (int Mv, int Pct)[] curve =
        [
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
            (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
        ];
        if (mv >= curve[0].Mv) return 100;
        for (int i = 1; i < curve.Length; i++)
        {
            if (mv < curve[i].Mv) continue;
            var (hiMv, hiPct) = curve[i - 1];
            var (loMv, loPct) = curve[i];
            return loPct + (mv - loMv) * (hiPct - loPct) / (hiMv - loMv);
        }
        return 0;
    }

    public static string BatteryStatus(byte s) => s switch
    {
        0 => "放电中", 1 => "充电中", 2 => "即将充满", 3 => "已充满", 4 => "慢速充电",
        5 => "电池异常", 6 => "温度异常", _ => "",
    };

    public static string UnifiedCharging(byte s) => s switch
    {
        0 => "放电中", 1 => "充电中", 2 => "慢速充电", 3 => "已充满", 4 => "充电异常", _ => "",
    };
}
