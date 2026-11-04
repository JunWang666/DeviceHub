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
    /// <summary>未自定义时的名称（插件描述 &gt; manifest 声明名 &gt; 产品串）；用户改过名时 DisplayName 是自定义名。</summary>
    public string? DefaultName { get; init; }
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
    /// <summary>接管插件指定的图标（已校验），没有则为空、由界面用默认图标。</summary>
    public string? Glyph { get; init; }
    /// <summary>当前连接状态。</summary>
    public bool Connected { get; init; }
    /// <summary>是否已有插件正在管理（供 UI 绑定）。</summary>
    public bool HasOwner => !string.IsNullOrEmpty(OwnerPluginId);
    /// <summary>是否存在可管理此设备的候选插件（无候选的设备不进“新设备”列表）。</summary>
    public bool HasCandidatePlugin { get; init; }
    /// <summary>
    /// 子设备所属的物理设备（如 Unifying 接收器上配对的鼠标）；物理设备为 null。
    /// 子设备由管理父设备的插件发布，没有自己的通道，I/O 仍经父设备进行，授权随父设备。
    /// </summary>
    public string? ParentKey { get; init; }
    public bool IsChild => ParentKey is not null;
}

/// <summary>插件发布的子设备：Id 在父设备内唯一且跨重启稳定（如配对设备的序列号）。</summary>
public sealed record ChildDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Connected { get; init; }
}

/// <summary>设备键约定。核心生成的物理设备键不含 '/'，子设备键 = 父设备键 + "/" + 子设备 Id。</summary>
public static class DeviceKeys
{
    public const char ChildSeparator = '/';
    public static string Child(string parentKey, string childId) => parentKey + ChildSeparator + childId;
    public static bool IsChild(string deviceKey) => deviceKey.Contains(ChildSeparator);

    /// <summary>拆出父设备键与子设备 Id；不是子设备键时返回 false。</summary>
    public static bool TrySplit(string deviceKey, out string parentKey, out string childId)
    {
        int i = deviceKey.IndexOf(ChildSeparator);
        parentKey = i < 0 ? deviceKey : deviceKey[..i];
        childId = i < 0 ? "" : deviceKey[(i + 1)..];
        return i >= 0;
    }
}

/// <summary>屏幕状态提示的预设种类：决定默认图标与强调色。</summary>
public enum OsdKind { Info, Connected, Disconnected, BatteryLow, Charging, Charged, Warning }

/// <summary>
/// 插件请求的屏幕状态提示（类似系统音量浮窗，显示在屏幕底部中间）。
/// 只能填模板里的字段：图标、一行文本、可选进度；设备名称由核心按用户看到的名称填写，插件无法隐藏或改写。
/// </summary>
public sealed record OsdContent
{
    public OsdKind Kind { get; init; } = OsdKind.Info;
    /// <summary>Segoe Fluent Icons 的字形（单个私用区字符，U+E000–U+F8FF）；为空或不合法时用 Kind 的默认图标。</summary>
    public string? Glyph { get; init; }
    /// <summary>状态文本，最多 <see cref="MaxTextLength"/> 个字符，超出截断。</summary>
    public required string Text { get; init; }
    /// <summary>0–100 时显示进度条（如电量），null 不显示。</summary>
    public int? Percent { get; init; }
    public const int MaxTextLength = 40;
}

/// <summary>核心校验并补全后推给 UI 的状态提示。</summary>
public sealed record OsdMessage
{
    public required string PluginId { get; init; }
    public required string DeviceKey { get; init; }
    /// <summary>核心填写的设备名（自定义名 &gt; 默认名），UI 必须显示。</summary>
    public required string DeviceName { get; init; }
    public required OsdKind Kind { get; init; }
    public required string Glyph { get; init; }
    public required string Text { get; init; }
    public int? Percent { get; init; }
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
    /// <summary>上一个版本（升级或安装前的版本），可回滚到它；没有为 null。</summary>
    public string? PreviousVersion { get; init; }
    public required PluginRunState State { get; init; }
    public int RestartCount { get; init; }
    /// <summary>插件进程的专用内存（不含与其他进程共享的运行时映像，接近任务管理器的"内存"列）。</summary>
    public long MemoryBytes { get; init; }
    public string? LastError { get; init; }
    /// <summary>此插件 UI 页面所在程序集相对路径（相对插件版本目录），供 App 进程内加载。</summary>
    public string? UiAssemblyPath { get; init; }
    /// <summary>UI 页面的入口类型全名（实现 IPluginPage）。</summary>
    public string? UiPageTypeName { get; init; }
    /// <summary>主页小组件的提供类型全名（实现 IPluginWidgetProvider）；未单独声明时用 <see cref="UiPageTypeName"/>。</summary>
    public string? UiWidgetTypeName { get; init; }
    /// <summary>插件在 manifest 里声明的主页小组件。</summary>
    public IReadOnlyList<WidgetInfo> Widgets { get; init; } = [];
}

/// <summary>主页小组件尺寸（网格单元：小 1×1，宽 2×1，大 2×2）。</summary>
public enum WidgetSize { Small, Wide, Large }

/// <summary>
/// 插件声明的一种主页小组件。小组件绑定到插件管理的某台设备（或子设备）：
/// 用户在主页上选"哪台设备的哪个小组件"，核心画卡片外框（设备名、图标），插件只提供内容。
/// </summary>
public sealed record WidgetInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    /// <summary>Segoe Fluent Icons 私用区字符；空则用设备图标。</summary>
    public string? Glyph { get; init; }
    /// <summary>支持的尺寸；空表示只有 <see cref="WidgetSize.Small"/>。</summary>
    public IReadOnlyList<WidgetSize> Sizes { get; init; } = [];
    public WidgetSize DefaultSize { get; init; } = WidgetSize.Small;
    /// <summary>适用的设备：any = 任意，physical = 只有物理设备（如接收器、直连设备），child = 只有子设备。</summary>
    public string Targets { get; init; } = "any";
}

/// <summary>安装插件包的结果。Previous 为安装前的版本（新插件为 null）。</summary>
public sealed record PluginInstallResult
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public string? Previous { get; init; }
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
    /// <summary>设备显示名：自定义名 &gt; 上次在线时的名称 &gt; 插件声明的名称。从未在线过的旧记录为空。</summary>
    public string DeviceDisplayName { get; init; } = "";
    /// <summary>未自定义时的名称（用于改名对话框的占位与"恢复默认"）。</summary>
    public string DefaultName { get; init; } = "";
    /// <summary>用户起的名字，没有为 null。</summary>
    public string? CustomName { get; init; }
    /// <summary>授权（接入）时间；旧记录可能没有。</summary>
    public DateTimeOffset? GrantedAt { get; init; }
    public bool DeviceConnected { get; init; }
    /// <summary>设备上次在线时插件给的图标，离线时侧边栏照样用它。</summary>
    public string? DeviceGlyph { get; init; }
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
    /// <summary>侧边栏和列表里的设备图标：Segoe Fluent Icons 的一个字符（如耳机 ""）。为空或不是该字体的私用区字符时用默认图标。</summary>
    public string? Glyph { get; init; }
    public IReadOnlyList<DeviceDetailItem> Details { get; init; } = Array.Empty<DeviceDetailItem>();
}

/// <summary>插件给的图标字符检查：只接受 Segoe Fluent Icons 所在的私用区（E000–F8FF）的单个字符。</summary>
public static class Glyphs
{
    public const string Device = "";
    public static bool IsValid(string? g) => g is { Length: 1 } && g[0] is >= '' and <= '';
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
