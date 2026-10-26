using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using DeviceHub.Agent.Native;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace DeviceHub.Agent.Core;

/// <summary>
/// 蓝牙总线：已配对的 Apple 经典蓝牙设备（AirPods/Beats）+ 它们的两个数据来源。
/// <list type="bullet">
/// <item><b>continuity</b>：AirPods 在 BLE 广播里发的接近配对包（Apple 公司 Id 0x004C，类型 0x07），
/// 含左右耳/充电盒电量、充电、入耳、开盖。广播用随机地址，无法确定来自哪副耳机，
/// 所以只把“型号相符 + 该设备当前已连接”的广播交给插件（同 PodBridge 的连接门控），并清零末尾 16 字节加密段。</item>
/// <item><b>aap</b>：经第三方驱动的 L2CAP 控制通道（降噪模式等），见 <see cref="AapDriver"/>。</item>
/// </list>
/// 插件拿不到无线电和驱动句柄，只能经 <see cref="DeviceBroker"/> 按授权读写这两个通道。
/// </summary>
public sealed class BluetoothService : IDisposable
{
    public const ushort AppleVendorId = 0x004C;

    private readonly ILogger<BluetoothService> _log;
    private readonly ConcurrentDictionary<string, PairedDevice> _paired = new();  // DeviceInformation.Id → 设备
    private readonly ConcurrentDictionary<string, Channel<Advert>> _adverts = new(); // deviceKey → 广播队列
    private readonly ConcurrentDictionary<string, AapSession> _aap = new();         // deviceKey → AAP 会话
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DeviceWatcher? _watcher;
    private BluetoothLEAdvertisementWatcher? _scanner;
    private readonly object _scanLock = new();

    /// <summary>配对设备增删或连接状态变化。</summary>
    public event Action? Changed;

    public BluetoothService(ILogger<BluetoothService> log) => _log = log;

    private sealed class PairedDevice
    {
        public required string Id;
        public required BluetoothDevice Device;
        public required string Name;
        public ushort Vid, Pid;
        public string Key => $"bt:{Vid:x4}:{Pid:x4}:{Device.BluetoothAddress:x12}";
        public bool Connected => Device.ConnectionStatus == BluetoothConnectionStatus.Connected;
    }

    private sealed record Advert(byte[] Data, short Rssi);

    // —— 配对设备 ——

    /// <summary>开始监视已配对设备。没有蓝牙适配器时什么也不做（总线为空）。</summary>
    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            _watcher.Added += (_, info) => { var t = AddAsync(info.Id); };
            _watcher.Removed += (_, upd) => Remove(upd.Id);
            _watcher.Updated += (_, _) => { }; // 不订阅 Updated 时 DeviceWatcher 不会报告枚举完成

