using System.Collections.Concurrent;
using DeviceHub.Agent.Native;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DeviceHub.Agent.Core;

/// <summary>
/// 设备代理：统一枚举设备、持有真实句柄、按授权做 I/O 代理。
/// 插件永远拿不到句柄；撤销授权会立即关闭句柄。
///
/// HID 设备按 PnP ContainerId 把同一物理设备的多个顶层集合聚合成一个 <see cref="DeviceInfo"/>
/// （例如 2.4G 接收器的键盘/多媒体/厂商配置集合），授权以物理设备为单位。
/// 插件 I/O 指定通道角色，由 manifest 声明解析到具体集合；未声明的集合插件无法访问。
/// 蓝牙设备（AirPods）由 <see cref="BluetoothService"/> 提供，走同一套授权与通道解析。
/// </summary>
public sealed class DeviceBroker : IDisposable
{
    private readonly ILogger<DeviceBroker> _log;
    private readonly PermissionService _perms;
    private readonly PluginManager _plugins;
    private readonly BluetoothService _bt;
    private readonly ConcurrentDictionary<string, SafeFileHandle> _openHandles = new(); // "deviceKey|channelId" → handle
    private readonly ConcurrentDictionary<string, NativeHid.HidPath> _channels = new(); // "deviceKey|channelId" → 集合
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();        // deviceKey → 最近一次枚举结果
    private readonly ConcurrentDictionary<string, (string PluginId, ChildDevice Child)> _children = new(); // 子设备键 → 发布者与内容

    public DeviceBroker(ILogger<DeviceBroker> log, PermissionService perms, PluginManager plugins, BluetoothService bt)
    {
        _log = log;
        _perms = perms;
        _plugins = plugins;
        _bt = bt;
        _perms.GrantRevoked += (_, deviceKey) => { CloseHandles(deviceKey); _bt.CloseAap(deviceKey); RemoveChildren(c => c.Key.StartsWith(deviceKey + DeviceKeys.ChildSeparator, StringComparison.Ordinal)); };
    }

