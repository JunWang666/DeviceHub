using DeviceHub.Contracts;
using DeviceHub.Sdk;

namespace DeviceHub.PluginHost;

/// <summary>Agent → 本进程插件后端的调用适配。</summary>
public sealed class HostApiService : IHostApi
{
    private readonly IDevicePlugin _plugin;
    private readonly IPluginContext _ctx;
    private readonly CancellationToken _ct;
    private bool _initialized;

    public HostApiService(IDevicePlugin plugin, IPluginContext ctx, CancellationToken ct)
    { _plugin = plugin; _ctx = ctx; _ct = ct; }

    public async Task InitializeAsync(string pluginId, string pluginVersionDir)
    {
        if (_initialized) return;
        await _plugin.InitializeAsync(_ctx, _ct);
        _initialized = true;
    }

    public Task AttachDeviceAsync(DeviceInfo device) => _plugin.AttachAsync(device, _ct);
    public Task DetachDeviceAsync(string deviceKey) => _plugin.DetachAsync(deviceKey);
    public Task<string> InvokeAsync(string deviceKey, string method, string argsJson) => _plugin.InvokeAsync(deviceKey, method, argsJson, _ct);
    public Task<string> GetStateAsync(string deviceKey) => _plugin.GetStateAsync(deviceKey);
    public Task<DeviceDescription?> DescribeDeviceAsync(string deviceKey) => _plugin.DescribeAsync(deviceKey);
    public Task PrepareShutdownAsync() => _plugin.PrepareShutdownAsync();
    public Task<int> GetSchemaVersionAsync() => Task.FromResult(_plugin.SchemaVersion);
    public Task MigrateAsync(int fromSchema, int toSchema) => _plugin.MigrateAsync(fromSchema, toSchema);
}