            _watcher.EnumerationCompleted += (_, _) => _ready.TrySetResult();
            _watcher.Stopped += (_, _) => _ready.TrySetResult();
            _watcher.Start();
        }
        catch (Exception ex)
        {
            _log.LogInformation("蓝牙不可用，跳过蓝牙设备：{Msg}", ex.Message);
            _ready.TrySetResult();
        }
    }

    /// <summary>等首轮枚举完成（插件启动前调用，保证已授权的 AirPods 能被接管）。</summary>
    public Task WaitReadyAsync(TimeSpan timeout) => _ready.Task.WaitAsync(timeout).ContinueWith(_ => { });

    private async Task AddAsync(string id)
    {
        try
        {
            var dev = await BluetoothDevice.FromIdAsync(id);
            if (dev is null) return;
            ushort vid = 0, pid = 0;
            try
            {
                var info = await DeviceInformation.CreateFromIdAsync(dev.DeviceInformation.Id,
                    new[] { "System.DeviceInterface.Bluetooth.VendorId", "System.DeviceInterface.Bluetooth.ProductId" });
                if (info.Properties.TryGetValue("System.DeviceInterface.Bluetooth.VendorId", out var v) && v is ushort vv) vid = vv;
                if (info.Properties.TryGetValue("System.DeviceInterface.Bluetooth.ProductId", out var p) && p is ushort pp) pid = pp;
            }
            catch (Exception ex) { _log.LogDebug(ex, "读取蓝牙 Device ID 失败 {Name}", dev.Name); }

            // 没有 Device ID 记录的老设备按名称兜底（同 PodBridge 的名称启发式），型号留 0 = 未知。
            if (vid == 0 && (dev.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || dev.Name.Contains("Beats", StringComparison.OrdinalIgnoreCase)))
                vid = AppleVendorId;
            if (vid != AppleVendorId) { dev.Dispose(); return; }

            var pd = new PairedDevice { Id = id, Device = dev, Name = dev.Name, Vid = vid, Pid = pid };
            dev.ConnectionStatusChanged += (_, _) =>
            {
                _log.LogInformation("蓝牙设备 {Name} {State}", pd.Name, pd.Connected ? "已连接" : "已断开");
                if (!pd.Connected) CloseAap(pd.Key);
                Changed?.Invoke();
            };
            dev.NameChanged += (_, _) => { pd.Name = dev.Name; Changed?.Invoke(); };
            _paired[id] = pd;
            _log.LogInformation("发现已配对 Apple 设备 {Name}（{Vid:X4}:{Pid:X4}，{State}）", pd.Name, vid, pid, pd.Connected ? "已连接" : "未连接");
            Changed?.Invoke();
        }
        catch (Exception ex) { _log.LogWarning(ex, "打开蓝牙设备失败 {Id}", id); }
    }

    private void Remove(string id)
    {
        if (!_paired.TryRemove(id, out var pd)) return;
        CloseAap(pd.Key);
        _adverts.TryRemove(pd.Key, out _);
        pd.Device.Dispose();
        Changed?.Invoke();
    }

    /// <summary>当前已配对的 Apple 设备（含未连接的）。</summary>
    public IReadOnlyList<DeviceInfo> Devices()
    {
        var driver = AapDriver.Installed();
        return _paired.Values.Select(pd =>
        {
            var channels = new List<HidChannelInfo> { new() { Id = "continuity", Kind = ChannelKinds.Continuity, InputLength = AdvertLength } };
            if (driver is not null) channels.Add(new HidChannelInfo { Id = "aap", Kind = ChannelKinds.Aap, InputLength = AapMaxFrame, OutputLength = AapMaxFrame });
            return new DeviceInfo
            {
                DeviceKey = pd.Key,
                Bus = DeviceBus.Bluetooth,
                Vid = pd.Vid,
                Pid = pd.Pid,
                DisplayName = pd.Name,
                Channels = channels,
                Connected = pd.Connected,
            };
        }).ToList();
    }

    private PairedDevice? Get(string deviceKey) => _paired.Values.FirstOrDefault(p => p.Key == deviceKey);

    // —— continuity：BLE 广播 ——

    /// <summary>接近配对包：类型 0x07、剩余长度 25，共 27 字节（不含公司 Id）。</summary>
    private const int AdvertLength = 27;

    /// <summary>读下一条属于此设备的接近配对广播。</summary>
    public async Task<DeviceIoResult> ReadAdvertAsync(string deviceKey, int timeoutMs)
    {
        var pd = Get(deviceKey);
        if (pd is null) return DeviceIoResult.Fail(DeviceIoStatus.DeviceGone);
        if (!pd.Connected) return DeviceIoResult.Fail(DeviceIoStatus.DeviceGone, "设备未连接");
        if (!EnsureScanning()) return DeviceIoResult.Fail(DeviceIoStatus.Error, "无法开始 BLE 扫描（蓝牙已关闭？）");

        var q = _adverts.GetOrAdd(deviceKey, _ => NewQueue<Advert>(8));
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var a = await q.Reader.ReadAsync(cts.Token);
            return new DeviceIoResult { Status = DeviceIoStatus.Ok, Data = a.Data, Rssi = a.Rssi };
        }
        catch (OperationCanceledException) { return DeviceIoResult.Fail(DeviceIoStatus.Timeout); }
    }

    /// <summary>按需开始扫描（只有插件读 continuity 时才扫）；被系统停掉（关蓝牙）后下次读取时重启。</summary>
    private bool EnsureScanning()
    {
        lock (_scanLock)
        {
            if (_scanner?.Status == BluetoothLEAdvertisementWatcherStatus.Started) return true;
            try
            {
                _scanner = new BluetoothLEAdvertisementWatcher
                {
                    // 主动扫描会顺带请求扫描响应，Apple 的厂商数据才完整（PodBridge 的实测结论）。
                    ScanningMode = BluetoothLEScanningMode.Active,
                };
                var filter = new BluetoothLEManufacturerData { CompanyId = AppleVendorId };
                _scanner.AdvertisementFilter.Advertisement.ManufacturerData.Add(filter);
                _scanner.Received += OnAdvertisement;
                _scanner.Stopped += (_, e) => _log.LogInformation("BLE 扫描已停止：{Err}", e.Error);
                _scanner.Start();
                _log.LogInformation("已开始 BLE 扫描（仅 Apple 厂商数据）");
                return _scanner.Status != BluetoothLEAdvertisementWatcherStatus.Aborted;
            }
            catch (Exception ex) { _log.LogWarning("开始 BLE 扫描失败：{Msg}", ex.Message); return false; }
        }
    }

    private void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        foreach (var md in e.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != AppleVendorId || md.Data.Length != AdvertLength) continue;
            var data = md.Data.ToArray();
            if (data[0] != 0x07 || data[1] != AdvertLength - 2) continue;
            var model = (ushort)(data[3] | data[4] << 8);
            // 末尾 16 字节是加密/哈希段，可能带用户信息，不交给插件（同 AirPodsDesktop 的 Desensitize）。
            Array.Clear(data, 11, 16);

            foreach (var pd in _paired.Values)
            {
                if (!pd.Connected || (pd.Pid != 0 && pd.Pid != model)) continue;
                _adverts.GetOrAdd(pd.Key, _ => NewQueue<Advert>(8)).Writer.TryWrite(new Advert(data, e.RawSignalStrengthInDBm));
            }
        }
    }

    // —— aap：经驱动的 L2CAP 通道 ——

    private const int AapMaxFrame = 1024;

    private sealed class AapSession : IDisposable
    {
        public required IAapLink Link;
        public readonly Channel<byte[]> Inbound = NewQueue<byte[]>(64);
        public readonly CancellationTokenSource Stop = new();
        public volatile string? Error;
        public void Dispose() { Stop.Cancel(); try { Link.Dispose(); } catch { } }
    }

    public DeviceIoResult SendAap(string deviceKey, ushort psm, byte[] frame)
    {
        var (s, fail) = OpenAap(deviceKey, psm);
        if (s is null) return fail!;
        try { s.Link.Send(frame); return DeviceIoResult.Success(); }
        catch (Exception ex) { CloseAap(deviceKey); return DeviceIoResult.Fail(DeviceIoStatus.Error, "AAP 发送失败：" + ex.Message); }
    }

    public async Task<DeviceIoResult> ReceiveAapAsync(string deviceKey, ushort psm, int timeoutMs)
    {
        var (s, fail) = OpenAap(deviceKey, psm);
        if (s is null) return fail!;
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return DeviceIoResult.Success(await s.Inbound.Reader.ReadAsync(cts.Token)); }
        catch (OperationCanceledException) { return DeviceIoResult.Fail(DeviceIoStatus.Timeout); }
        catch (ChannelClosedException) { return DeviceIoResult.Fail(DeviceIoStatus.DeviceGone, s.Error ?? "AAP 通道已断开"); }
    }

    private (AapSession?, DeviceIoResult?) OpenAap(string deviceKey, ushort psm)
    {
        if (_aap.TryGetValue(deviceKey, out var s) && !s.Stop.IsCancellationRequested) return (s, null);
        var pd = Get(deviceKey);
        if (pd is null) return (null, DeviceIoResult.Fail(DeviceIoStatus.DeviceGone));
        if (!pd.Connected) return (null, DeviceIoResult.Fail(DeviceIoStatus.DeviceGone, "设备未连接"));
        lock (_aap)
        {
            if (_aap.TryGetValue(deviceKey, out s) && !s.Stop.IsCancellationRequested) return (s, null);
            try
            {
                var link = AapDriver.Open(pd.Device.BluetoothAddress, psm);
                if (link is null) return (null, DeviceIoResult.Fail(DeviceIoStatus.DeviceGone, "未安装 AAP 驱动"));
                s = new AapSession { Link = link };
                _aap[deviceKey] = s;
                var session = s;
                new Thread(() => ReceiveLoop(deviceKey, session)) { IsBackground = true, Name = "aap-rx " + pd.Name }.Start();
                _log.LogInformation("AAP 通道已打开 {Name}（{Driver}）", pd.Name, link.DriverName);
                return (s, null);
            }
            catch (Exception ex)
            {
                _log.LogWarning("打开 AAP 通道失败 {Name}：{Msg}", pd.Name, ex.Message);
                return (null, DeviceIoResult.Fail(DeviceIoStatus.Error, "打开 AAP 通道失败：" + ex.Message));
            }
        }
    }

    private void ReceiveLoop(string deviceKey, AapSession s)
    {
        var buf = new byte[AapMaxFrame];
        try
        {
            while (!s.Stop.IsCancellationRequested)
            {
                var n = s.Link.Receive(buf, 500);
                if (n > 0) s.Inbound.Writer.TryWrite(buf[..n]);
            }
        }
        catch (Exception ex) when (!s.Stop.IsCancellationRequested)
        {
            s.Error = "AAP 通道已断开：" + ex.Message;
            _log.LogInformation("AAP 接收结束 {Device}：{Msg}", deviceKey, ex.Message);
        }
        catch { }
        finally
        {
            s.Inbound.Writer.TryComplete();
            s.Stop.Cancel();
            _aap.TryRemove(new KeyValuePair<string, AapSession>(deviceKey, s));
            try { s.Link.Dispose(); } catch { }
        }
    }

    /// <summary>关闭某设备的 AAP 通道（撤销授权、断开连接时）。</summary>
    public void CloseAap(string deviceKey)
    {
        if (_aap.TryRemove(deviceKey, out var s)) { s.Dispose(); _log.LogInformation("已关闭 AAP 通道 {Device}", deviceKey); }
    }

    private static Channel<T> NewQueue<T>(int capacity) =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false });

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { }
        try { _scanner?.Stop(); } catch { }
        foreach (var k in _aap.Keys.ToList()) CloseAap(k);
        foreach (var pd in _paired.Values) pd.Device.Dispose();
    }
}
