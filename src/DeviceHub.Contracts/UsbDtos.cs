namespace DeviceHub.Contracts;

// USB 拓扑（核心功能，只读）：主控制器 → 根集线器 → 端口 → 设备 → （集线器的）端口 …
// 由 Agent 扫描时就地构建，所以这些类型是可写的类而不是 record。

/// <summary>一次 USB 拓扑扫描的结果。</summary>
public sealed class UsbTopology
{
    public DateTimeOffset ScannedAt { get; set; }
    public List<UsbControllerInfo> Controllers { get; set; } = new();
}

/// <summary>USB 主控制器（xHCI 等）。</summary>
public sealed class UsbControllerInfo
{
    public string Name { get; set; } = "";
    public string? InstanceId { get; set; }
    public UsbHubInfo? RootHub { get; set; }
    /// <summary>打不开控制器/根集线器时的原因。</summary>
    public string? Error { get; set; }
}

public sealed class UsbHubInfo
{
    /// <summary>集线器设备名（打开它用的符号链接，不带 \\.\ 前缀）。</summary>
    public string Name { get; set; } = "";
    public bool IsRoot { get; set; }
    /// <summary>USB 3 集线器（外置 USB 3 集线器在系统里是 USB 3 + USB 2 两个集线器）。</summary>
    public bool IsUsb3 { get; set; }
    public bool? IsBusPowered { get; set; }
    public List<UsbPortInfo> Ports { get; set; } = new();
}

/// <summary>端口连接状态（USB_CONNECTION_STATUS）。</summary>
public enum UsbConnectionStatus
{
    NoDevice = 0, Connected = 1, FailedEnumeration = 2, GeneralFailure = 3, Overcurrent = 4,
    NotEnoughPower = 5, NotEnoughBandwidth = 6, HubNestedTooDeeply = 7, InLegacyHub = 8,
    Enumerating = 9, Reset = 10,
}

public sealed class UsbPortInfo
{
    public int Number { get; set; }
    public bool SupportsUsb2 { get; set; }
    public bool SupportsUsb3 { get; set; }
    public bool IsTypeC { get; set; }
    /// <summary>用户可插拔的外部接口（不是内置摄像头/蓝牙之类的内部口）。</summary>
    public bool IsUserConnectable { get; set; }
    public bool IsDebugCapable { get; set; }
    /// <summary>同一物理接口的另一半（USB 3 口的 USB 2 伴随口，反之亦然）。</summary>
    public int? CompanionPort { get; set; }
    public string? CompanionHub { get; set; }
    /// <summary>这是另一个端口的 USB 2 伴随口，已并入那个端口显示。</summary>
    public bool MergedIntoCompanion { get; set; }
    public UsbConnectionStatus Status { get; set; }
    public UsbDeviceInfo? Device { get; set; }
}

public enum UsbSpeed { Unknown, Low, Full, High, Super, SuperPlus, SuperPlus20 }

/// <summary>Billboard bmConfigured 的 2 bit 状态。</summary>
public enum UsbAltModeState { Unspecified = 0, NotAttempted = 1, Failed = 2, Active = 3 }

/// <summary>Billboard 能力里的一个备用模式（DisplayPort、Thunderbolt 等）。</summary>
public sealed record UsbAltMode(ushort Svid, string Name, UsbAltModeState State);

public enum UsbIssueLevel { Info, Warning, Error }

/// <summary>诊断结论：例如"支持 USB 3 但以 USB 2.0 运行"。</summary>
public sealed record UsbIssue(UsbIssueLevel Level, string Title, string Detail);

public sealed class UsbDeviceInfo
{
    public string Name { get; set; } = "";
    public string? Manufacturer { get; set; }
    public string? Product { get; set; }
    public string? Serial { get; set; }
    public ushort Vid { get; set; }
    public ushort Pid { get; set; }
    public ushort BcdUsb { get; set; }
    public ushort BcdDevice { get; set; }
    public byte DeviceClass { get; set; }
    public int Address { get; set; }
    public bool IsHub { get; set; }

    public UsbSpeed Speed { get; set; }
    /// <summary>当前协商速率（Mbps）。</summary>
    public int SpeedMbps { get; set; }
    /// <summary>设备声明能达到的最高速率（Mbps，来自 BOS/设备限定符），未知为 0。</summary>
    public int CapableMbps { get; set; }
    /// <summary>SuperSpeed+ 的通道数（Gen 2x2 为 2），未知为 0。</summary>
    public int Lanes { get; set; }

    /// <summary>配置描述符里的最大取电（mA）。</summary>
    public int MaxPowerMa { get; set; }
    public bool SelfPowered { get; set; }
    public bool RemoteWakeup { get; set; }

    /// <summary>接口类别，如"HID"、"音频"、"大容量存储"。</summary>
    public List<string> InterfaceClasses { get; set; } = new();
    /// <summary>系统里该设备下的功能节点名称（如"HID Keyboard Device"）。</summary>
    public List<string> Functions { get; set; } = new();
    /// <summary>BOS 能力，如"USB 2.0 LPM"、"SuperSpeed 5 Gbps"。</summary>
    public List<string> Capabilities { get; set; } = new();
    public List<UsbAltMode> AltModes { get; set; } = new();

    public string? InstanceId { get; set; }
    public string? DriverDescription { get; set; }
    /// <summary>设备管理器的问题代码（0 = 正常）。</summary>
    public int ProblemCode { get; set; }

    public List<UsbIssue> Issues { get; set; } = new();

    /// <summary>集线器设备的下游端口。</summary>
    public UsbHubInfo? Hub { get; set; }
    /// <summary>USB 3 集线器的 USB 2 另一半（其端口里没能并入的设备会在这里）。</summary>
    public UsbHubInfo? Usb2Twin { get; set; }
}
