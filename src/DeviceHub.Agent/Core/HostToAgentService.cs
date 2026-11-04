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

    /// <summary>
    /// 只允许给自己管理的设备弹提示；设备名由核心按用户看到的名称填写（自定义名 &gt; 上次显示名 &gt; 核心名称），
    /// 文本截断、图标限定在 Segoe Fluent Icons 私用区，插件只能在模板内发挥。
    /// </summary>
    public Task ShowOsdAsync(string pluginId, string deviceKey, OsdContent content)
    {
        Check(pluginId);
        if (_perms.GetGrant(pluginId, deviceKey) != AccessScope.Always) return Task.CompletedTask;
        var dev = _broker.Find(deviceKey);
        var (last, custom) = _perms.GetNames(pluginId, deviceKey);
        var name = custom ?? last ?? dev?.DisplayName ?? _plugins.DeclaredName(pluginId, deviceKey) ?? "未知设备";
        var text = (content.Text ?? "").ReplaceLineEndings(" ").Trim();
        if (text.Length > OsdContent.MaxTextLength) text = text[..(OsdContent.MaxTextLength - 1)] + "…";
        int? pct = content.Percent is { } p ? Math.Clamp(p, 0, 100) : null;
        var msg = new OsdMessage
        {
            PluginId = pluginId,
            DeviceKey = deviceKey,
            DeviceName = name,
            Kind = content.Kind,
            Glyph = ValidGlyph(content.Glyph) ?? DefaultGlyph(content.Kind, pct),
            Text = text,
            Percent = pct,
        };
        _log.LogInformation("[OSD:{Plugin}] {Device}: {Text}", pluginId, name, text);
        _appHub.Osd(msg);
        return Task.CompletedTask;
    }

    private static string? ValidGlyph(string? g) =>
        g is { Length: 1 } && g[0] is >= '' and <= '' ? g : null;

    /// <summary>Segoe Fluent Icons：Battery0–10 = E850–E85A，BatteryCharging0–10 = E85B–E865。</summary>
    private static string DefaultGlyph(OsdKind kind, int? pct) => kind switch
    {
        OsdKind.Connected => "",
        OsdKind.Disconnected => "",
        OsdKind.BatteryLow => char.ConvertFromUtf32(0xE850 + Math.Clamp((pct ?? 10) / 10, 0, 10)),
        OsdKind.Charging => char.ConvertFromUtf32(0xE85B + Math.Clamp((pct ?? 50) / 10, 0, 10)),
        OsdKind.Charged => "",
        OsdKind.Warning => "",
        _ => "",
    };

    public Task PublishChildrenAsync(string pluginId, string parentKey, IReadOnlyList<ChildDevice> children)
    {
        Check(pluginId);
        if (DeviceKeys.IsChild(parentKey) || _perms.GetGrant(pluginId, parentKey) != AccessScope.Always)
        {
            _log.LogWarning("拒绝发布子设备：{Plugin} 未获 {Parent} 的授权", pluginId, parentKey);
            return Task.CompletedTask;
        }
        foreach (var c in children) _perms.InheritGrant(pluginId, parentKey, DeviceKeys.Child(parentKey, c.Id));
        if (_broker.SetChildren(pluginId, parentKey, children))
        {
            _log.LogInformation("子设备更新 {Parent}: {Children}", parentKey, string.Join(", ", children.Select(c => $"{c.Id}={c.Name}{(c.Connected ? "" : "(离线)")}")));
            _appHub.DevicesChanged(_broker.Enumerate());
        }
        return Task.CompletedTask;
    }

    public Task ForgetChildAsync(string pluginId, string parentKey, string childId)
    {
        Check(pluginId);
        if (DeviceKeys.IsChild(parentKey) || _perms.GetGrant(pluginId, parentKey) != AccessScope.Always) return Task.CompletedTask;
        var key = DeviceKeys.Child(parentKey, childId);
        _perms.Revoke(pluginId, key);
        _broker.RemoveChild(key);
        _log.LogInformation("移除子设备 {Child}", key);
        _appHub.DevicesChanged(_broker.Enumerate());
        return Task.CompletedTask;
    }
}
