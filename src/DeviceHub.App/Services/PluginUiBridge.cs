using DeviceHub.Sdk.Ui;

namespace DeviceHub.App.Services;

/// <summary>把插件 UI 的请求路由到 Agent，并把 Agent 的推送回传给页面。</summary>
public sealed class PluginUiBridge : IPluginUiBridge, IDisposable
{
    private readonly AgentClient _client;
    public string PluginId { get; }
    public string DeviceKey { get; }
    public event Action<string>? StatePushed;

    public PluginUiBridge(AgentClient client, string pluginId, string deviceKey)
    {
        _client = client; PluginId = pluginId; DeviceKey = deviceKey;
        _client.PluginStatePushed += OnPushed;
    }

    private void OnPushed(string pluginId, string deviceKey, string json)
    {
        if (pluginId == PluginId && deviceKey == DeviceKey) StatePushed?.Invoke(json);
    }

    public Task<string> GetStateAsync() =>
        _client.Agent?.GetPluginStateAsync(PluginId, DeviceKey) ?? Task.FromResult("");

    public Task<string> InvokeAsync(string method, string argsJson) =>
        _client.Agent?.InvokePluginAsync(PluginId, DeviceKey, method, argsJson) ?? Task.FromResult("");

    public void Dispose() => _client.PluginStatePushed -= OnPushed;
}
