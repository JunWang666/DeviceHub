using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceHub.Agent.Core;

/// <summary>
/// PluginHost → Agent 的调用入口（每个插件连接一个实例）。
/// 授权校验、设备 I/O 代理、存储与推送都在这里汇聚。
/// </summary>
public sealed class HostToAgentService : IHostToAgentApi
{
    private readonly string _boundPluginId;
    private readonly DeviceBroker _broker;
    private readonly PermissionService _perms;
    private readonly StorageService _storage;
    private readonly AppHub _appHub;
    private readonly PluginManager _plugins;
    private readonly ILogger _log;

    public HostToAgentService(string pluginId, DeviceBroker broker, PermissionService perms,
        StorageService storage, AppHub appHub, PluginManager plugins, ILogger log)
    {
        _boundPluginId = pluginId;
        _broker = broker; _perms = perms; _storage = storage; _appHub = appHub; _plugins = plugins; _log = log;
    }

    private void Check(string pluginId)
    {
        if (pluginId != _boundPluginId)
            throw new UnauthorizedAccessException($"插件 Id 不匹配：声称 {pluginId}，实际 {_boundPluginId}");
    }

    public async Task<AccessScope> RequestDeviceAccessAsync(string pluginId, string deviceKey, string reason)
    {
        Check(pluginId);
        var device = _broker.Find(deviceKey);
        if (device is null) return AccessScope.Denied;
        var manifest = _plugins.ActiveManifest(pluginId);
        var info = new PluginInfo
        {
            Id = pluginId,
            DisplayName = manifest?.DisplayName ?? pluginId,
            Version = manifest?.Version ?? "?",
            State = PluginRunState.Running,
        };
        return await _perms.RequestAsync(info, device, reason);
    }

    public Task<DeviceIoResult> GetFeatureAsync(string pluginId, string deviceKey, string? channel, byte reportId, int length)
    { Check(pluginId); return _broker.GetFeatureAsync(pluginId, deviceKey, channel, reportId, length); }

    public Task<DeviceIoResult> SetFeatureAsync(string pluginId, string deviceKey, string? channel, byte[] report)
    { Check(pluginId); return _broker.SetFeatureAsync(pluginId, deviceKey, channel, report); }

    public Task<DeviceIoResult> WriteOutputAsync(string pluginId, string deviceKey, string? channel, byte[] report)
    { Check(pluginId); return _broker.WriteOutputAsync(pluginId, deviceKey, channel, report); }

    public Task<DeviceIoResult> ReadInputAsync(string pluginId, string deviceKey, string? channel, int timeoutMs)
    { Check(pluginId); return _broker.ReadInputAsync(pluginId, deviceKey, channel, timeoutMs); }

    public Task<string?> StorageGetAsync(string pluginId, string key)
    { Check(pluginId); return _storage.GetAsync(pluginId, key); }

    public Task StorageSetAsync(string pluginId, string key, string valueJson)
    { Check(pluginId); return _storage.SetAsync(pluginId, key, valueJson); }

    public Task PushStateAsync(string pluginId, string deviceKey, string stateJson)
    { Check(pluginId); _appHub.PluginStatePushed(pluginId, deviceKey, stateJson); return Task.CompletedTask; }

    public Task NotifyAsync(string pluginId, string title, string message)
    { Check(pluginId); _log.LogInformation("[通知:{Plugin}] {Title} — {Msg}", pluginId, title, message); return Task.CompletedTask; }
}
