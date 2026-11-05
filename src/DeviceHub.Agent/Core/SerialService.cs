using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading.Channels;
using DeviceHub.Agent.Native;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceHub.Agent.Core;

/// <summary>
/// USB 串口总线：把同一物理设备（按 PnP ContainerId 聚合）的多个 COM 口做成一台 <see cref="DeviceBus.Serial"/> 设备，
/// 每个口是一个 <see cref="ChannelKinds.Serial"/> 通道（Id 形如 "mi02-serial"，Interface = USB 接口号）。
/// 例：4G 模块的 DM / NMEA / AT 口。
/// <para>
/// 端口在插件第一次读写时打开，之后一直由核心持有（COM 口是独占的），后台线程把收到的字节排进队列；
/// 撤销授权时关闭，设备拔出后读写失败也会关闭，下次访问时重新打开。插件只拿到字节，拿不到端口。
/// </para>
/// </summary>
public sealed class SerialService : IDisposable
{
    private readonly ILogger<SerialService> _log;
    private readonly ConcurrentDictionary<string, NativeSerial.UsbInterfaceInfo> _ports = new(); // "deviceKey|channelId" → COM 口
    private readonly ConcurrentDictionary<string, Session> _sessions = new();                    // "deviceKey|channelId" → 已打开的端口

    public SerialService(ILogger<SerialService> log) => _log = log;

    public static bool IsSerialKey(string deviceKey) => deviceKey.StartsWith("ser:", StringComparison.Ordinal);

