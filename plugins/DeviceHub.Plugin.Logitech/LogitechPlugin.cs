using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DeviceHub.Contracts;
using DeviceHub.Sdk;

namespace DeviceHub.Plugin.Logitech;

/// <summary>
/// 罗技插件后端：经 HID++ 管理 Unifying / Bolt / Lightspeed 接收器上的设备，以及有线、蓝牙直连设备。
/// 协议与 Feature 语义参照 Solaar、libratbag（Piper）、logiops（见 <see cref="Hidpp"/>）。
/// 每个设备先用 Root.getFeature 查出 Feature 索引，只操作设备声明支持的 Feature；设备 I/O 经核心 Broker 代理。
/// </summary>
public sealed class LogitechPlugin : IDevicePlugin
{
    public int SchemaVersion => 1;

    private const int ReplyTimeoutMs = 500;
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(60);

    private IPluginContext _ctx = null!;
    private readonly ConcurrentDictionary<string, LogitechState> _states = new();
    private readonly ConcurrentDictionary<string, Link> _links = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _listeners = new();
    /// <summary>一问一答期间读到的非回包（设备通知），由监听循环在释放设备锁后处理。</summary>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<byte[]>> _inbox = new();
    /// <summary>(deviceKey, 设备号) → feature id → 索引。</summary>
    private readonly ConcurrentDictionary<(string, int), Dictionary<ushort, byte>> _features = new();
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>设备上可用的 HID++ 集合：长报告必有；短报告集合可能没有（蓝牙、部分 Lightspeed 设备）。</summary>
    private sealed record Link(bool HasShort, ushort Pid);

    public Task InitializeAsync(IPluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        return Task.CompletedTask;
    }

    public async Task AttachAsync(DeviceInfo device, CancellationToken ct)
    {
        var st = _states.GetOrAdd(device.DeviceKey, _ => new LogitechState());
        _links[device.DeviceKey] = new Link(device.Channels.Any(c => c.UsagePage == 0xFF00 && c.Usage == 0x0001), device.Pid);
        st.ProductName = device.DisplayName;
        st.ReceiverKind = ReceiverKind(device.Pid);
        st.Link = device.Channels.Any(c => c.UsagePage == 0xFF43) ? "蓝牙" : st.ReceiverKind.Length > 0 ? "USB 接收器" : "USB";

        var scope = await _ctx.Device.RequestAccessAsync(device.DeviceKey, "读取电量、DPI 等设置，并按你的操作写入设备");
        st.Authorized = scope != AccessScope.Denied;
        st.Connected = true;
        st.Status = st.Authorized ? "" : "用户拒绝授权";
        await Push(device.DeviceKey, st);
        if (!st.Authorized) return;

        await RefreshAsync(device.DeviceKey, st);
        StartPoll(device.DeviceKey, st);
        StartListener(device.DeviceKey, st);
    }

    // —— 子设备：接收器上的每个配对设备在核心里是一个独立设备（键 = 接收器键/Id），页面、改名、提示都按它走 ——

    /// <summary>设备键 → (物理设备键, 状态, 对应的 HID++ 设备)。接收器本身没有对应设备（Ds 为 null）。</summary>
    private (string Key, LogitechState St, HidppDeviceState? Ds) Resolve(string deviceKey)
    {
        if (DeviceKeys.TrySplit(deviceKey, out var parent, out var id))
        {
            var pst = _states.GetOrAdd(parent, _ => new LogitechState());
            return (parent, pst, pst.Devices.FirstOrDefault(d => d.Id == id));
        }
        var st = _states.GetOrAdd(deviceKey, _ => new LogitechState());
        return (deviceKey, st, st.IsReceiver ? null : st.Devices.FirstOrDefault());
    }

    /// <summary>设备在核心里的键：配对设备是子设备键，直连设备就是物理设备键。</summary>
    private static string KeyOf(string key, LogitechState st, HidppDeviceState ds) =>
        st.IsReceiver ? DeviceKeys.Child(key, ds.Id) : key;

    /// <summary>推给某个设备页的状态：接收器 / 直连设备是完整状态，子设备只含它自己。</summary>
    private LogitechState View(string deviceKey)
    {
        var (_, st, ds) = Resolve(deviceKey);
        if (!DeviceKeys.IsChild(deviceKey)) return st;
        return ds is null
            ? new LogitechState { Authorized = st.Authorized, IsChild = true, ReceiverKind = st.ReceiverKind, Status = st.Connected ? "接收器上已没有这个设备" : "" }
            : ChildView(st, ds);
    }

    private static LogitechState ChildView(LogitechState st, HidppDeviceState ds) => new()
    {
        Authorized = st.Authorized,
        Connected = st.Connected && ds.Online,
        Status = st.Status,
        IsChild = true,
        ProductName = ds.Name,
        ReceiverKind = st.ReceiverKind,
        Link = $"{ReceiverTitle(st.ReceiverKind)} · 设备 {ds.Index}",
        Devices = [ds],
        LastReadAt = st.LastReadAt,
    };

    private static string ChildName(HidppDeviceState d) => d.Name.Length > 0 ? d.Name : $"设备 {d.Index}";

    /// <summary>把接收器的配对表同步成核心的子设备（核心比较后有变化才刷新界面）。</summary>
    private Task PublishAsync(string key, LogitechState st) => _ctx.PublishChildrenAsync(key,
        st.IsReceiver && st.Authorized
            ? st.Devices.Where(d => d.Id.Length > 0)
                .Select(d => new ChildDevice { Id = d.Id, Name = ChildName(d), Connected = d.Online }).ToList()
            : []);

    /// <summary>按 PID 区分接收器类型（Solaar base_usb 的接收器表）；非接收器返回空。</summary>
    private static string ReceiverKind(ushort pid) => pid switch
    {
        0xC52B or 0xC532 => "unifying",
        0xC548 => "bolt",
        0xC539 or 0xC53A or 0xC53D or 0xC53F or 0xC541 or 0xC545 or 0xC547 or 0xC54D => "lightspeed",
        0xC517 or 0xC518 or 0xC51A or 0xC51B or 0xC521 or 0xC525 or 0xC526 or 0xC52E or 0xC52F
            or 0xC531 or 0xC534 or 0xC537 => "nano",
        _ => "",
    };

    private static string ReceiverTitle(string kind) => kind switch
    {
        "unifying" => "Logitech Unifying 接收器",
        "bolt" => "Logitech Bolt 接收器",
        "lightspeed" => "Logitech LIGHTSPEED 接收器",
        "nano" => "Logitech Nano 接收器",
        _ => "Logitech 接收器",
    };

