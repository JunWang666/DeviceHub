namespace DeviceHub.Plugin.Logitech.Ui;

// 与后端 LogitechState 对应的 UI 侧 DTO（camelCase）。
public sealed class BatteryDto
{
    public int? Percent { get; set; }
    public bool Estimated { get; set; }
    public int? VoltageMv { get; set; }
    public bool Charging { get; set; }
    public string Status { get; set; } = "";
}

public sealed class DpiDto
{
    public int Value { get; set; }
    public int Default { get; set; }
    public List<int> Choices { get; set; } = new();
    public int Min { get; set; }
    public int Max { get; set; }
    public int Step { get; set; }
}

public sealed class ReportRateDto { public int Ms { get; set; } public List<int> Choices { get; set; } = new(); }
public sealed class SmartShiftDto { public bool Ratchet { get; set; } public int Threshold { get; set; } public bool Enhanced { get; set; } }
public sealed class WheelDto { public bool HiRes { get; set; } public bool Inverted { get; set; } public bool Diverted { get; set; } public int Multiplier { get; set; } }
public sealed class HostDto { public int Count { get; set; } public int Current { get; set; } }
public sealed class OnboardDto { public bool Onboard { get; set; } public int ProfileCount { get; set; } public int Profile { get; set; } }
public sealed class FeatureDto { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public int Index { get; set; } }

public sealed class HidppDeviceDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "unknown";
    public string Wpid { get; set; } = "";
    public bool Online { get; set; }
    public string Protocol { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Status { get; set; } = "";
    public BatteryDto? Battery { get; set; }
    public DpiDto? Dpi { get; set; }
    public ReportRateDto? ReportRate { get; set; }
    public int? PointerSpeed { get; set; }
    public SmartShiftDto? SmartShift { get; set; }
    public WheelDto? Wheel { get; set; }
    public bool? FnSwap { get; set; }
    public HostDto? Hosts { get; set; }
    public OnboardDto? Onboard { get; set; }
    public List<FeatureDto> Features { get; set; } = new();
    public List<string> Unsupported { get; set; } = new();
}

public sealed class LogitechStateDto
{
    public bool Authorized { get; set; }
    public bool Connected { get; set; }
    public string Status { get; set; } = "";
    public bool IsReceiver { get; set; }
    public bool IsChild { get; set; }
    public string ProductName { get; set; } = "";
    public string ReceiverKind { get; set; } = "";
    public string ReceiverFirmware { get; set; } = "";
    public string Link { get; set; } = "";
    public List<HidppDeviceDto> Devices { get; set; } = new();
    public DateTimeOffset? LastReadAt { get; set; }
}

/// <summary>接收器页配对表的一行。</summary>
public sealed class PairedRow
{
    public string Glyph { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public double Opacity { get; set; } = 1;
}