    /// <summary>
    /// 当前在线的 USB 串口设备：有 COM 口的，或者有接口没装上驱动的（这样插件能在驱动缺失时接管并提示安装）。
    /// 同一设备上的网卡接口作为只读的 net 通道一并给出（Detail = NetCfgInstanceId）。
    /// </summary>
    public IReadOnlyList<DeviceInfo> Devices()
    {
        var list = new List<DeviceInfo>();
        IReadOnlyList<NativeSerial.UsbInterfaceInfo> ifaces;
        try { ifaces = NativeSerial.Enumerate(); }
        catch (Exception ex) { _log.LogWarning(ex, "枚举 USB 串口失败"); return list; }

        // 没有容器 Id 时退回父设备实例（实例 Id 末段去掉最后一节，如 "7&4ADB4C9&0"）。
        foreach (var g in ifaces.GroupBy(p => (p.Vid, p.Pid, Group: p.ContainerId?.ToString("N") ?? ParentPart(p.InstanceId))))
        {
            // 只有网卡、没有串口也不缺驱动的设备（普通 USB 网卡）不归串口总线管。
            if (!g.Any(p => p.Kind is NativeSerial.InterfaceKind.Serial or NativeSerial.InterfaceKind.NoDriver)) continue;
            var key = $"ser:{g.Key.Vid:x4}:{g.Key.Pid:x4}:{g.Key.Group}";
            var channels = new List<HidChannelInfo>();
            foreach (var p in g.OrderBy(p => p.Interface).ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase))
            {
                var (suffix, kind) = p.Kind switch
                {
                    NativeSerial.InterfaceKind.Serial => ("serial", ChannelKinds.Serial),
                    NativeSerial.InterfaceKind.Net => ("net", ChannelKinds.Net),
                    _ => ("nodriver", ChannelKinds.NoDriver),
                };
                var baseId = $"mi{p.Interface:x2}-{suffix}";
                var id = baseId;
                for (int n = 2; channels.Any(c => c.Id == id); n++) id = $"{baseId}#{n}";
                bool io = p.Kind == NativeSerial.InterfaceKind.Serial;
                if (io) _ports[K(key, id)] = p;
                channels.Add(new HidChannelInfo
                {
                    Id = id, Kind = kind, Interface = p.Interface,
                    InputLength = io ? ChunkSize : 0, OutputLength = io ? ChunkSize : 0,
                    Detail = p.Kind switch
                    {
                        NativeSerial.InterfaceKind.Net => p.NetCfgInstanceId,
                        NativeSerial.InterfaceKind.NoDriver => NativeSerial.ProblemText(p.ProblemCode),
                        _ => p.PortName,
                    },
                });
            }
            list.Add(new DeviceInfo
            {
                DeviceKey = key,
                Bus = DeviceBus.Serial,
                Vid = g.Key.Vid,
                Pid = g.Key.Pid,
                DisplayName = g.Select(p => p.Product).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? $"USB 串口 {g.Key.Vid:X4}:{g.Key.Pid:X4}",
                Channels = channels,
                Connected = true,
            });
        }
        return list;
    }

    /// <summary>通道对应的端口名（如 COM5），供插件详情展示。</summary>
    public string? PortName(string deviceKey, string channelId) => _ports.TryGetValue(K(deviceKey, channelId), out var p) ? p.PortName : null;

    private static string ParentPart(string instanceId)
    {
        var tail = instanceId[(instanceId.LastIndexOf('\\') + 1)..];
        int amp = tail.LastIndexOf('&');
        return "inst:" + (amp > 0 ? tail[..amp] : tail).ToLowerInvariant();
    }

    // —— I/O ——

    private const int ChunkSize = 4096;

    private sealed class Session : IDisposable
    {
        public required SerialPort Port;
        public readonly Channel<byte[]> Inbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        public readonly CancellationTokenSource Stop = new();
        public volatile string? Error;
        public readonly object WriteLock = new();
        public void Dispose() { Stop.Cancel(); try { Port.Close(); } catch { } try { Port.Dispose(); } catch { } }
    }

    public async Task<DeviceIoResult> WriteAsync(string deviceKey, string channelId, byte[] data)
    {
        var (s, fail) = await OpenAsync(deviceKey, channelId);
        if (s is null) return fail!;
        try
        {
            lock (s.WriteLock) s.Port.BaseStream.Write(data, 0, data.Length);
            return DeviceIoResult.Success();
        }
        catch (Exception ex)
        {
            Drop(deviceKey, channelId, s, ex.Message);
            return DeviceIoResult.Fail(DeviceIoStatus.DeviceGone, "写串口失败：" + ex.Message);
        }
    }

    public async Task<DeviceIoResult> ReadAsync(string deviceKey, string channelId, int timeoutMs)
    {
        var (s, fail) = await OpenAsync(deviceKey, channelId);
        if (s is null) return fail!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(s.Stop.Token);
        cts.CancelAfter(timeoutMs);
        try { return DeviceIoResult.Success(await s.Inbound.Reader.ReadAsync(cts.Token)); }
        catch (OperationCanceledException) when (!s.Stop.IsCancellationRequested) { return DeviceIoResult.Fail(DeviceIoStatus.Timeout); }
        catch (Exception) { return DeviceIoResult.Fail(DeviceIoStatus.DeviceGone, s.Error ?? "端口已关闭"); }
    }

    private readonly ConcurrentDictionary<string, Task<(Session?, DeviceIoResult?)>> _opening = new();

    /// <summary>
    /// 取已打开的端口，没有就去打开。打开放在独立线程上：USB 串口驱动在上一个持有者异常退出后，
    /// 可能要二三十秒才让 Open 返回（实测 Quectel qcusbser 17–29 秒），不能让它占着 RPC 线程池拖住整个 Agent。
    /// 3 秒内没打开就先回 Busy，调用方稍后重试；打开完成后下一次调用直接拿到会话。
    /// </summary>
    private async Task<(Session?, DeviceIoResult?)> OpenAsync(string deviceKey, string channelId)
    {
        var k = K(deviceKey, channelId);
        if (_sessions.TryGetValue(k, out var s) && !s.Stop.IsCancellationRequested) return (s, null);
        if (!_ports.TryGetValue(k, out var info)) return (null, DeviceIoResult.Fail(DeviceIoStatus.DeviceGone));
        var task = _opening.GetOrAdd(k, key => Task.Factory.StartNew(() =>
        {
            try { return OpenNow(deviceKey, channelId, info); }
            finally { _opening.TryRemove(k, out _); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        try { return await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { return (null, DeviceIoResult.Fail(DeviceIoStatus.Busy, $"正在打开 {info.PortName}…")); }
    }

    private (Session?, DeviceIoResult?) OpenNow(string deviceKey, string channelId, NativeSerial.UsbInterfaceInfo info)
    {
        var k = K(deviceKey, channelId);
        if (_sessions.TryGetValue(k, out var s) && !s.Stop.IsCancellationRequested) return (s, null);
        var port = new SerialPort(info.PortName!, 115200, Parity.None, 8, StopBits.One)
        {
            DtrEnable = true, RtsEnable = true, Handshake = Handshake.None,
            ReadBufferSize = 64 * 1024, WriteTimeout = 2000,
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { port.Open(); }
        catch (UnauthorizedAccessException)
        {
            port.Dispose();
            return (null, DeviceIoResult.Fail(DeviceIoStatus.Busy, $"{info.PortName} 正被其他程序占用"));
        }
        catch (Exception ex)
        {
            port.Dispose();
            return (null, DeviceIoResult.Fail(DeviceIoStatus.DeviceGone, $"打开 {info.PortName} 失败：{ex.Message}"));
        }
        var session = new Session { Port = port };
        _sessions[k] = session;
        new Thread(() => ReadLoop(deviceKey, channelId, session)) { IsBackground = true, Name = "serial-rx " + info.PortName }.Start();
        _log.LogInformation("已打开串口 {Port}（{Device} {Channel}，用时 {Ms} ms）", info.PortName, deviceKey, channelId, sw.ElapsedMilliseconds);
        return (session, null);
    }

    private void ReadLoop(string deviceKey, string channelId, Session s)
    {
        var buf = new byte[ChunkSize];
        try
        {
            var stream = s.Port.BaseStream;
            while (!s.Stop.IsCancellationRequested)
            {
                int n = stream.ReadAsync(buf, 0, buf.Length, s.Stop.Token).GetAwaiter().GetResult();
                if (n <= 0) continue;
                s.Inbound.Writer.TryWrite(buf.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Drop(deviceKey, channelId, s, ex.Message); }
    }

    private void Drop(string deviceKey, string channelId, Session s, string error)
    {
        s.Error = error;
        if (_sessions.TryRemove(new KeyValuePair<string, Session>(K(deviceKey, channelId), s)))
            _log.LogInformation("串口已断开 {Device} {Channel}：{Error}", deviceKey, channelId, error);
        s.Inbound.Writer.TryComplete();
        s.Dispose();
    }

    /// <summary>撤销授权：关闭该设备所有已打开的串口。</summary>
    public void Close(string deviceKey)
    {
        foreach (var k in _sessions.Keys.Where(k => k.StartsWith(deviceKey + "|", StringComparison.Ordinal)).ToList())
            if (_sessions.TryRemove(k, out var s)) { s.Inbound.Writer.TryComplete(); s.Dispose(); _log.LogInformation("已关闭串口 {Channel}", k); }
    }

    private static string K(string deviceKey, string channelId) => deviceKey + "|" + channelId;

    public void Dispose()
    {
        foreach (var k in _sessions.Keys.ToList())
            if (_sessions.TryRemove(k, out var s)) s.Dispose();
    }
}
