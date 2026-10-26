using DeviceHub.Contracts;

namespace DeviceHub.Sdk;

/// <summary>
/// 设备插件后端接口。实现类运行在独立的 PluginHost 进程中。
/// 插件只负责设备逻辑，拿不到设备句柄，所有 I/O 经 <see cref="IPluginContext.Device"/> 代理。
/// </summary>
public interface IDevicePlugin
{
    /// <summary>插件数据的 Schema 版本，用于迁移。</summary>
    int SchemaVersion { get; }

    Task InitializeAsync(IPluginContext ctx, CancellationToken ct);

    /// <summary>核心检测到匹配设备并通知接管。插件在此申请访问并读取初始状态。</summary>
    Task AttachAsync(DeviceInfo device, CancellationToken ct);

    /// <summary>设备移除。</summary>
    Task DetachAsync(string deviceKey);

    /// <summary>处理来自 UI 的调用（例如 SetDpi、SetLighting）。返回最新状态 JSON。</summary>
    Task<string> InvokeAsync(string deviceKey, string method, string argsJson, CancellationToken ct);

    /// <summary>返回某设备的当前状态 JSON（供 UI 首次绑定）。</summary>
    Task<string> GetStateAsync(string deviceKey);

    /// <summary>
    /// 描述插件管理的设备：页头副标题、详情弹窗里展示哪些信息由插件决定。
    /// 默认不提供，App 只显示核心信息。
    /// </summary>
    Task<DeviceDescription?> DescribeAsync(string deviceKey) => Task.FromResult<DeviceDescription?>(null);

    /// <summary>更新前刷写设备状态、准备退出。</summary>
    Task PrepareShutdownAsync();

    /// <summary>数据迁移。</summary>
    Task MigrateAsync(int fromSchema, int toSchema) => Task.CompletedTask;
}

/// <summary>插件运行上下文，由核心注入。</summary>
public interface IPluginContext
{
    string PluginId { get; }
    string VersionDir { get; }
    IDeviceChannel Device { get; }
    IPluginStorage Storage { get; }
    INotifier Notify { get; }
    /// <summary>向 UI 推送状态变更。</summary>
    Task PushStateAsync(string deviceKey, string stateJson);
}

/// <summary>
/// 设备 I/O 抽象。调用会经核心做授权校验。
/// channel 为插件 plugin.json 中声明的通道角色（如 "config"），省略时使用声明的第一个通道；
/// 未声明的通道一律拒绝（AccessDenied）。
/// </summary>
public interface IDeviceChannel
{
    /// <summary>申请访问设备；未授权会触发用户弹窗。</summary>
    Task<AccessScope> RequestAccessAsync(string deviceKey, string reason);
    Task<DeviceIoResult> GetFeatureAsync(string deviceKey, byte reportId, int length, string? channel = null);
    Task<DeviceIoResult> SetFeatureAsync(string deviceKey, byte[] report, string? channel = null);
    /// <summary>
    /// 写 Output 报告（中断 OUT）。report[0] 为 report id，无 id 的集合填 0；不足长度由核心补零。
    /// aap 通道：原样发送一帧 AAP 数据。
    /// </summary>
    Task<DeviceIoResult> WriteOutputAsync(string deviceKey, byte[] report, string? channel = null);
    /// <summary>
    /// 读一个 Input 报告；timeoutMs 内无数据返回 Timeout。
    /// continuity 通道：下一条与该设备型号相符的 Apple 接近配对广播（27 字节，加密段已清零，带 Rssi）；设备未连接时返回 DeviceGone。
    /// aap 通道：下一帧 AirPods 推送的 AAP 数据。
    /// </summary>
    Task<DeviceIoResult> ReadInputAsync(string deviceKey, int timeoutMs, string? channel = null);
}

/// <summary>按 pluginId 隔离的存储。</summary>
public interface IPluginStorage
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value);
}

public interface INotifier
{
    Task ShowAsync(string title, string message);
}