    public IReadOnlyList<DeviceInfo> Enumerate()
    {
        var list = new List<DeviceInfo>();
        // 聚合键：优先 ContainerId；没有容器时退回序列号；都没有就是独立设备（按路径）。
        var groups = NativeHid.Enumerate().GroupBy(h =>
            (h.Vid, h.Pid, Group: h.ContainerId?.ToString("N") ?? (h.Serial is { Length: > 0 } s ? "sn:" + s : "path:" + h.Path)));
        foreach (var g in groups)
        {
            var first = g.First();
            // 设备 key 要跨重启/换口稳定：有序列号用序列号，否则用 ContainerId。
            var key = first.Serial is { Length: > 0 } sn ? $"hid:{first.Vid:x4}:{first.Pid:x4}:{sn}"
                    : first.ContainerId is { } cid ? $"hid:{first.Vid:x4}:{first.Pid:x4}:{cid:N}"
                    : $"hid:{first.Vid:x4}:{first.Pid:x4}:{Path.GetFileName(first.Path)}";

            var channels = new List<HidChannelInfo>();
            foreach (var h in g.OrderBy(h => h.Interface).ThenBy(h => h.UsagePage).ThenBy(h => h.Usage))
            {
                var baseId = $"mi{(h.Interface < 0 ? "xx" : h.Interface.ToString("x2"))}-{h.UsagePage:x4}-{h.Usage:x4}";
                var id = baseId;
                for (int n = 2; channels.Any(c => c.Id == id); n++) id = $"{baseId}#{n}";
                _channels[K(key, id)] = h;
                channels.Add(new HidChannelInfo
                {
                    Id = id, Interface = h.Interface, UsagePage = h.UsagePage, Usage = h.Usage,
                    InputLength = h.InputLength, OutputLength = h.OutputLength, FeatureLength = h.FeatureLength,
                });
            }

            var dev = new DeviceInfo
            {
                DeviceKey = key,
                Bus = DeviceBus.Hid,
                Vid = first.Vid,
                Pid = first.Pid,
                Serial = first.Serial,
                Channels = channels,
                DisplayName = g.Select(h => h.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? $"HID {first.Vid:X4}:{first.Pid:X4}",
                Connected = true,
            };
            _devices[key] = dev;
            list.Add(dev);
        }
        foreach (var dev in _bt.Devices())
        {
            _devices[dev.DeviceKey] = dev;
            list.Add(dev);
        }
        // 子设备跟在父设备后面；父设备不在线时不出现。
        var parents = new Dictionary<string, DeviceInfo>();
        foreach (var d in list) parents.TryAdd(d.DeviceKey, d);
        foreach (var (key, (_, child)) in _children.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            DeviceKeys.TrySplit(key, out var parentKey, out _);
            if (!parents.TryGetValue(parentKey, out var parent)) continue;
            list.Add(new DeviceInfo
            {
                DeviceKey = key,
                ParentKey = parentKey,
                Bus = parent.Bus,
                Vid = parent.Vid,
                Pid = parent.Pid,
                DisplayName = child.Name,
                Connected = child.Connected,
            });
        }
        return list;
    }

    public DeviceInfo? Find(string deviceKey) => Enumerate().FirstOrDefault(d => d.DeviceKey == deviceKey);

    // —— 子设备：由管理父设备的插件发布，只用于展示与路由，没有自己的通道 ——

    /// <summary>整体替换 parentKey 下由 pluginId 发布的子设备；有变化时返回 true。</summary>
    public bool SetChildren(string pluginId, string parentKey, IReadOnlyList<ChildDevice> children)
    {
        var prefix = parentKey + DeviceKeys.ChildSeparator;
        var next = children.Where(c => c.Id.Length > 0 && !c.Id.Contains(DeviceKeys.ChildSeparator))
            .GroupBy(c => c.Id).Select(g => g.First())
            .ToDictionary(c => prefix + c.Id, c => (pluginId, c));
        var old = _children.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        bool changed = old.Count != next.Count
            || old.Any(kv => !next.TryGetValue(kv.Key, out var n) || n != kv.Value);
        foreach (var kv in old.Where(kv => !next.ContainsKey(kv.Key))) _children.TryRemove(kv.Key, out _);
        foreach (var (k, v) in next) _children[k] = v;
        return changed;
    }

    /// <summary>插件停止/崩溃时清掉它发布的子设备。</summary>
    public bool RemoveChildren(string pluginId) => RemoveChildren(c => c.Value.PluginId == pluginId);

    private bool RemoveChildren(Func<KeyValuePair<string, (string PluginId, ChildDevice Child)>, bool> match)
    {
        bool any = false;
        foreach (var kv in _children.Where(match).ToList()) any |= _children.TryRemove(kv.Key, out _);
        return any;
    }

    // —— 授权后的 I/O 代理。channel = 插件 manifest 里声明的通道角色，null 为默认（第一个声明的）——
    public Task<DeviceIoResult> GetFeatureAsync(string pluginId, string deviceKey, string? channel, byte reportId, int length)
        => Guarded(pluginId, deviceKey, channel, (h, _) =>
        {
            var data = NativeHid.GetFeature(h, reportId, length);
            return data is null ? DeviceIoResult.Fail(DeviceIoStatus.Error, "GetFeature 失败")
                                : DeviceIoResult.Success(data);
        });

    public Task<DeviceIoResult> SetFeatureAsync(string pluginId, string deviceKey, string? channel, byte[] report)
        => Guarded(pluginId, deviceKey, channel, (h, _) =>
            NativeHid.SetFeature(h, report) ? DeviceIoResult.Success()
                                            : DeviceIoResult.Fail(DeviceIoStatus.Error, "SetFeature 失败"));

    /// <summary>写 Output 报告（WriteFile → 中断 OUT），按集合的 OutputReportByteLength 补零。aap 通道：发送一帧 AAP。</summary>
    public Task<DeviceIoResult> WriteOutputAsync(string pluginId, string deviceKey, string? channel, byte[] report)
        => IsBluetooth(deviceKey)
            ? GuardedBt(pluginId, deviceKey, channel, (c, spec) => Task.FromResult(c.Kind == ChannelKinds.Aap
                ? _bt.SendAap(deviceKey, spec.Psm, report)
                : DeviceIoResult.Fail(DeviceIoStatus.Error, "该通道只读")))
            : Guarded(pluginId, deviceKey, channel, (h, c) =>
        {
            if (c.OutputLength == 0) return DeviceIoResult.Fail(DeviceIoStatus.Error, "该通道没有 Output 报告");
            return NativeHid.WriteOutput(h, report, c.OutputLength)
                ? DeviceIoResult.Success()
                : DeviceIoResult.Fail(DeviceIoStatus.Error, "WriteOutput 失败");
        });

    /// <summary>读一个 Input 报告（ReadFile，带超时）。continuity / aap 通道：下一条广播 / 下一帧 AAP。</summary>
    public Task<DeviceIoResult> ReadInputAsync(string pluginId, string deviceKey, string? channel, int timeoutMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 1, 10_000);
        if (IsBluetooth(deviceKey))
            return GuardedBt(pluginId, deviceKey, channel, (c, spec) => c.Kind == ChannelKinds.Aap
                ? _bt.ReceiveAapAsync(deviceKey, spec.Psm, timeoutMs)
                : _bt.ReadAdvertAsync(deviceKey, timeoutMs));
        return Guarded(pluginId, deviceKey, channel, (h, c) =>
        {
            var data = NativeHid.ReadInput(h, c.InputLength, timeoutMs);
            return data is null ? DeviceIoResult.Fail(DeviceIoStatus.Timeout) : DeviceIoResult.Success(data);
        });
    }

