using System.Text.Json;
using DeviceHub.Contracts;
using DeviceHub.Sdk;

namespace DeviceHub.PluginHost;

/// <summary>把 SDK 的上下文抽象桥接到 Agent（经 IHostToAgentApi 代理）。</summary>
public sealed class PluginContext : IPluginContext
{
    private readonly IHostToAgentApi _agent;
    public string PluginId { get; }
    public string VersionDir { get; }
    public IDeviceChannel Device { get; }
    public IPluginStorage Storage { get; }
    public INotifier Notify { get; }

    public PluginContext(string pluginId, string versionDir, IHostToAgentApi agent)
    {
        PluginId = pluginId; VersionDir = versionDir; _agent = agent;
        Device = new Channel(pluginId, agent);
        Storage = new Store(pluginId, agent);
        Notify = new Notifier(pluginId, agent);
    }

    public Task PushStateAsync(string deviceKey, string stateJson) => _agent.PushStateAsync(PluginId, deviceKey, stateJson);
    public Task PublishChildrenAsync(string parentKey, IReadOnlyList<ChildDevice> children) => _agent.PublishChildrenAsync(PluginId, parentKey, children);

    private sealed class Channel(string pid, IHostToAgentApi a) : IDeviceChannel
    {
        public Task<AccessScope> RequestAccessAsync(string deviceKey, string reason) => a.RequestDeviceAccessAsync(pid, deviceKey, reason);
        public Task<DeviceIoResult> GetFeatureAsync(string deviceKey, byte reportId, int length, string? channel = null) => a.GetFeatureAsync(pid, deviceKey, channel, reportId, length);
        public Task<DeviceIoResult> SetFeatureAsync(string deviceKey, byte[] report, string? channel = null) => a.SetFeatureAsync(pid, deviceKey, channel, report);
        public Task<DeviceIoResult> WriteOutputAsync(string deviceKey, byte[] report, string? channel = null) => a.WriteOutputAsync(pid, deviceKey, channel, report);
        public Task<DeviceIoResult> ReadInputAsync(string deviceKey, int timeoutMs, string? channel = null) => a.ReadInputAsync(pid, deviceKey, channel, timeoutMs);
    }

    private sealed class Store(string pid, IHostToAgentApi a) : IPluginStorage
    {
        public async Task<T?> GetAsync<T>(string key)
        {
            var json = await a.StorageGetAsync(pid, key);
            return json is null ? default : JsonSerializer.Deserialize<T>(json);
        }
        public Task SetAsync<T>(string key, T value) => a.StorageSetAsync(pid, key, JsonSerializer.Serialize(value));
    }

    private sealed class Notifier(string pid, IHostToAgentApi a) : INotifier
    {
        public Task ShowAsync(string title, string message) => a.NotifyAsync(pid, title, message);
        public Task ShowOsdAsync(string deviceKey, OsdContent content) => a.ShowOsdAsync(pid, deviceKey, content);
    }
}
