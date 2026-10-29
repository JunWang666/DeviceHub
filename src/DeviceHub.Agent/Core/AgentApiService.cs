using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceHub.Agent.Core;

/// <summary>App → Agent 的调用入口。</summary>
public sealed class AgentApiService : IAgentApi
{
    private readonly DeviceBroker _broker;
    private readonly PluginSupervisor _supervisor;
    private readonly PermissionService _perms;
    private readonly PluginManager _plugins;
    private readonly ILogger _log;

    public AgentApiService(DeviceBroker broker, PluginSupervisor supervisor, PermissionService perms,
        PluginManager plugins, ILogger log)
    { _broker = broker; _supervisor = supervisor; _perms = perms; _plugins = plugins; _log = log; }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync()
    {
        // 设备的“归属插件” = 已获 Always 授权的插件（即正在管理它的插件）。
        var granted = _perms.AllGrants()
            .Where(g => g.Scope == AccessScope.Always)
            .GroupBy(g => g.DeviceKey)
            .ToDictionary(g => g.Key, g => g.First());
        var plugins = _supervisor.List().ToDictionary(p => p.Id);
        var list = new List<DeviceInfo>();
        foreach (var d in _broker.Enumerate())
            list.Add(await DecorateAsync(d, granted.TryGetValue(d.DeviceKey, out var g) ? g : null, plugins));
        return list;
    }

    /// <summary>
    /// 补上显示信息：名称/副标题由插件决定（运行时描述 &gt; manifest 声明名 &gt; HID 产品串），
    /// 再加上接管插件名与接入时间。
    /// </summary>
    private async Task<DeviceInfo> DecorateAsync(DeviceInfo d,
        (string PluginId, string DeviceKey, AccessScope Scope, DateTimeOffset? GrantedAt)? grant,
        IReadOnlyDictionary<string, PluginInfo> plugins)
    {
        var owner = grant?.PluginId;
        var candidates = _plugins.MatchPlugins(d);
        var declared = (owner is null ? null : _plugins.MatchDevice(owner, d)?.Name)
                       ?? candidates.Select(id => _plugins.MatchDevice(id, d)?.Name).FirstOrDefault(n => n is not null);
        var desc = owner is null ? null : await _supervisor.DescribeDeviceAsync(owner, d.DeviceKey);
        var defaultName = desc?.Title ?? declared ?? d.DisplayName;
        string? custom = null;
        if (owner is not null)
        {
            _perms.RememberName(owner, d.DeviceKey, defaultName);
            custom = _perms.GetNames(owner, d.DeviceKey).CustomName;
        }
        return d with
        {
            OwnerPluginId = owner,
            OwnerPluginName = owner is null ? null : plugins.TryGetValue(owner, out var p) ? p.DisplayName : owner,
            GrantedAt = grant?.GrantedAt,
            HasCandidatePlugin = candidates.Count > 0,
            DisplayName = custom ?? defaultName,
            DefaultName = defaultName,
            Subtitle = desc?.Subtitle,
        };
    }

    public async Task<DeviceDetails?> GetDeviceDetailsAsync(string deviceKey)
    {
        var raw = _broker.Find(deviceKey);
        if (raw is null) return null;
        var grant = _perms.AllGrants().Where(g => g.DeviceKey == deviceKey && g.Scope == AccessScope.Always)
            .Select(g => ((string, string, AccessScope, DateTimeOffset?)?)g).FirstOrDefault();
        var plugins = _supervisor.List().ToDictionary(p => p.Id);
        var dev = await DecorateAsync(raw, grant, plugins);
        return new DeviceDetails
        {
            Device = dev,
            Grant = (await ListGrantsAsync()).FirstOrDefault(g => g.DeviceKey == deviceKey),
            Plugin = dev.OwnerPluginId is { } id && plugins.TryGetValue(id, out var p) ? p : null,
            Description = dev.OwnerPluginId is { } owner ? await _supervisor.DescribeDeviceAsync(owner, deviceKey) : null,
        };
    }

    public Task<IReadOnlyList<PluginInfo>> ListCandidatePluginsAsync(string deviceKey)
    {
        var dev = _broker.Find(deviceKey);
        if (dev is null) return Task.FromResult<IReadOnlyList<PluginInfo>>(Array.Empty<PluginInfo>());
        var ids = _plugins.MatchPlugins(dev).ToHashSet();
        IReadOnlyList<PluginInfo> list = _supervisor.List().Where(p => ids.Contains(p.Id)).ToList();
        return Task.FromResult(list);
    }

