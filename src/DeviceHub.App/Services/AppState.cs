using System.Collections.ObjectModel;
using DeviceHub.Contracts;
using Microsoft.UI.Dispatching;

namespace DeviceHub.App.Services;

/// <summary>进程内共享状态：Agent 连接 + 可观察集合。</summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    public AgentClient Client { get; } = new();
    public DispatcherQueue? Dispatcher { get; set; }

    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public ObservableCollection<DeviceInfo> ManagedDevices { get; } = new();   // 已授权、正在管理
    public ObservableCollection<DeviceInfo> NewDevices { get; } = new();        // 待授权
    public ObservableCollection<PluginInfo> Plugins { get; } = new();

    public event Action? DevicesRefreshed;
    public event Action<PermissionRequest>? PermissionRequested;

    private AppState()
    {
        Client.PluginStateChanged += p => OnUi(() => Upsert(Plugins, p, x => x.Id == p.Id));
        Client.PermissionRequested += r => OnUi(() => PermissionRequested?.Invoke(r));
        Client.Osd += m => OnUi(() => OsdWindow.Show(m));
        // 推送里是未经插件修饰的原始列表，重新拉一次带名称/归属的完整列表（如 AirPods 连上/断开）。
        Client.DevicesChanged += _ => { var t = RefreshAsync(); };
    }

    public async Task InitializeAsync()
    {
        if (!await Client.ConnectAsync()) return;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (Client.Agent is null) return;
        var devs = await Client.Agent.ListDevicesAsync();
        var plugs = await Client.Agent.ListPluginsAsync();
        IReadOnlyList<GrantInfo> grants;
        try { grants = await Client.Agent.ListGrantsAsync(); }
        catch { grants = Array.Empty<GrantInfo>(); } // 拿不到授权记录时只是少了离线占位，在线设备照常显示
        // 已授权但当前不在线的设备（蓝牙断开、接收器拔掉）也留在侧边栏，用授权记录补一个离线占位。
        var online = devs.Where(d => d.HasOwner).ToList();
        var offline = grants
            .Where(g => g.Scope == AccessScope.Always && online.All(d => d.DeviceKey != g.DeviceKey))
            .OrderBy(g => g.GrantedAt)
            .Select(g => new DeviceInfo
            {
                DeviceKey = g.DeviceKey,
                Bus = g.DeviceKey.StartsWith("bt:", StringComparison.Ordinal) ? DeviceBus.Bluetooth : DeviceBus.Hid,
                DisplayName = string.IsNullOrEmpty(g.DeviceDisplayName) ? "未知设备" : g.DeviceDisplayName,
                DefaultName = g.DefaultName,
                OwnerPluginId = g.PluginId,
                OwnerPluginName = g.PluginDisplayName,
                GrantedAt = g.GrantedAt,
                Subtitle = "未连接",
                Glyph = g.DeviceGlyph,
                Connected = false,
                ParentKey = DeviceKeys.TrySplit(g.DeviceKey, out var parent, out _) ? parent : null,
            });
        OnUi(() =>
        {
            Devices.Clear(); foreach (var d in devs) Devices.Add(d);
            ManagedDevices.Clear(); foreach (var d in online.Concat(offline)) ManagedDevices.Add(d);
            NewDevices.Clear(); foreach (var d in devs.Where(d => !d.HasOwner && d.HasCandidatePlugin)) NewDevices.Add(d);
            Plugins.Clear(); foreach (var p in plugs) Plugins.Add(p);
            DevicesRefreshed?.Invoke();
        });
    }

    public async Task AuthorizeAndUseAsync(string pluginId, string deviceKey)
    {
        if (Client.Agent is null) return;
        await Client.Agent.AuthorizeAndUseAsync(pluginId, deviceKey);
        await RefreshAsync();
    }

    private static void Upsert<T>(ObservableCollection<T> col, T item, Func<T, bool> match)
    {
        for (int i = 0; i < col.Count; i++) if (match(col[i])) { col[i] = item; return; }
        col.Add(item);
    }

    public void OnUi(Action action)
    {
        if (Dispatcher is { } d && !d.HasThreadAccess) d.TryEnqueue(() => action());
        else action();
    }
}