    public Task<DeviceDescription?> DescribeAsync(string deviceKey)
    {
        if (DeviceKeys.IsChild(deviceKey)) return Task.FromResult(DescribeChild(deviceKey));
        if (!_states.TryGetValue(deviceKey, out var st)) return Task.FromResult<DeviceDescription?>(null);
        var online = st.Devices.Where(d => d.Online).ToList();
        string? title = st.IsReceiver ? ReceiverTitle(st.ReceiverKind) : st.Devices.FirstOrDefault()?.Name is { Length: > 0 } n ? n : null;
        string subtitle = st.IsReceiver
            ? (online.Count == 0 ? $"{st.Devices.Count} 个已配对设备，均未连接"
                : string.Join(" · ", online.Select(d => d.Battery?.Percent is { } p ? $"{d.Name} {p}%" : d.Name)))
            : string.Join(" · ", new[] { st.Link, st.Devices.FirstOrDefault()?.Battery?.Percent is { } bp ? $"电量 {bp}%" : null }.Where(s => s is not null));

        var details = new List<DeviceDetailItem>
        {
            new("连接方式", st.Link),
            new("配置通道", "HID++（厂商自定义 0xFF00 短/长报告，蓝牙为 0xFF43）"),
        };
        if (st.IsReceiver)
        {
            details.Add(new("接收器固件", st.ReceiverFirmware.Length > 0 ? st.ReceiverFirmware : "未读取"));
            foreach (var d in st.Devices)
                details.Add(new($"设备 {d.Index}", $"{d.Name}{(d.Online ? "" : "（未连接）")}{(d.Firmware.Length > 0 ? " · " + d.Firmware : "")}"));
        }
        else if (st.Devices.FirstOrDefault() is { } dev)
        {
            details.Add(new("协议", dev.Protocol.Length > 0 ? "HID++ " + dev.Protocol : "未读取"));
            details.Add(new("固件", dev.Firmware.Length > 0 ? dev.Firmware : "未读取"));
            details.Add(new("Feature", string.Join("、", dev.Features.Select(f => f.Name))));
        }
        details.Add(new("最近读取", st.LastReadAt is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未读取"));
        details.Add(new("已支持", "电量、DPI、回报率、指针速度、SmartShift、滚轮方向/高精度、Fn 反转、Easy-Switch、板载配置"));
        details.Add(new("尚未支持", "灯效、按键重映射、宏、配对新设备、扩展 DPI/回报率（0x2202/0x8061）"));
        details.Add(new("注意", "G HUB / Logi Options+ 同时运行时会互相覆盖设置，建议先退出"));
        return Task.FromResult<DeviceDescription?>(new DeviceDescription
        {
            Title = title, Subtitle = subtitle, Details = details,
            Glyph = st.IsReceiver ? "" : KindGlyph(st.Devices.FirstOrDefault()?.Kind ?? ""), // 接收器用 USB 图标
        });
    }

    private DeviceDescription? DescribeChild(string deviceKey)
    {
        var (_, st, ds) = Resolve(deviceKey);
        if (ds is null) return null;
        var subtitle = !ds.Online ? "未连接"
            : ds.Battery?.Percent is { } p ? $"{ReceiverTitle(st.ReceiverKind)} · 电量 {p}%" : ReceiverTitle(st.ReceiverKind);
        var details = new List<DeviceDetailItem>
        {
            new("连接方式", $"经 {ReceiverTitle(st.ReceiverKind)}（配对槽位 {ds.Index}）"),
            new("状态", ds.Online ? "已连接" : ds.Status.Length > 0 ? ds.Status : "未连接"),
            new("协议", ds.Protocol.Length > 0 ? "HID++ " + ds.Protocol : "未读取"),
            new("固件", ds.Firmware.Length > 0 ? ds.Firmware : "未读取"),
        };
        if (ds.Wpid.Length > 0) details.Add(new("WPID", ds.Wpid));
        details.Add(new("设备 Id", ds.Id.StartsWith("slot", StringComparison.Ordinal) ? $"{ds.Id}（接收器不提供序列号）" : ds.Id));
        if (ds.Features.Count > 0) details.Add(new("Feature", string.Join("、", ds.Features.Select(f => f.Name))));
        details.Add(new("最近读取", st.LastReadAt is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未读取"));
        details.Add(new("设置恢复", "在本软件里改过的 DPI、回报率、滚轮等设置会在设备重新连接时自动写回"));
        return new DeviceDescription { Title = ChildName(ds), Subtitle = subtitle, Details = details, Glyph = KindGlyph(ds.Kind) };
    }

    public Task DetachAsync(string deviceKey)
    {
        if (_pollers.TryRemove(deviceKey, out var cts)) cts.Cancel();
        if (_listeners.TryRemove(deviceKey, out var lcts)) lcts.Cancel();
        if (_states.TryGetValue(deviceKey, out var s)) { s.Connected = false; s.Status = "设备已移除"; }
        return Task.CompletedTask;
    }

    public async Task<string> InvokeAsync(string deviceKey, string method, string argsJson, CancellationToken ct)
    {
        // 子设备页的操作落到接收器状态里对应的配对设备上；I/O 一律用物理设备键。
        await EnsureStateAsync(deviceKey);
        var (key, st, ds) = Resolve(deviceKey);
        var a = string.IsNullOrWhiteSpace(argsJson) ? default : JsonDocument.Parse(argsJson).RootElement;

        if (method == "Refresh")
        {
            await RefreshAsync(key, st);
            return Json(View(deviceKey));
        }

        if (method is "StartPairing" or "StopPairing" or "Unpair")
        {
            if (DeviceKeys.IsChild(deviceKey) || !st.CanPair) st.PairingStatus = "这个接收器不支持在这里配对";
            else await PairingAsync(key, st, method, a);
            await Push(key, st);
            return Json(View(deviceKey));
        }

        if (ds is null) { st.Status = st.IsReceiver ? "请在左侧选择接收器下的设备进行设置" : "设备不存在"; return Json(View(deviceKey)); }
        if (!ds.Online) { ds.Status = "设备未连接，无法写入"; await Push(key, st); return Json(View(deviceKey)); }

        await ApplyAsync(key, st, ds, method, a);
        if (Persisted.Contains(method) && ds.Status == Written)
            await SaveSettingAsync(KeyOf(key, st, ds), method, string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);

        await Push(key, st);
        return Json(View(deviceKey));
    }

    private const string Written = "已写入设备";

    /// <summary>重新连接时自动写回的设置（按此顺序）；切换主机、板载模式这类一次性操作不记。</summary>
    private static readonly string[] Persisted = ["SetDpi", "SetReportRate", "SetPointerSpeed", "SetSmartShift", "SetWheel", "SetFnSwap"];

    private static string SettingsKey(string ownKey) => "settings:" + ownKey;

    private async Task SaveSettingAsync(string ownKey, string method, string argsJson)
    {
        var map = await _ctx.Storage.GetAsync<Dictionary<string, string>>(SettingsKey(ownKey)) ?? new();
        map[method] = argsJson;
        await _ctx.Storage.SetAsync(SettingsKey(ownKey), map);
    }

    /// <summary>
    /// 设备上线后把用户在本软件里改过的设置写回（Solaar 的做法：鼠标断电重连后部分设置会回到默认）。
    /// 只写与设备当前值不同的项。
    /// </summary>
    private async Task RestoreAsync(string key, LogitechState st, HidppDeviceState ds)
    {
        if (!ds.Online) return;
        var map = await _ctx.Storage.GetAsync<Dictionary<string, string>>(SettingsKey(KeyOf(key, st, ds)));
        if (map is null) return;
        int n = 0;
        foreach (var m in Persisted)
        {
            if (!map.TryGetValue(m, out var json)) continue;
            var a = JsonDocument.Parse(json).RootElement;
            if (!Differs(m, a, ds)) continue;
            await ApplyAsync(key, st, ds, m, a);
            if (ds.Status == Written) n++;
        }
        if (n > 0) ds.Status = $"已恢复 {n} 项设置";
    }

    private static bool Differs(string method, JsonElement a, HidppDeviceState ds) => method switch
    {
        "SetDpi" => ds.Dpi is { } d && Int(a, "dpi", d.Value) != d.Value,
        "SetReportRate" => ds.ReportRate is { } r && Int(a, "ms", r.Ms) != r.Ms,
        "SetPointerSpeed" => ds.PointerSpeed is { } p && Int(a, "value", p) != p,
        "SetSmartShift" => ds.SmartShift is { } s && (Bool(a, "ratchet", s.Ratchet) != s.Ratchet || Int(a, "threshold", s.Threshold) != s.Threshold),
        "SetWheel" => ds.Wheel is { } w && (Bool(a, "hiRes", w.HiRes) != w.HiRes || Bool(a, "inverted", w.Inverted) != w.Inverted),
        "SetFnSwap" => ds.FnSwap is { } f && Bool(a, "swap", f) != f,
        _ => false,
    };

    /// <summary>对一个在线设备执行一项设置；结果写在 ds.Status（成功为 <see cref="Written"/>）。</summary>
    private async Task ApplyAsync(string deviceKey, LogitechState st, HidppDeviceState ds, string method, JsonElement a)
    {
        var dev = (byte)ds.Index;
        ds.Status = "";

        switch (method)
        {
            case "SetDpi" when ds.Dpi is { } dpi && Feat(deviceKey, dev, Feature.AdjustableDpi) is { } f:
            {
                int v = Int(a, "dpi", dpi.Value);
                v = dpi.Choices.Count > 0 ? dpi.Choices.MinBy(c => Math.Abs(c - v))
                    : Math.Clamp(dpi.Min + (v - dpi.Min + dpi.Step / 2) / Math.Max(1, dpi.Step) * dpi.Step, dpi.Min, dpi.Max);
                if (await Write(deviceKey, st, ds, Hidpp.Call(dev, f, 3, "设置 DPI", 0, (byte)(v >> 8), (byte)v)))
                    await ReadDpi(deviceKey, st, ds, f);
                break;
            }

            case "SetReportRate" when ds.ReportRate is { } rr && Feat(deviceKey, dev, Feature.ReportRate) is { } f:
            {
                int ms = Int(a, "ms", rr.Ms);
                if (!rr.Choices.Contains(ms)) { ds.Status = $"不支持 {ms} ms"; break; }
                if (await Write(deviceKey, st, ds, Hidpp.Call(dev, f, 2, "设置回报率", (byte)ms))) rr.Ms = ms;
                break;
            }

            case "SetPointerSpeed" when ds.PointerSpeed is not null && Feat(deviceKey, dev, Feature.PointerSpeed) is { } f:
            {
                // Solaar 的取值范围：0x002E–0x01FF，0x0100 = 1.0 倍。
                int v = Math.Clamp(Int(a, "value", ds.PointerSpeed.Value), 0x2E, 0x1FF);
                if (await Write(deviceKey, st, ds, Hidpp.Call(dev, f, 1, "设置指针速度", (byte)(v >> 8), (byte)v))) ds.PointerSpeed = v;
                break;
            }

            case "SetSmartShift" when ds.SmartShift is { } ss:
            {
                bool ratchet = Bool(a, "ratchet", ss.Ratchet);
                int threshold = Math.Clamp(Int(a, "threshold", ss.Threshold), 1, 255);
                // 0x2110 set = function 1；0x2111 set = function 2。参数 [模式 1 自由/2 棘轮, 阈值, 0=默认阈值不变]。
                var (id, fn) = ss.Enhanced ? (Feature.SmartShiftEnhanced, 2) : (Feature.SmartShift, 1);
                if (Feat(deviceKey, dev, id) is { } f &&
                    await Write(deviceKey, st, ds, Hidpp.Call(dev, f, fn, "设置 SmartShift", (byte)(ratchet ? 2 : 1), (byte)threshold, 0)))
                    await ReadSmartShift(deviceKey, st, ds);
                break;
            }

            case "SetWheel" when ds.Wheel is { } w && Feat(deviceKey, dev, Feature.HiResWheel) is { } f:
            {
                bool hiRes = Bool(a, "hiRes", w.HiRes), inverted = Bool(a, "inverted", w.Inverted);
                // 模式位：0x01 target（转发给 HID++，保持原样）、0x02 高精度、0x04 反向。
                byte mode = (byte)((w.Diverted ? 0x01 : 0) | (hiRes ? 0x02 : 0) | (inverted ? 0x04 : 0));
                if (await Write(deviceKey, st, ds, Hidpp.Call(dev, f, 2, "设置滚轮", mode)))
                    await ReadWheel(deviceKey, st, ds, f);
                break;
            }

            case "SetFnSwap" when ds.FnSwap is not null:
            {
                bool swap = Bool(a, "swap", ds.FnSwap.Value);
                byte v = (byte)(swap ? 1 : 0);
                Hidpp.Request? req =
                    Feat(deviceKey, dev, Feature.NewFnInversion) is { } f2 ? Hidpp.Call(dev, f2, 1, "设置 Fn 反转", v)
                    : Feat(deviceKey, dev, Feature.K375sFnInversion) is { } f3 ? Hidpp.Call(dev, f3, 1, "设置 Fn 反转", 0xFF, v)
                    : Feat(deviceKey, dev, Feature.FnInversion) is { } f1 ? Hidpp.Call(dev, f1, 1, "设置 Fn 反转", v)
                    : null;
                if (req is not null && await Write(deviceKey, st, ds, req)) ds.FnSwap = swap;
                break;
            }

            case "SetHost" when ds.Hosts is { } h && Feat(deviceKey, dev, Feature.ChangeHost) is { } f:
            {
                int host = Int(a, "host", h.Current);
                if (host < 0 || host >= h.Count || host == h.Current) break;
                // 设备会立刻断开并连到另一台主机，不会回包（Solaar 同样不等回复）。
                if (await WriteOnly(deviceKey, st, Hidpp.Call(dev, f, 1, "切换主机", (byte)host)))
                {
                    h.Current = host;
                    ds.Online = false;
                    ds.Status = $"已切换到通道 {host + 1}";
                }
                break;
            }

            case "SetOnboardMode" when ds.Onboard is { } ob && Feat(deviceKey, dev, Feature.OnboardProfiles) is { } f:
            {
                bool onboard = Bool(a, "onboard", ob.Onboard);
                // libratbag：setMode = function 1，1 = 板载、2 = 主机。
                if (await Write(deviceKey, st, ds, Hidpp.Call(dev, f, 1, "切换板载模式", (byte)(onboard ? 1 : 2))))
                    await ReadOnboard(deviceKey, st, ds, f);
                break;
            }

            case "SetProfile" when ds.Onboard is { } ob && Feat(deviceKey, dev, Feature.OnboardProfiles) is { } f:
            {
                int p = Int(a, "profile", ob.Profile);
                if (p < 1 || p > ob.ProfileCount) break;
                if (await Write(deviceKey, st, ds, Hidpp.Call(dev, f, 3, "切换配置", 0, (byte)p)))
                    await ReadOnboard(deviceKey, st, ds, f);
                break;
            }

            default:
                ds.Status = "该设备不支持此操作";
                break;
        }
    }

    public async Task<string> GetStateAsync(string deviceKey)
    {
        await EnsureStateAsync(deviceKey);
        return Json(View(deviceKey));
    }

    /// <summary>
    /// 已授权但离线的设备（侧边栏离线项）没被 Attach 过，第一次被问到时向核心查授权，
    /// 免得默认状态把"未连接"显示成"未授权"。子设备按物理设备（接收器）的授权算。
    /// </summary>
    private async Task EnsureStateAsync(string deviceKey)
    {
        var key = DeviceKeys.TrySplit(deviceKey, out var parent, out _) ? parent : deviceKey;
        if (_states.ContainsKey(key)) return;
        var authorized = await _ctx.Device.GetAccessAsync(key) != AccessScope.Denied;
        _states.TryAdd(key, new LogitechState { Authorized = authorized });
    }

    public Task PrepareShutdownAsync()
    {
        foreach (var cts in _pollers.Values) cts.Cancel();
        foreach (var cts in _listeners.Values) cts.Cancel();
        return Task.CompletedTask;
    }

    // —— 枚举 ——

    /// <summary>
    /// 先 ping 0xFF：回 2.x 的是直连设备；回 1.0 错误的是接收器，读它的配对表再逐个 ping 配对设备。
    /// </summary>
    private async Task RefreshAsync(string deviceKey, LogitechState st)
    {
        if (!st.Authorized) { st.Status = "未授权"; return; }
        st.Status = "正在读取…";
        await Push(deviceKey, st);

        var ping = await TransactAsync(deviceKey, st, Hidpp.Ping(Hidpp.Receiver));
        if (ping.Failed) { await Push(deviceKey, st); return; }

        if (ping.Data is { } r && r[4] >= 2)
        {
            st.IsReceiver = false;
            var ds = st.Devices.FirstOrDefault(d => d.Index == Hidpp.Receiver) ?? new HidppDeviceState { Index = Hidpp.Receiver };
            st.Devices = [ds];
            await ProbeAndRestoreAsync(deviceKey, st, ds);
            if (ds.Name.Length == 0) ds.Name = st.ProductName;
        }
        else if (ping.Error is not null || ping.Data is not null)
        {
            st.IsReceiver = true;
            await EnableConnectionNotificationsAsync(deviceKey, st);
            await ReadReceiverAsync(deviceKey, st);
            foreach (var ds in st.Devices) await ProbeAndRestoreAsync(deviceKey, st, ds);
            // 没有配对表的接收器只能靠 ping 发现设备：去掉没响应、也没记过名字的空槽位。
            if (!HasPairingTable(st)) st.Devices.RemoveAll(d => !d.Online && d.Name.Length == 0);
            await SaveNamesAsync(deviceKey, st);
        }
        else
        {
            st.Status = "设备对 HID++ 无响应";
            await Push(deviceKey, st);
            return;
        }

        if (st.Status == "正在读取…") st.Status = "";
        st.LastReadAt = DateTimeOffset.Now;
        await Push(deviceKey, st);
    }

    private static bool HasPairingTable(LogitechState st) => st.ReceiverKind is "unifying" or "bolt";

    /// <summary>探测设备；从离线变为在线时写回保存的设置。返回是否刚刚上线。</summary>
    private async Task<bool> ProbeAndRestoreAsync(string deviceKey, LogitechState st, HidppDeviceState ds)
    {
        bool was = ds.Online;
        await ProbeDeviceAsync(deviceKey, st, ds);
        if (!ds.Online || was) return false;
        await RestoreAsync(deviceKey, st, ds);
        return true;
    }

    /// <summary>
    /// 打开接收器的无线连接通知（寄存器 0x00 的 0x000100 位，Solaar 的 NotificationFlag.wireless），
    /// 配对设备连上 / 断开时接收器才会发 0x41 通知。只追加这一位，不动其他软件设的位。
    /// </summary>
    private async Task EnableConnectionNotificationsAsync(string deviceKey, LogitechState st)
    {
        var r = await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.GetRegister, Hidpp.RegNotifications, "读取通知开关"));
        if (r.Data is not { } f || (f[5] & 0x01) != 0) return;
        await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.SetRegister, Hidpp.RegNotifications, "开启连接通知", f[4], (byte)(f[5] | 0x01), f[6]));
    }

