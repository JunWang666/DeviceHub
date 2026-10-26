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
        OnUi(() =>
        {
            Devices.Clear(); foreach (var d in devs) Devices.Add(d);
            ManagedDevices.Clear(); foreach (var d in devs.Where(d => d.HasOwner)) ManagedDevices.Add(d);
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
