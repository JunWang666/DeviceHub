namespace DeviceHub.Plugin.Logitech;

/// <summary>电量。Percent 为 null 时只有 Level 文本（如 1.0 设备只报"低 / 正常"）。</summary>
public sealed class BatteryState
{
    public int? Percent { get; set; }
    /// <summary>电量由电压估算（0x1001），不是设备直接报告的百分比。</summary>
    public bool Estimated { get; set; }
    public int? VoltageMv { get; set; }
    public bool Charging { get; set; }
    public bool Full { get; set; }
    public string Status { get; set; } = "";
}

public sealed class DpiState
{
    public int Value { get; set; }
    public int Default { get; set; }
    /// <summary>离散档位；为空时用 Min/Max/Step 的连续范围。</summary>
    public List<int> Choices { get; set; } = new();
    public int Min { get; set; }
    public int Max { get; set; }
    public int Step { get; set; }
}

public sealed class ReportRateState
{
    /// <summary>当前回报间隔（毫秒）。</summary>
    public int Ms { get; set; }
    public List<int> Choices { get; set; } = new();
}

public sealed class SmartShiftState
{
    /// <summary>true = 棘轮（段落感），false = 自由滚动。</summary>
    public bool Ratchet { get; set; }
    /// <summary>自动切换到自由滚动的灵敏度 1–254（越大越难切换）；255 = 始终棘轮。</summary>
    public int Threshold { get; set; }
    public bool Enhanced { get; set; }
}

public sealed class WheelState
{
    public bool HiRes { get; set; }
    public bool Inverted { get; set; }
    /// <summary>滚轮被诊断/重映射软件转发到 HID++（target 位）；此时系统收不到滚轮事件。</summary>
    public bool Diverted { get; set; }
    public int Multiplier { get; set; }
}

public sealed class HostState
{
    public int Count { get; set; }
    /// <summary>当前通道，0 起。</summary>
    public int Current { get; set; }
}

public sealed class OnboardState
{
    /// <summary>true = 板载模式（设备用存储的配置，忽略软件设置的 DPI / 回报率），false = 主机模式。</summary>
    public bool Onboard { get; set; }
    public int ProfileCount { get; set; }
    /// <summary>当前配置，1 起；0 = 未知。</summary>
    public int Profile { get; set; }
}

public sealed class FeatureEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Index { get; set; }
}

/// <summary>
/// 设备上的一个可编程按键 / 控件（HID++ 0x1B04 Reprogrammable Controls v4）。
/// 接管（divert）后按键不再执行原功能，按下 / 抬起由设备以 HID++ 事件报告，本插件转给核心的按键映射。
/// </summary>
public sealed class ControlState
{
    /// <summary>控件 id（CID），如 0x00C3 = 手势键。</summary>
    public int Cid { get; set; }
    /// <summary>默认功能（TID）。</summary>
    public int Tid { get; set; }
    public string Name { get; set; } = "";
    /// <summary>getCidInfo 的标志位（低字节 flags1、高字节 flags2）。</summary>
    public int Flags { get; set; }
    public bool IsMouse { get; set; }
    public bool Divertable { get; set; }
    public bool Diverted { get; set; }
    /// <summary>接管后当前是否按着（设备事件实时更新）。</summary>
    public bool Pressed { get; set; }
}

/// <summary>接收器上的一个配对设备，或直连设备本身（Index = 0xFF）。</summary>
public sealed class HidppDeviceState
{
    public int Index { get; set; }
    /// <summary>子设备 Id（序列号；读不到时为 slotN），直连设备为空。用作核心的子设备键。</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>mouse / keyboard / headset … 见 <see cref="HidppParse.DeviceType"/>。</summary>
    public string Kind { get; set; } = "unknown";
    /// <summary>无线产品 id（接收器配对信息），直连设备为空。</summary>
    public string Wpid { get; set; } = "";
    public bool Online { get; set; }
    public string Protocol { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Status { get; set; } = "";

    public BatteryState? Battery { get; set; }
    public DpiState? Dpi { get; set; }
    public ReportRateState? ReportRate { get; set; }
    /// <summary>指针速度，256 = 1.0 倍。</summary>
    public int? PointerSpeed { get; set; }
    public SmartShiftState? SmartShift { get; set; }
    public WheelState? Wheel { get; set; }
    public bool? FnSwap { get; set; }
    public HostState? Hosts { get; set; }
    public OnboardState? Onboard { get; set; }
    /// <summary>可编程按键（0x1B04）；设备不支持时为空。</summary>
    public List<ControlState> Controls { get; set; } = new();

    public List<FeatureEntry> Features { get; set; } = new();
    /// <summary>识别到但本插件尚未实现的功能（灯效、按键重映射…），供 UI 列出。</summary>
    public List<string> Unsupported { get; set; } = new();
}

/// <summary>推给 UI 的完整状态（camelCase JSON，与 UI 侧 LogitechStateDto 对应）。</summary>
public sealed class LogitechState
{
    public bool Authorized { get; set; }
    public bool Connected { get; set; }
    public string Status { get; set; } = "";

    public bool IsReceiver { get; set; }
    /// <summary>这是接收器上某个配对设备的视图（子设备页），Devices 只含这一个设备。</summary>
    public bool IsChild { get; set; }
    public string ProductName { get; set; } = "";
    /// <summary>unifying / bolt / lightspeed / nano / 空（直连设备）。</summary>
    public string ReceiverKind { get; set; } = "";
    public string ReceiverFirmware { get; set; } = "";
    public string Link { get; set; } = "";
    /// <summary>可在本软件里配对新设备、取消配对（目前只支持 Unifying 接收器）。</summary>
    public bool CanPair => IsReceiver && ReceiverKind == "unifying";
    /// <summary>接收器的配对窗口正打开着，等新设备开机。</summary>
    public bool PairingOpen { get; set; }
    /// <summary>配对 / 取消配对的进度与结果，如"已配对：MX Master 3"。</summary>
    public string PairingStatus { get; set; } = "";

    public List<HidppDeviceState> Devices { get; set; } = new();
    public DateTimeOffset? LastReadAt { get; set; }
}
