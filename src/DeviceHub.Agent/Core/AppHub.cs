using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceHub.Agent.Core;

/// <summary>保存当前连接的 UI 回调，向其转发事件。UI 未连接时事件被安全丢弃。</summary>
public sealed class AppHub
{
    private readonly ILogger<AppHub> _log;
    private volatile IAppCallback? _callback;

    public AppHub(ILogger<AppHub> log) => _log = log;

    public bool HasUi => _callback != null;
    public void SetCallback(IAppCallback cb) => _callback = cb;
    public void ClearCallback(IAppCallback cb) { if (ReferenceEquals(_callback, cb)) _callback = null; }

    public void PermissionRequested(PermissionRequest r) => Safe(c => c.OnPermissionRequested(r));
    public void DevicesChanged(IReadOnlyList<DeviceInfo> d) => Safe(c => c.OnDevicesChanged(d));
    public void PluginStateChanged(PluginInfo p) => Safe(c => c.OnPluginStateChanged(p));
    public void PluginStatePushed(string pluginId, string deviceKey, string json) =>
        Safe(c => c.OnPluginStatePushed(pluginId, deviceKey, json));
    public void UsbChanged() => Safe(c => c.OnUsbChanged());
    public void Osd(OsdMessage m) => Safe(c => c.OnOsd(m));

    private void Safe(Action<IAppCallback> act)
    {
        var cb = _callback;
        if (cb is null) return;
        try { act(cb); } catch (Exception ex) { _log.LogDebug(ex, "向 UI 推送失败（可能已断开）"); }
    }
}
