using System.IO.Pipes;
using DeviceHub.Contracts;
using StreamJsonRpc;

namespace DeviceHub.App.Services;

/// <summary>连接 Agent 的客户端，同时作为 IAppCallback 接收推送。</summary>
public sealed class AgentClient : IAppCallback, IDisposable
{
    private JsonRpc? _rpc;
    public IAgentApi? Agent { get; private set; }
    public bool Connected => Agent != null;

    public event Action<PermissionRequest>? PermissionRequested;
    public event Action<IReadOnlyList<DeviceInfo>>? DevicesChanged;
    public event Action<PluginInfo>? PluginStateChanged;
    public event Action<string, string, string>? PluginStatePushed; // pluginId, deviceKey, json
    public event Action? UsbChanged;
    public event Action<OsdMessage>? Osd;
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", "devicehub-agent", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, ct);
            var formatter = new SystemTextJsonFormatter();
            var handler = new HeaderDelimitedMessageHandler(pipe, pipe, formatter);
            _rpc = new JsonRpc(handler);
            Agent = _rpc.Attach<IAgentApi>();
            _rpc.AddLocalRpcTarget<IAppCallback>(this, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
            _rpc.Disconnected += (_, _) => { Agent = null; Disconnected?.Invoke(); };
            _rpc.StartListening();
            return true;
        }
        catch { Agent = null; return false; }
    }

    // —— IAppCallback（由 Agent 调用）——
    public void OnPermissionRequested(PermissionRequest request) => PermissionRequested?.Invoke(request);
    public void OnDevicesChanged(IReadOnlyList<DeviceInfo> devices) => DevicesChanged?.Invoke(devices);
    public void OnPluginStateChanged(PluginInfo plugin) => PluginStateChanged?.Invoke(plugin);
    public void OnPluginStatePushed(string pluginId, string deviceKey, string stateJson)
        => PluginStatePushed?.Invoke(pluginId, deviceKey, stateJson);
    public void OnUsbChanged() => UsbChanged?.Invoke();
    public void OnOsd(OsdMessage message) => Osd?.Invoke(message);

    public void Dispose() => _rpc?.Dispose();
}