    public async Task AuthorizeAndUseAsync(string pluginId, string deviceKey)
    {
        _perms.Grant(pluginId, deviceKey, AccessScope.Always);
        await _supervisor.AttachDeviceToAsync(pluginId, deviceKey);
    }

    public Task<IReadOnlyList<PluginInfo>> ListPluginsAsync() => Task.FromResult(_supervisor.List());

    public Task RespondPermissionAsync(string requestId, AccessScope scope)
    { _perms.Respond(requestId, scope); return Task.CompletedTask; }

    public Task<IReadOnlyList<GrantInfo>> ListGrantsAsync()
    {
        var plugins = _supervisor.List().ToDictionary(p => p.Id);
        var devices = _broker.Enumerate().ToDictionary(d => d.DeviceKey);
        IReadOnlyList<GrantInfo> list = _perms.AllGrants()
            .Select(g =>
            {
                var dev = devices.GetValueOrDefault(g.DeviceKey);
                // 离线时用上次在线时记下的名字；在线时也优先用它（它来自插件描述，比 manifest 名更具体）
                var (last, custom) = _perms.GetNames(g.PluginId, g.DeviceKey);
                var live = dev is null ? null : _plugins.MatchDevice(g.PluginId, dev)?.Name ?? dev.DisplayName;
                var defaultName = last ?? live ?? _plugins.DeclaredName(g.PluginId, g.DeviceKey) ?? "";
                return new GrantInfo
                {
                    PluginId = g.PluginId,
                    DeviceKey = g.DeviceKey,
                    Scope = g.Scope,
                    GrantedAt = g.GrantedAt,
                    PluginDisplayName = plugins.TryGetValue(g.PluginId, out var p) ? p.DisplayName : g.PluginId,
                    DeviceDisplayName = custom ?? defaultName,
                    DefaultName = defaultName,
                    CustomName = custom,
                    DeviceConnected = dev is not null,
                };
            })
            .OrderByDescending(g => g.GrantedAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task RenameDeviceAsync(string pluginId, string deviceKey, string? name)
    { _perms.SetCustomName(pluginId, deviceKey, name); return Task.CompletedTask; }

    public Task RevokeGrantAsync(string pluginId, string deviceKey)
    { _perms.Revoke(pluginId, deviceKey); return Task.CompletedTask; }

    public Task StartPluginAsync(string pluginId) => _supervisor.StartAsync(pluginId);
    public Task StopPluginAsync(string pluginId) => _supervisor.StopAsync(pluginId);

    public Task<string> InvokePluginAsync(string pluginId, string deviceKey, string method, string argsJson)
        => _supervisor.InvokeAsync(pluginId, deviceKey, method, argsJson);

    public Task<string> GetPluginStateAsync(string pluginId, string deviceKey)
        => _supervisor.GetStateAsync(pluginId, deviceKey);

    public async Task<bool> StagePluginUpdateAsync(string pluginId, string packagePath)
    {
        // 更新前通知旧插件刷写状态并停止其进程（后端热切换），版本指针在重启后生效。
        await _supervisor.StopAsync(pluginId);
        return _plugins.StageUpdate(pluginId, packagePath);
    }

    /// <summary>
    /// UI 重启前调用：把待定版本切为 active 并重启对应插件后端（Agent 本身不重启）。
    /// 新版 UI 资源由 App 下次启动时从 active 版本同步到 exe 目录。
    /// </summary>
    public async Task RequestUiRestartAsync()
    {
        _log.LogInformation("收到 UI 重启请求（UI 自行重启，Agent 保持运行）");
        foreach (var id in _plugins.PluginIds().ToList())
        {
            if (!_plugins.ApplyPendingIfAny(id)) continue;
            await _supervisor.StopAsync(id);
            await _supervisor.StartAsync(id);
        }
    }

    public async Task<string> InstallPluginPackageAsync(string packagePath)
    {
        var id = PeekId(packagePath);
        if (id is not null) await _supervisor.StopAsync(id); // 已在运行的旧版先停，刷写状态
        var r = _plugins.InstallPackage(packagePath);
        if (r.Activated) await _supervisor.StartAsync(r.Id);  // 新插件立即启动后端；UI 需重启界面后加载
        return r.Id;
    }

    public Task<UsbTopology> GetUsbTopologyAsync() => Task.Run(Native.NativeUsb.Scan);

    private static string? PeekId(string packagePath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(packagePath);
            using var s = zip.GetEntry("manifest.json")?.Open();
            return s is null ? null : System.Text.Json.JsonDocument.Parse(s).RootElement.GetProperty("id").GetString();
        }
        catch { return null; }
    }
}