    /// <summary>接收器固件（寄存器 0xF1）与配对表（长寄存器 0xB5）。</summary>
    private async Task ReadReceiverAsync(string deviceKey, LogitechState st)
    {
        var fw = await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.GetRegister, Hidpp.RegReceiverFirmware, "读取接收器固件", 0x01));
        var build = await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.GetRegister, Hidpp.RegReceiverFirmware, "读取接收器固件", 0x02));
        if (fw.Data is { } f)
            st.ReceiverFirmware = $"{f[5]:X2}.{f[6]:X2}" + (build.Data is { } b ? $".B{b[5]:X2}{b[6]:X2}" : "");

        var cached = await _ctx.Storage.GetAsync<Dictionary<int, string>>(NamesKey(deviceKey)) ?? new();
        var cachedIds = await _ctx.Storage.GetAsync<Dictionary<int, string>>(IdsKey(deviceKey)) ?? new();
        var old = st.Devices.ToDictionary(d => d.Index);
        var list = new List<HidppDeviceState>();
        bool bolt = st.ReceiverKind == "bolt";
        int slots = st.ReceiverKind is "nano" or "lightspeed" ? 2 : 6;

        for (int n = 1; n <= slots; n++)
        {
            var ds = old.GetValueOrDefault(n) ?? new HidppDeviceState { Index = n };
            if (HasPairingTable(st))
            {
                // Unifying：0x20+n-1 配对信息 / 0x40+n-1 名称；Bolt：0x50+n / 0x60+n（Solaar receiver.py）。
                // 读不到即槽位未配对。
                var pair = await TransactAsync(deviceKey, st, LongRegister(bolt ? 0x50 + n : 0x20 + n - 1));
                if (pair.Data is not { } p) continue;

                ds.Wpid = bolt ? $"{p[7]:X2}{p[6]:X2}" : $"{p[7]:X2}{p[8]:X2}";
                ds.Kind = HidppParse.PairedKind((bolt ? p[5] : p[11]) & 0x0F);
                var nameReply = await TransactAsync(deviceKey, st, LongRegister(bolt ? 0x60 + n : 0x40 + n - 1, bolt ? (byte)0x01 : (byte)0));
                if (nameReply.Data is { } nm)
                {
                    int off = bolt ? 7 : 6, len = Math.Min(nm[off - 1], nm.Length - off);
                    if (len > 0) ds.Name = Encoding.ASCII.GetString(nm, off, len).TrimEnd('\0');
                }
                // 序列号作子设备 Id：同一个设备换槽位重新配对后仍是同一个子设备（名字、设置跟着走）。
                // Unifying 在扩展配对信息 0x30+n-1 的 [1..4]；Bolt 在配对信息的 [4..7]（Solaar receiver.py）。
                byte[]? sn = bolt ? p[8..12]
                    : (await TransactAsync(deviceKey, st, LongRegister(0x30 + n - 1))).Data is { } x ? x[5..9] : null;
                if (sn is not null && sn.Any(b => b != 0)) ds.Id = Convert.ToHexString(sn);
            }
            // 读不到序列号：沿用上次的 Id，没有就按槽位。
            if (ds.Id.Length == 0) ds.Id = cachedIds.GetValueOrDefault(n) ?? $"slot{n}";
            if (ds.Name.Length == 0 && cached.TryGetValue(n, out var cn)) ds.Name = cn;
            list.Add(ds);
        }
        st.Devices = list;

        Hidpp.Request LongRegister(int sub, byte extra = 0) =>
            Hidpp.Register(Hidpp.Receiver, Hidpp.GetLongRegister, Hidpp.RegReceiverInfo, "读取配对信息", (byte)sub, extra);
    }

    /// <summary>ping 一个设备，查 Feature 索引并读出所有已支持的设置。</summary>
    private async Task ProbeDeviceAsync(string deviceKey, LogitechState st, HidppDeviceState ds)
    {
        var dev = (byte)ds.Index;
        var ping = await TransactAsync(deviceKey, st, Hidpp.Ping(dev));
        if (ping.Data is not { } pr)
        {
            ds.Online = false;
            ds.Status = ping.Error is { } e ? (e == 0x01 ? "HID++ 1.0 设备（暂不支持）" : Hidpp.Error10Text(e)) : "无响应";
            if (st.IsReceiver && ping.Error is 0x08) ds.Status = "未配对";
            return;
        }
        ds.Online = true;
        ds.Status = "";
        ds.Protocol = $"{pr[4]}.{pr[5]}";

        var feats = new Dictionary<ushort, byte>();
        foreach (var id in Feature.Probe)
            if (await TransactAsync(deviceKey, st, Hidpp.GetFeature(dev, id)) is { Data: { } fr } && fr[4] != 0)
                feats[id] = fr[4];
        _features[(deviceKey, ds.Index)] = feats;
        ds.Features = feats.OrderBy(kv => kv.Key)
            .Select(kv => new FeatureEntry { Id = $"0x{kv.Key:X4}", Name = Feature.Name(kv.Key), Index = kv.Value }).ToList();
        ds.Unsupported = new[] { Feature.ColorLedEffects, Feature.RgbEffects, Feature.ReprogControlsV4, Feature.ExtendedDpi, Feature.ExtendedReportRate }
            .Where(feats.ContainsKey).Select(Feature.Name).Distinct().ToList();

        if (feats.TryGetValue(Feature.DeviceName, out var fn)) await ReadName(deviceKey, st, ds, fn);
        if (feats.TryGetValue(Feature.FirmwareInfo, out var ff)) await ReadFirmware(deviceKey, st, ds, ff);
        await ReadBattery(deviceKey, st, ds);
        if (feats.TryGetValue(Feature.AdjustableDpi, out var fd)) await ReadDpi(deviceKey, st, ds, fd); else ds.Dpi = null;
        if (feats.TryGetValue(Feature.ReportRate, out var frr)) await ReadReportRate(deviceKey, st, ds, frr); else ds.ReportRate = null;
        if (feats.TryGetValue(Feature.PointerSpeed, out var fps) &&
            await TransactAsync(deviceKey, st, Hidpp.Call(dev, fps, 0, "读取指针速度")) is { Data: { } ps })
            ds.PointerSpeed = (ps[4] << 8) | ps[5];
        else ds.PointerSpeed = null;
        await ReadSmartShift(deviceKey, st, ds);
        if (feats.TryGetValue(Feature.HiResWheel, out var fw)) await ReadWheel(deviceKey, st, ds, fw); else ds.Wheel = null;
        await ReadFnSwap(deviceKey, st, ds);
        if (feats.TryGetValue(Feature.ChangeHost, out var fh) &&
            await TransactAsync(deviceKey, st, Hidpp.Call(dev, fh, 0, "读取主机通道")) is { Data: { } hr } && hr[4] > 1)
            ds.Hosts = new HostState { Count = hr[4], Current = hr[5] };
        else ds.Hosts = null;
        if (feats.TryGetValue(Feature.OnboardProfiles, out var fo)) await ReadOnboard(deviceKey, st, ds, fo); else ds.Onboard = null;
    }

    private async Task ReadName(string deviceKey, LogitechState st, HidppDeviceState ds, byte f)
    {
        var dev = (byte)ds.Index;
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 0, "读取名称长度")) is not { Data: { } c }) return;
        var sb = new StringBuilder();
        for (int off = 0; off < c[4] && off < 64;)
        {
            if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 1, "读取名称", (byte)off)) is not { Data: { } part }) break;
            int take = Math.Min(16, c[4] - off);
            sb.Append(Encoding.ASCII.GetString(part, 4, take));
            off += take;
        }
        if (sb.Length > 0) ds.Name = sb.ToString().TrimEnd('\0');
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 2, "读取设备类型")) is { Data: { } t })
            ds.Kind = HidppParse.DeviceType(t[4]);
    }

    private async Task ReadFirmware(string deviceKey, LogitechState st, HidppDeviceState ds, byte f)
    {
        var dev = (byte)ds.Index;
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 0, "读取固件实体数")) is not { Data: { } c }) return;
        for (int e = 0; e < Math.Min((int)c[4], 8); e++)
        {
            if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 1, "读取固件", (byte)e)) is not { Data: { } fw }) continue;
            // 类型 0 = 主固件（1 = bootloader、2 = 硬件版本…）。
            if ((fw[4] & 0x0F) == 0) { ds.Firmware = HidppParse.Firmware(fw); return; }
        }
    }

    /// <summary>优先统一电池（0x1004），其次电池状态（0x1000），最后按电压估算（0x1001）。</summary>
    private async Task ReadBattery(string deviceKey, LogitechState st, HidppDeviceState ds)
    {
        var dev = (byte)ds.Index;
        if (Feat(deviceKey, dev, Feature.UnifiedBattery) is { } fu &&
            await TransactAsync(deviceKey, st, Hidpp.Call(dev, fu, 1, "读取电量")) is { Data: { } u })
        {
            // [电量%, 等级位(1 危急/2 低/4 良好/8 满), 充电状态, 外接电源]；部分设备电量% 为 0、只报等级。
            int? pct = u[4] > 0 ? u[4] : (u[5] & 8) != 0 ? 100 : (u[5] & 4) != 0 ? 50 : (u[5] & 2) != 0 ? 20 : (u[5] & 1) != 0 ? 5 : null;
            ds.Battery = new BatteryState { Percent = pct, Estimated = u[4] == 0, Charging = u[6] is 1 or 2, Full = u[6] == 3, Status = HidppParse.UnifiedCharging(u[6]) };
        }
        else if (Feat(deviceKey, dev, Feature.BatteryStatus) is { } fb &&
                 await TransactAsync(deviceKey, st, Hidpp.Call(dev, fb, 0, "读取电量")) is { Data: { } b })
            ds.Battery = new BatteryState { Percent = b[4], Charging = b[6] is 1 or 2 or 4, Full = b[6] == 3, Status = HidppParse.BatteryStatus(b[6]) };
        else if (Feat(deviceKey, dev, Feature.BatteryVoltage) is { } fv &&
                 await TransactAsync(deviceKey, st, Hidpp.Call(dev, fv, 0, "读取电压")) is { Data: { } v })
        {
            int mv = (v[4] << 8) | v[5];
            bool charging = (v[6] & 0x80) != 0;
            ds.Battery = new BatteryState { Percent = HidppParse.VoltageToPercent(mv), Estimated = true, VoltageMv = mv, Charging = charging, Status = charging ? "充电中" : "放电中" };
        }
        else ds.Battery = null;
    }

    private async Task ReadDpi(string deviceKey, LogitechState st, HidppDeviceState ds, byte f)
    {
        var dev = (byte)ds.Index;
        var dpi = ds.Dpi ?? new DpiState();
        if (dpi.Max == 0 && await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 1, "读取 DPI 档位", 0)) is { Data: { } l })
            (dpi.Choices, dpi.Min, dpi.Max, dpi.Step) = HidppParse.DpiList(l);
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 2, "读取 DPI", 0)) is { Data: { } c })
        {
            dpi.Value = (c[5] << 8) | c[6];
            dpi.Default = (c[7] << 8) | c[8];
            // 部分设备 getSensorDpi 的当前值为 0，表示"默认值"。
            if (dpi.Value == 0) dpi.Value = dpi.Default;
            ds.Dpi = dpi;
        }
    }

    private async Task ReadReportRate(string deviceKey, LogitechState st, HidppDeviceState ds, byte f)
    {
        var dev = (byte)ds.Index;
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 0, "读取回报率档位")) is not { Data: { } l }) return;
        // 位 i 置位 = 支持 (i+1) ms。
        var choices = Enumerable.Range(0, 8).Where(i => (l[4] & (1 << i)) != 0).Select(i => i + 1).ToList();
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 1, "读取回报率")) is { Data: { } c })
            ds.ReportRate = new ReportRateState { Ms = c[4], Choices = choices };
    }

    private async Task ReadSmartShift(string deviceKey, LogitechState st, HidppDeviceState ds)
    {
        var dev = (byte)ds.Index;
        // 0x2111 get = function 1；0x2110 get = function 0。回包 [模式, 自动切换阈值, 默认阈值]。
        var (f, fn, enhanced) = Feat(deviceKey, dev, Feature.SmartShiftEnhanced) is { } fe ? (fe, 1, true)
            : Feat(deviceKey, dev, Feature.SmartShift) is { } fs ? (fs, 0, false) : ((byte)0, 0, false);
        if (f == 0) { ds.SmartShift = null; return; }
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, fn, "读取 SmartShift")) is { Data: { } r })
            ds.SmartShift = new SmartShiftState { Ratchet = r[4] == 2, Threshold = r[5] == 0 ? r[6] : r[5], Enhanced = enhanced };
    }

    private async Task ReadWheel(string deviceKey, LogitechState st, HidppDeviceState ds, byte f)
    {
        var dev = (byte)ds.Index;
        var w = ds.Wheel ?? new WheelState();
        if (w.Multiplier == 0 && await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 0, "读取滚轮能力")) is { Data: { } cap })
            w.Multiplier = cap[4];
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 1, "读取滚轮模式")) is { Data: { } m })
        {
            w.Diverted = (m[4] & 0x01) != 0;
            w.HiRes = (m[4] & 0x02) != 0;
            w.Inverted = (m[4] & 0x04) != 0;
            ds.Wheel = w;
        }
    }

    private async Task ReadFnSwap(string deviceKey, LogitechState st, HidppDeviceState ds)
    {
        var dev = (byte)ds.Index;
        Hidpp.Reply? r = null;
        int at = 4;
        if (Feat(deviceKey, dev, Feature.NewFnInversion) is { } f2) r = await TransactAsync(deviceKey, st, Hidpp.Call(dev, f2, 0, "读取 Fn 反转"));
        else if (Feat(deviceKey, dev, Feature.K375sFnInversion) is { } f3) { r = await TransactAsync(deviceKey, st, Hidpp.Call(dev, f3, 0, "读取 Fn 反转", 0xFF)); at = 5; }
        else if (Feat(deviceKey, dev, Feature.FnInversion) is { } f1) r = await TransactAsync(deviceKey, st, Hidpp.Call(dev, f1, 0, "读取 Fn 反转"));
        ds.FnSwap = r?.Data is { } d ? (d[at] & 0x01) != 0 : null;
    }

    private async Task ReadOnboard(string deviceKey, LogitechState st, HidppDeviceState ds, byte f)
    {
        var dev = (byte)ds.Index;
        var ob = ds.Onboard ?? new OnboardState();
        // libratbag：0x00 描述（profile_count 在第 4 个参数）、0x20 getMode、0x40 getCurrentProfile。
        if (ob.ProfileCount == 0 && await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 0, "读取板载配置描述")) is { Data: { } d })
            ob.ProfileCount = d[7];
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 2, "读取板载模式")) is not { Data: { } m }) return;
        ob.Onboard = m[4] == 1;
        if (await TransactAsync(deviceKey, st, Hidpp.Call(dev, f, 4, "读取当前配置")) is { Data: { } p }) ob.Profile = p[5];
        ds.Onboard = ob;
    }

    private byte? Feat(string deviceKey, byte dev, ushort id) =>
        _features.TryGetValue((deviceKey, dev), out var m) && m.TryGetValue(id, out var i) ? i : null;

    private static string NamesKey(string deviceKey) => "names:" + deviceKey;

    /// <summary>记下配对设备的名字：设备离线时接收器不一定能给出名称（nano / lightspeed 没有配对表）。</summary>
    private async Task SaveNamesAsync(string deviceKey, LogitechState st)
    {
        var names = st.Devices.Where(d => d.Name.Length > 0).ToDictionary(d => d.Index, d => d.Name);
        if (names.Count > 0) await _ctx.Storage.SetAsync(NamesKey(deviceKey), names);
        var ids = st.Devices.Where(d => d.Id.Length > 0).ToDictionary(d => d.Index, d => d.Id);
        if (ids.Count > 0) await _ctx.Storage.SetAsync(IdsKey(deviceKey), ids);
    }

    private static string IdsKey(string deviceKey) => "ids:" + deviceKey;

    /// <summary>定时刷新在线设备的电量，并重新 ping 离线设备（休眠的鼠标被唤醒后自动出现）。</summary>
    private void StartPoll(string deviceKey, LogitechState st)
    {
        var cts = new CancellationTokenSource();
        if (_pollers.TryRemove(deviceKey, out var old)) old.Cancel();
        _pollers[deviceKey] = cts;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Poll);
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (!st.Authorized || !st.Connected) continue;
                bool changed = false;
                foreach (var ds in st.Devices.ToList())
                {
                    if (ds.Online)
                    {
                        var before = ds.Battery;
                        var ping = await TransactAsync(deviceKey, st, Hidpp.Ping((byte)ds.Index));
                        if (ping.Data is null)
                        {
                            ds.Online = false; ds.Status = "设备未连接"; changed = true;
                            await OsdAsync(deviceKey, st, ds, OsdKind.Disconnected, "已断开连接");
                            continue;
                        }
                        await ReadBattery(deviceKey, st, ds);
                        changed |= ds.Battery?.Percent != before?.Percent || ds.Battery?.Charging != before?.Charging;
                        await BatteryEventsAsync(deviceKey, st, ds, before);
                    }
                    else if (st.IsReceiver && ds.Status != "未配对")
                    {
                        // 通知漏掉时的兜底：休眠唤醒的设备在这里被发现。
                        if (await ProbeAndRestoreAsync(deviceKey, st, ds)) { changed = true; await ConnectedOsdAsync(deviceKey, st, ds); }
                    }
                }
                if (changed) await Push(deviceKey, st);
            }
        }, cts.Token);
    }

    // —— 配对 / 取消配对（Unifying：短寄存器 0xB2 = [动作, 槽位, 超时秒]，动作 1 打开窗口 / 2 关闭 / 3 取消配对；Solaar receiver.py）——

    private const byte PairingTimeoutS = 30;
    private readonly ConcurrentDictionary<string, long> _pairingStamp = new();

    private async Task PairingAsync(string deviceKey, LogitechState st, string method, JsonElement a)
    {
        switch (method)
        {
            case "StartPairing":
            {
                var r = await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.SetRegister, Hidpp.RegPairing, "打开配对", 0x01, 0x00, PairingTimeoutS));
                if (r.Data is null) { st.PairingStatus = PairingError(r, "打开配对"); return; }
                st.PairingOpen = true;
                st.PairingStatus = $"请在 {PairingTimeoutS} 秒内打开要配对的设备；已开机的设备先关再开";
                // 接收器窗口到时会发 0x4A；万一没收到，稍后自己收起，免得页面一直停在"等待中"
                long stamp = Environment.TickCount64;
                _pairingStamp[deviceKey] = stamp;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(PairingTimeoutS + 5));
                    if (!st.PairingOpen || _pairingStamp.GetValueOrDefault(deviceKey) != stamp) return;
                    st.PairingOpen = false;
                    st.PairingStatus = "没有发现新设备，配对已结束";
                    await Push(deviceKey, st);
                });
                return;
            }
            case "StopPairing":
            {
                var r = await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.SetRegister, Hidpp.RegPairing, "关闭配对", 0x02, 0x00, 0x00));
                if (r.Data is null) { st.PairingStatus = PairingError(r, "关闭配对"); return; }
                st.PairingOpen = false;
                st.PairingStatus = "已取消配对";
                return;
            }
            case "Unpair":
            {
                int slot = Int(a, "slot", 0);
                var ds = st.Devices.FirstOrDefault(d => d.Index == slot);
                if (ds is null) { st.PairingStatus = "接收器上没有这个设备"; return; }
                var r = await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.SetRegister, Hidpp.RegPairing, "取消配对", 0x03, (byte)slot, 0x00));
                if (r.Data is null) { st.PairingStatus = PairingError(r, "取消配对"); return; }

                st.Devices.Remove(ds);
                _features.TryRemove((deviceKey, slot), out _);
                // 槽位缓存的名字 / Id 一并清掉，免得新设备配到这个槽位时沿用旧名字
                var names = await _ctx.Storage.GetAsync<Dictionary<int, string>>(NamesKey(deviceKey)) ?? new();
                var ids = await _ctx.Storage.GetAsync<Dictionary<int, string>>(IdsKey(deviceKey)) ?? new();
                if (names.Remove(slot)) await _ctx.Storage.SetAsync(NamesKey(deviceKey), names);
                if (ids.Remove(slot)) await _ctx.Storage.SetAsync(IdsKey(deviceKey), ids);
                if (ds.Id.Length > 0) await _ctx.ForgetChildAsync(deviceKey, ds.Id);
                st.PairingStatus = $"已取消配对：{ChildName(ds)}";
                return;
            }
        }
    }

    private static string PairingError(Hidpp.Reply r, string what) =>
        r.Error is { } e ? $"{what}失败：{(r.Is10Error ? Hidpp.Error10Text(e) : Hidpp.Error20Text(e))}"
        : r.Failed ? $"{what}失败" : $"{what}：接收器无响应";

    /// <summary>0x4A 配对窗口关闭时的错误码（Solaar hidpp10_constants.PairingError）。</summary>
    private static string PairingResult(int error, string current) => error switch
    {
        0 => current.StartsWith("已配对", StringComparison.Ordinal) ? current : "配对已结束",
        1 => "没有发现设备：请确认设备已开机、靠近接收器",
        2 => "这个设备不支持 Unifying",
        3 => "接收器的配对槽位已满，请先取消配对一个设备",
        6 => "配对超时",
        _ => $"配对失败（错误 {error}）",
    };

    // —— 通知：配对设备连接 / 断开（接收器 0x41）、配对窗口（0x4A）、电量变化（0x1000 / 0x1004 事件）——

    /// <summary>
    /// 后台监听设备主动发来的报告。每轮只占用设备锁取走 HID 驱动里已缓存的报告（每个集合等 1ms），
    /// 然后在锁外歇 200ms：报告在驱动的缓冲区里不会丢，一问一答也不用排在长时间的等待后面。
    /// 一问一答期间读到的通知放进收件箱，在这里统一处理。
    /// </summary>
    private const int ListenInterval = 200;

    private void StartListener(string deviceKey, LogitechState st)
    {
        var cts = new CancellationTokenSource();
        if (_listeners.TryRemove(deviceKey, out var old)) old.Cancel();
        _listeners[deviceKey] = cts;
        var inbox = _inbox.GetOrAdd(deviceKey, _ => new());
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!st.Authorized || !st.Connected) { await Task.Delay(1000, cts.Token); continue; }
                    var link = _links.GetValueOrDefault(deviceKey) ?? new Link(false, 0);
                    string[] roles = link.HasShort ? ["short", "long"] : ["long"];
                    bool failed = false;
                    var gate = _locks.GetOrAdd(deviceKey, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        foreach (var role in roles)
                            for (int i = 0; i < 16; i++)
                            {
                                var r = await _ctx.Device.ReadInputAsync(deviceKey, 1, role);
                                if (r.Status == DeviceIoStatus.Ok && r.Data is { } d) { inbox.Enqueue(d); continue; }
                                failed |= r.Status is not (DeviceIoStatus.Ok or DeviceIoStatus.Timeout);
                                break;
                            }
                    }
                    finally { gate.Release(); }

                    while (inbox.TryDequeue(out var rep))
                        try { await OnReportAsync(deviceKey, st, rep); }
                        catch (Exception) { /* 单条通知处理失败不影响监听 */ }
                    await Task.Delay(failed ? 1000 : ListenInterval, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    private async Task OnReportAsync(string deviceKey, LogitechState st, byte[] rep)
    {
        if (rep.Length < 7 || rep[0] is not (Hidpp.ShortId or Hidpp.LongId)) return;
        var ds = st.Devices.FirstOrDefault(d => d.Index == rep[1]);

        if (st.IsReceiver && rep[1] == Hidpp.Receiver && rep[2] == Hidpp.PairingLock)
        {
            bool open = (rep[3] & 0x01) != 0;
            if (open == st.PairingOpen && open) return;
            st.PairingOpen = open;
            if (!open) st.PairingStatus = PairingResult(rep[4], st.PairingStatus);
            await Push(deviceKey, st);
            return;
        }

        // 0x41 连接通知：[10 槽位 41 协议 标志 wpid-lo wpid-hi]，标志位 0x40 = 链路断开（Solaar notifications.py）。
        if (st.IsReceiver && rep[2] == Hidpp.DeviceConnection)
        {
            bool lost = (rep[4] & 0x40) != 0;
            if (ds is null)
            {
                if (lost) return;
                await RefreshAsync(deviceKey, st); // 新配对的设备
                if (st.Devices.FirstOrDefault(d => d.Index == rep[1]) is not { } added) return;
                st.PairingStatus = $"已配对：{ChildName(added)}";
                await Push(deviceKey, st);
                if (added.Online) await ConnectedOsdAsync(deviceKey, st, added);
                return;
            }
            if (lost)
            {
                if (!ds.Online) return;
                ds.Online = false;
                ds.Status = "设备未连接";
                await Push(deviceKey, st);
                await OsdAsync(deviceKey, st, ds, OsdKind.Disconnected, "已断开连接");
                return;
            }
            if (ds.Online) return;
            await Task.Delay(300); // 刚连上的设备立刻 ping 常常没响应
            if (!await ProbeAndRestoreAsync(deviceKey, st, ds)) return;
            await Push(deviceKey, st);
            await ConnectedOsdAsync(deviceKey, st, ds);
            return;
        }

        // HID++ 2.0 事件（software id = 0）：电池 Feature 的电量 / 充电状态变化。
        if (ds is { Online: true } && (rep[3] & 0x0F) == 0 &&
            (rep[2] == Feat(deviceKey, rep[1], Feature.UnifiedBattery) || rep[2] == Feat(deviceKey, rep[1], Feature.BatteryStatus)))
        {
            var before = ds.Battery;
            await ReadBattery(deviceKey, st, ds);
            await Push(deviceKey, st);
            await BatteryEventsAsync(deviceKey, st, ds, before);
        }
    }

    private const int LowBattery = 15, CriticalBattery = 5;

    /// <summary>按电量前后变化弹提示：跌破 15% / 5%、开始充电、充满。首次读到电量时不提示。</summary>
    private async Task BatteryEventsAsync(string deviceKey, LogitechState st, HidppDeviceState ds, BatteryState? before)
    {
        if (before is null || ds.Battery is not { } now) return;
        if (now.Full && !before.Full)
            await OsdAsync(deviceKey, st, ds, OsdKind.Charged, "电池已充满", 100);
        else if (now.Charging && !before.Charging)
            await OsdAsync(deviceKey, st, ds, OsdKind.Charging, now.Percent is { } cp ? $"正在充电 · {cp}%" : "正在充电", now.Percent);
        else if (!now.Charging && now.Percent is { } p && before.Percent is { } bp)
        {
            if (p < CriticalBattery && bp >= CriticalBattery)
                await OsdAsync(deviceKey, st, ds, OsdKind.BatteryLow, $"电量严重不足（{p}%），请立即充电", p);
            else if (p < LowBattery && bp >= LowBattery)
                await OsdAsync(deviceKey, st, ds, OsdKind.BatteryLow, $"电量低（{p}%），请及时充电", p);
        }
    }

    private Task ConnectedOsdAsync(string deviceKey, LogitechState st, HidppDeviceState ds) =>
        OsdAsync(deviceKey, st, ds, OsdKind.Connected,
            ds.Battery?.Percent is { } p ? $"已连接 · 电量 {p}%" : "已连接", ds.Battery?.Percent, KindGlyph(ds.Kind));

    /// <summary>屏幕提示发到设备自己的键上（配对设备是子设备键），核心据此显示用户给它起的名字。</summary>
    private Task OsdAsync(string deviceKey, LogitechState st, HidppDeviceState ds, OsdKind kind, string text, int? percent = null, string? glyph = null)
    {
        if (st.IsReceiver && ds.Id.Length == 0) return Task.CompletedTask;
        return _ctx.Notify.ShowOsdAsync(KeyOf(deviceKey, st, ds),
            new OsdContent { Kind = kind, Text = text, Percent = percent, Glyph = glyph });
    }

    /// <summary>Segoe Fluent Icons：鼠标 / 键盘 / 耳机 / 手柄 …，其他用默认图标。</summary>
    private static string? KindGlyph(string kind) => kind switch
    {
        "mouse" or "trackball" or "touchpad" => "",
        "keyboard" or "numpad" => "",
        "headset" => "",
        "gamepad" or "joystick" or "wheel" => "",
        _ => null,
    };

    // —— 传输 ——

    /// <summary>写一帧并等回包；成功返回 true，失败时把原因写到设备状态。</summary>
    private async Task<bool> Write(string deviceKey, LogitechState st, HidppDeviceState ds, Hidpp.Request req)
    {
        if (!st.Authorized) { st.Status = "未授权"; return false; }
        var r = await TransactAsync(deviceKey, st, req);
        if (r.Data is not null) { ds.Status = "已写入设备"; return true; }
        if (r.Error is { } e) ds.Status = $"{req.Note}失败：{(r.Is10Error ? Hidpp.Error10Text(e) : Hidpp.Error20Text(e))}";
        else if (!r.Failed) ds.Status = $"{req.Note}：设备无响应";
        return false;
    }

    /// <summary>只写不等回包（切换主机后设备直接断开）。</summary>
    private async Task<bool> WriteOnly(string deviceKey, LogitechState st, Hidpp.Request req)
    {
        if (!st.Authorized) { st.Status = "未授权"; return false; }
        var gate = _locks.GetOrAdd(deviceKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var w = await _ctx.Device.WriteOutputAsync(deviceKey, req.Data, "long");
            if (w.Status == DeviceIoStatus.Ok) return true;
            Fail(st, w.Status, req.Note);
            return false;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// 一问一答：先清空两个集合里积压的通知，按报告类型写到对应集合，再在两个集合上轮流读，直到收到匹配的回包或错误。
    /// （短请求的 2.0 回包、1.0 长寄存器回包走长报告集合，1.0 错误走短报告集合。）同一设备串行。
    /// </summary>
    private async Task<Hidpp.Reply> TransactAsync(string deviceKey, LogitechState st, Hidpp.Request req)
    {
        var link = _links.GetValueOrDefault(deviceKey) ?? new Link(false, 0);
        var gate = _locks.GetOrAdd(deviceKey, _ => new SemaphoreSlim(1, 1));
        string[] roles = link.HasShort ? ["long", "short"] : ["long"];
        await gate.WaitAsync();
        try
        {
            var inbox = _inbox.GetOrAdd(deviceKey, _ => new());
            foreach (var role in roles)
                for (int i = 0; i < 32; i++)
                {
                    var pending = await _ctx.Device.ReadInputAsync(deviceKey, 1, role);
                    if (pending.Status != DeviceIoStatus.Ok) break;
                    if (pending.Data is { } p && IsNotification(p)) inbox.Enqueue(p);
                }

            var data = req.Data;
            string writeRole = "long";
            if (data[0] == Hidpp.ShortId)
            {
                if (link.HasShort) writeRole = "short";
                else { var d = new byte[Hidpp.LongLength]; data.CopyTo(d, 0); d[0] = Hidpp.LongId; data = d; }
            }
            var w = await _ctx.Device.WriteOutputAsync(deviceKey, data, writeRole);
            if (w.Status != DeviceIoStatus.Ok) { Fail(st, w.Status, req.Note); return Hidpp.Reply.Fail; }

            var deadline = Environment.TickCount64 + ReplyTimeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                foreach (var role in roles)
                {
                    var r = await _ctx.Device.ReadInputAsync(deviceKey, roles.Length > 1 ? 15 : 50, role);
                    if (r.Status == DeviceIoStatus.Timeout) continue;
                    if (r.Status != DeviceIoStatus.Ok) { Fail(st, r.Status, req.Note); return Hidpp.Reply.Fail; }
                    if (r.Data is not { Length: >= 7 } rep) continue;
                    if (req.ErrorCode(rep) is { } code) return new Hidpp.Reply(null, code, rep[2] == Hidpp.Error10, false);
                    if (req.IsReply(rep)) return new Hidpp.Reply(rep, null, false, false);
                    if (IsNotification(rep)) inbox.Enqueue(rep);
                }
            }
            return Hidpp.Reply.Timeout;
        }
        finally { gate.Release(); }
    }

    /// <summary>0x41 连接 / 0x4A 配对窗口通知，或 software id 为 0 的 2.0 事件（其他软件 / 本插件的回包不算）。</summary>
    private static bool IsNotification(byte[] r) =>
        r.Length >= 7 && (r[2] is Hidpp.DeviceConnection or Hidpp.PairingLock || (r[0] == Hidpp.LongId && r[2] != Hidpp.Error20 && (r[3] & 0x0F) == 0));

    private static void Fail(LogitechState st, DeviceIoStatus s, string what)
    {
        st.Status = s switch
        {
            DeviceIoStatus.AccessRevoked => "授权已被撤销",
            DeviceIoStatus.AccessDenied => "未授权",
            DeviceIoStatus.DeviceGone => "设备已断开",
            _ => $"{what}失败：{s}",
        };
        if (s is DeviceIoStatus.AccessRevoked or DeviceIoStatus.AccessDenied) st.Authorized = false;
        if (s is DeviceIoStatus.DeviceGone) st.Connected = false;
    }

    /// <summary>推送接收器 / 直连设备的状态；接收器还要推给每个子设备页，并同步子设备列表。</summary>
    private async Task Push(string deviceKey, LogitechState st)
    {
        await _ctx.PushStateAsync(deviceKey, Json(st));
        if (!st.IsReceiver) return;
        foreach (var ds in st.Devices.Where(d => d.Id.Length > 0).ToList())
            await _ctx.PushStateAsync(DeviceKeys.Child(deviceKey, ds.Id), Json(ChildView(st, ds)));
        await PublishAsync(deviceKey, st);
    }

    private static int Int(JsonElement a, string p, int dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : dflt;
    private static bool Bool(JsonElement a, string p, bool dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : dflt;
    private static string Json(LogitechState s) => JsonSerializer.Serialize(s, J);
}
