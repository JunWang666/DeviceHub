using DeviceHub.Contracts;

namespace DeviceHub.App.Services;

/// <summary>界面上的时间/大小等格式化。</summary>
public static class Fmt
{
    public static string Time(DateTimeOffset? t) => t?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    public static string State(PluginRunState s) => s switch
    {
        PluginRunState.Running => "运行中",
        PluginRunState.Starting => "正在启动",
        PluginRunState.Stopped => "已停止",
        PluginRunState.Crashed => "已崩溃",
        PluginRunState.Disabled => "已禁用",
        _ => s.ToString(),
    };

    public static string Scope(AccessScope s) => s switch
    {
        AccessScope.Always => "始终允许",
        AccessScope.Once => "仅本次",
        _ => "拒绝",
    };

    public static string Bus(DeviceInfo d) => d.Bus switch
    {
        DeviceBus.Hid => $"USB HID · {d.Vid:X4}:{d.Pid:X4}",
        DeviceBus.Bluetooth => $"蓝牙 · {(d.Connected ? "已连接" : "未连接")}",
        _ => $"{d.Bus} · {d.Vid:X4}:{d.Pid:X4}",
    };

    /// <summary>"插件 XXX · 接入于 XXX"，可带插件给的副标题。</summary>
    public static string Managed(string? subtitle, string? plugin, DateTimeOffset? grantedAt)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(subtitle)) parts.Add(subtitle!);
        if (!string.IsNullOrWhiteSpace(plugin)) parts.Add($"插件 {plugin}");
        parts.Add($"接入于 {Time(grantedAt)}");
        return string.Join(" · ", parts);
    }
}

/// <summary>"新设备"列表项。</summary>
public sealed class NewDeviceRow
{
    public required DeviceInfo Device { get; init; }
    public string Title => Device.DisplayName;
    public string Description => Device.Channels.Count > 0 && Device.Bus == DeviceBus.Hid
        ? $"{Fmt.Bus(Device)} · {Device.Channels.Count} 个 HID 通道"
        : Fmt.Bus(Device);
}

/// <summary>"已授权的设备"列表项。</summary>
public sealed class GrantRow
{
    public required GrantInfo Grant { get; init; }
    public string Title => string.IsNullOrEmpty(Grant.DeviceDisplayName) ? "未知设备" : Grant.DeviceDisplayName;
    public string Description => Fmt.Managed(Grant.DeviceConnected ? null : "未连接", Grant.PluginDisplayName, Grant.GrantedAt);
}

/// <summary>"插件"列表项。</summary>
public sealed class PluginRow
{
    public required PluginInfo Plugin { get; init; }
    public string Title => Plugin.DisplayName;
    public string Description
    {
        get
        {
            var s = $"版本 {Plugin.Version} · {Fmt.State(Plugin.State)} · 内存 {Fmt.Size(Plugin.WorkingSetBytes)}";
            if (Plugin.RestartCount > 0) s += $" · 已重启 {Plugin.RestartCount} 次";
            if (!string.IsNullOrEmpty(Plugin.LastError)) s += $"\n最近错误：{Plugin.LastError}";
            return s;
        }
    }
    public bool CanStart => Plugin.State is PluginRunState.Stopped or PluginRunState.Crashed;
    public bool CanStop => Plugin.State is PluginRunState.Running or PluginRunState.Starting;
}