    private static bool IsBluetooth(string deviceKey) => deviceKey.StartsWith("bt:", StringComparison.Ordinal);

    /// <summary>蓝牙设备的授权 + 通道解析；Feature 报告这类 HID 专有操作不会走到这里。</summary>
    private async Task<DeviceIoResult> GuardedBt(string pluginId, string deviceKey, string? channel,
        Func<HidChannelInfo, ChannelMatch, Task<DeviceIoResult>> io)
    {
        if (_perms.GetGrant(pluginId, deviceKey) is AccessScope.Denied)
            return DeviceIoResult.Fail(DeviceIoStatus.AccessDenied, "未授权");
        try
        {
            var dev = _bt.Devices().FirstOrDefault(d => d.DeviceKey == deviceKey);
            if (dev is null) return DeviceIoResult.Fail(DeviceIoStatus.DeviceGone);
            var (ch, spec) = _plugins.ResolveChannelSpec(pluginId, dev, channel);
            if (spec is null) return DeviceIoResult.Fail(DeviceIoStatus.AccessDenied, channel is null ? "插件未声明任何通道" : $"插件未声明通道 {channel}");
            if (ch is null) return DeviceIoResult.Fail(DeviceIoStatus.DeviceGone, spec.Kind == ChannelKinds.Aap ? "未安装 AAP 驱动" : "设备没有该通道");
            return await io(ch, spec);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "蓝牙 I/O 异常 {Device}", deviceKey);
            return DeviceIoResult.Fail(DeviceIoStatus.Error, ex.Message);
        }
    }

    private Task<DeviceIoResult> Guarded(string pluginId, string deviceKey, string? channel,
        Func<SafeFileHandle, NativeHid.HidPath, DeviceIoResult> io)
    {
        var scope = _perms.GetGrant(pluginId, deviceKey);
        if (scope is AccessScope.Denied)
            return Task.FromResult(DeviceIoResult.Fail(DeviceIoStatus.AccessDenied, "未授权"));
        try
        {
            if (!_devices.TryGetValue(deviceKey, out var dev)) { Enumerate(); _devices.TryGetValue(deviceKey, out dev); }
            if (dev is null) return Task.FromResult(DeviceIoResult.Fail(DeviceIoStatus.DeviceGone));
            var ch = _plugins.ResolveChannel(pluginId, dev, channel);
            if (ch is null)
                return Task.FromResult(DeviceIoResult.Fail(DeviceIoStatus.AccessDenied,
                    channel is null ? "插件未声明任何通道" : $"插件未声明通道 {channel}"));
            var h = Open(deviceKey, ch.Id);
            if (h is null || !_channels.TryGetValue(K(deviceKey, ch.Id), out var col))
                return Task.FromResult(DeviceIoResult.Fail(DeviceIoStatus.DeviceGone));
            return Task.FromResult(io(h, col));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "设备 I/O 异常 {Device}", deviceKey);
            return Task.FromResult(DeviceIoResult.Fail(DeviceIoStatus.Error, ex.Message));
        }
    }

    private static string K(string deviceKey, string channelId) => deviceKey + "|" + channelId;

    private SafeFileHandle? Open(string deviceKey, string channelId)
    {
        var k = K(deviceKey, channelId);
        if (_openHandles.TryGetValue(k, out var existing) && !existing.IsClosed) return existing;
        if (!_channels.TryGetValue(k, out var col)) return null;
        var h = NativeHid.Open(col.Path);
        if (h.IsInvalid) { _log.LogWarning("打开通道失败 {Device} {Channel}", deviceKey, channelId); return null; }
        _openHandles[k] = h;
        return h;
    }

    /// <summary>撤销授权：关闭该物理设备所有通道的句柄。</summary>
    private void CloseHandles(string deviceKey)
    {
        foreach (var k in _openHandles.Keys.Where(k => k.StartsWith(deviceKey + "|", StringComparison.Ordinal)).ToList())
            if (_openHandles.TryRemove(k, out var h)) { try { h.Dispose(); } catch { } _log.LogInformation("已关闭句柄 {Channel}", k); }
    }

    public void Dispose()
    {
        foreach (var h in _openHandles.Values) { try { h.Dispose(); } catch { } }
    }
}
