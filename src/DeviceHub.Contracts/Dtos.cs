namespace DeviceHub.Contracts;

/// <summary>总线类型。Bluetooth = 已配对的经典蓝牙设备（如 AirPods），Vid/Pid 取自它的 Device ID 记录。</summary>
public enum DeviceBus { Hid, Usb, Ble, Serial, Bluetooth }

/// <summary>授权范围。</summary>
public enum AccessScope { Denied = 0, Once = 1, Always = 2 }

/// <summary>插件运行状态（供 UI 的资源面板显示）。</summary>
public enum PluginRunState { Stopped, Starting, Running, Crashed, Disabled }

/// <summary>
/// 物理设备下的一个通道。HID 设备的通道是顶层集合：复合设备/接收器会暴露多个（键盘输入、多媒体键、厂商配置通道等）。
/// 蓝牙设备的通道由核心提供：continuity（BLE 广播里该设备的 Apple Continuity 状态包，只读）、
/// aap（经第三方 L2CAP 驱动的 AAP 控制通道，驱动未安装时不存在）。
/// 插件通过 manifest 声明需要哪些通道，I/O 时按声明的角色名访问，拿不到未声明的通道。
/// </summary>
public sealed record HidChannelInfo
{
    /// <summary>通道种类，见 <see cref="ChannelKinds"/>。</summary>
    public string Kind { get; init; } = ChannelKinds.Hid;
    /// <summary>设备内唯一的通道标识，如 "mi02-ff01-0001"。</summary>
    public required string Id { get; init; }
    /// <summary>USB 接口号（mi_XX），非 USB 或未知为 -1。</summary>
    public int Interface { get; init; } = -1;
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    /// <summary>报告长度均含 report id 字节；0 表示该方向不存在。</summary>
    public int InputLength { get; init; }
    public int OutputLength { get; init; }
    public int FeatureLength { get; init; }
}

/// <summary>核心枚举到的物理设备。DeviceKey 在同一物理设备上保持稳定；HID 设备的多个集合聚合在 Channels 里。</summary>
public sealed record DeviceInfo
{
    public required string DeviceKey { get; init; }
    public required DeviceBus Bus { get; init; }
    public ushort Vid { get; init; }
    public ushort Pid { get; init; }
    public string? Serial { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>同一物理设备的全部通道：HID 集合（按 ContainerId 聚合），或核心为蓝牙设备提供的通道。</summary>
    public IReadOnlyList<HidChannelInfo> Channels { get; init; } = Array.Empty<HidChannelInfo>();
    /// <summary>供 UI 显示：如 "6 个 HID 通道：mi00-0001-0006, …"。</summary>
    public string ChannelSummary => Channels.Count == 0 ? "" : $"{Channels.Count} 个{(Bus == DeviceBus.Hid ? " HID " : "")}通道：{string.Join(", ", Channels.Select(c => c.Id))}";
    /// <summary>已匹配并接管此设备的插件 Id（空表示暂无插件接管）。</summary>
    public string? OwnerPluginId { get; init; }
    /// <summary>接管插件的显示名（供列表/页头显示"插件 XXX"）。</summary>
    public string? OwnerPluginName { get; init; }
    /// <summary>授权接入时间。</summary>
    public DateTimeOffset? GrantedAt { get; init; }
    /// <summary>接管插件给出的副标题（如"2.4G 接收器 · 面板灯效"），没有则为空。</summary>
    public string? Subtitle { get; init; }
    /// <summary>当前连接状态。</summary>
    public bool Connected { get; init; }
    /// <summary>是否已有插件正在管理（供 UI 绑定）。</summary>
    public bool HasOwner => !string.IsNullOrEmpty(OwnerPluginId);
    /// <summary>是否存在可管理此设备的候选插件（无候选的设备不进“新设备”列表）。</summary>
    public bool HasCandidatePlugin { get; init; }
}

/// <summary><see cref="HidChannelInfo.Kind"/> 的取值。</summary>
public static class ChannelKinds
{
    public const string Hid = "hid";
    /// <summary>BLE 广播中的 Apple 接近配对包（只读）。</summary>
    public const string Continuity = "continuity";
    /// <summary>AirPods 的 AAP 控制通道（L2CAP PSM 0x1001，需第三方驱动）。</summary>
    public const string Aap = "aap";
}

/// <summary>插件清单与当前运行信息。</summary>
public sealed record PluginInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required PluginRunState State { get; init; }
    public int RestartCount { get; init; }
    public long WorkingSetBytes { get; init; }
    public string? LastError { get; init; }
    /// <summary>此插件 UI 页面所在程序集相对路径（相对插件版本目录），供 App 进程内加载。</summary>
    public string? UiAssemblyPath { get; init; }
    /// <summary>UI 页面的入口类型全名（实现 IPluginPage）。</summary>
    public string? UiPageTypeName { get; init; }
}

/// <summary>授权请求（Agent → App，触发弹窗）。</summary>
public sealed record PermissionRequest
{
    public required string RequestId { get; init; }
    public required string PluginId { get; init; }
    public required string PluginDisplayName { get; init; }
    public required DeviceInfo Device { get; init; }
    /// <summary>申请的接口/用途描述，展示给用户。</summary>
    public required string Reason { get; init; }
}

/// <summary>一条授权记录（供隐私页展示）。</summary>
public sealed record GrantInfo
{
    public required string PluginId { get; init; }
    public required string DeviceKey { get; init; }
    public required AccessScope Scope { get; init; }
    /// <summary>插件 manifest 中的显示名。</summary>
    public string PluginDisplayName { get; init; } = "";
    /// <summary>设备显示名（插件声明的名称优先）。设备不在线时为空。</summary>
    public string DeviceDisplayName { get; init; } = "";
    /// <summary>授权（接入）时间；旧记录可能没有。</summary>
    public DateTimeOffset? GrantedAt { get; init; }
    public bool DeviceConnected { get; init; }
}

/// <summary>插件提供的一条设备信息（如"连接方式：2.4G 接收器"）。</summary>
public sealed record DeviceDetailItem(string Name, string Value);

/// <summary>
/// 插件对其管理设备的描述：列表/页头显示什么、详情里展示哪些信息，都由插件决定。
/// 字段为空时 App 回退到核心信息（manifest 名称、产品串等）。
/// </summary>
public sealed record DeviceDescription
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public IReadOnlyList<DeviceDetailItem> Details { get; init; } = Array.Empty<DeviceDetailItem>();
}

/// <summary>设备详情弹窗所需的全部信息（核心信息 + 插件描述）。</summary>
public sealed record DeviceDetails
{
    public required DeviceInfo Device { get; init; }
    public GrantInfo? Grant { get; init; }
    public PluginInfo? Plugin { get; init; }
    public DeviceDescription? Description { get; init; }
}

/// <summary>设备 I/O 错误码。</summary>
public enum DeviceIoStatus { Ok, AccessDenied, AccessRevoked, DeviceGone, Busy, Error, Timeout }

/// <summary>设备 I/O 结果。</summary>
public sealed record DeviceIoResult
{
    public required DeviceIoStatus Status { get; init; }
    public byte[]? Data { get; init; }
    public string? Message { get; init; }
    /// <summary>无线通道收到该包时的信号强度（dBm），其他通道为 null。</summary>
    public short? Rssi { get; init; }
    public static DeviceIoResult Success(byte[]? data = null) => new() { Status = DeviceIoStatus.Ok, Data = data };
    public static DeviceIoResult Fail(DeviceIoStatus s, string? m = null) => new() { Status = s, Message = m };
}
