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
/// </summary>
public sealed class DeviceBroker : IDisposable
{
    private readonly ILogger<DeviceBroker> _log;
    private readonly PermissionService _perms;
    private readonly PluginManager _plugins;
    private readonly ConcurrentDictionary<string, SafeFileHandle> _openHandles = new(); // "deviceKey|channelId" → handle
    private readonly ConcurrentDictionary<string, NativeHid.HidPath> _channels = new(); // "deviceKey|channelId" → 集合
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();        // deviceKey → 最近一次枚举结果

    public DeviceBroker(ILogger<DeviceBroker> log, PermissionService perms, PluginManager plugins)
    {
        _log = log;
        _perms = perms;
        _plugins = plugins;
        _perms.GrantRevoked += (_, deviceKey) => CloseHandles(deviceKey);
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
        return list;
    }

    public DeviceInfo? Find(string deviceKey) => Enumerate().FirstOrDefault(d => d.DeviceKey == deviceKey);

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

    /// <summary>写 Output 报告（WriteFile → 中断 OUT），按集合的 OutputReportByteLength 补零。</summary>
    public Task<DeviceIoResult> WriteOutputAsync(string pluginId, string deviceKey, string? channel, byte[] report)
        => Guarded(pluginId, deviceKey, channel, (h, c) =>
        {
            if (c.OutputLength == 0) return DeviceIoResult.Fail(DeviceIoStatus.Error, "该通道没有 Output 报告");
            return NativeHid.WriteOutput(h, report, c.OutputLength)
                ? DeviceIoResult.Success()
                : DeviceIoResult.Fail(DeviceIoStatus.Error, "WriteOutput 失败");
        });

    /// <summary>读一个 Input 报告（ReadFile，带超时）。</summary>
    public Task<DeviceIoResult> ReadInputAsync(string pluginId, string deviceKey, string? channel, int timeoutMs)
        => Guarded(pluginId, deviceKey, channel, (h, c) =>
        {
            var data = NativeHid.ReadInput(h, c.InputLength, Math.Clamp(timeoutMs, 1, 10_000));
            return data is null ? DeviceIoResult.Fail(DeviceIoStatus.Timeout) : DeviceIoResult.Success(data);
        });

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
