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
    }

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
        return Task.FromResult<DeviceDescription?>(new DeviceDescription { Title = title, Subtitle = subtitle, Details = details });
    }

    public Task DetachAsync(string deviceKey)
    {
        if (_pollers.TryRemove(deviceKey, out var cts)) cts.Cancel();
        if (_states.TryGetValue(deviceKey, out var s)) { s.Connected = false; s.Status = "设备已移除"; }
        return Task.CompletedTask;
    }

    public async Task<string> InvokeAsync(string deviceKey, string method, string argsJson, CancellationToken ct)
    {
        var st = _states.GetOrAdd(deviceKey, _ => new LogitechState());
        var a = string.IsNullOrWhiteSpace(argsJson) ? default : JsonDocument.Parse(argsJson).RootElement;

        if (method == "Refresh")
        {
            await RefreshAsync(deviceKey, st);
            return Json(st);
        }

        var ds = st.Devices.FirstOrDefault(d => d.Index == Int(a, "index", st.Devices.FirstOrDefault()?.Index ?? Hidpp.Receiver));
        if (ds is null) { st.Status = "设备不存在"; return Json(st); }
        if (!ds.Online) { ds.Status = "设备未连接，无法写入"; await Push(deviceKey, st); return Json(st); }
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

        await Push(deviceKey, st);
        return Json(st);
    }

    public Task<string> GetStateAsync(string deviceKey)
        => Task.FromResult(Json(_states.GetOrAdd(deviceKey, _ => new LogitechState())));

    public Task PrepareShutdownAsync()
    {
        foreach (var cts in _pollers.Values) cts.Cancel();
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
            await ProbeDeviceAsync(deviceKey, st, ds);
            if (ds.Name.Length == 0) ds.Name = st.ProductName;
        }
        else if (ping.Error is not null || ping.Data is not null)
        {
            st.IsReceiver = true;
            await ReadReceiverAsync(deviceKey, st);
            foreach (var ds in st.Devices) await ProbeDeviceAsync(deviceKey, st, ds);
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

    /// <summary>接收器固件（寄存器 0xF1）与配对表（长寄存器 0xB5）。</summary>
    private async Task ReadReceiverAsync(string deviceKey, LogitechState st)
    {
        var fw = await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.GetRegister, Hidpp.RegReceiverFirmware, "读取接收器固件", 0x01));
        var build = await TransactAsync(deviceKey, st, Hidpp.Register(Hidpp.Receiver, Hidpp.GetRegister, Hidpp.RegReceiverFirmware, "读取接收器固件", 0x02));
        if (fw.Data is { } f)
            st.ReceiverFirmware = $"{f[5]:X2}.{f[6]:X2}" + (build.Data is { } b ? $".B{b[5]:X2}{b[6]:X2}" : "");

        var cached = await _ctx.Storage.GetAsync<Dictionary<int, string>>(NamesKey(deviceKey)) ?? new();
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
            }
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
            ds.Battery = new BatteryState { Percent = pct, Estimated = u[4] == 0, Charging = u[6] is 1 or 2, Status = HidppParse.UnifiedCharging(u[6]) };
        }
        else if (Feat(deviceKey, dev, Feature.BatteryStatus) is { } fb &&
                 await TransactAsync(deviceKey, st, Hidpp.Call(dev, fb, 0, "读取电量")) is { Data: { } b })
            ds.Battery = new BatteryState { Percent = b[4], Charging = b[6] is 1 or 2 or 4, Status = HidppParse.BatteryStatus(b[6]) };
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
    }

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
                        var before = ds.Battery?.Percent;
                        var ping = await TransactAsync(deviceKey, st, Hidpp.Ping((byte)ds.Index));
                        if (ping.Data is null) { ds.Online = false; ds.Status = "设备未连接"; changed = true; continue; }
                        await ReadBattery(deviceKey, st, ds);
                        changed |= ds.Battery?.Percent != before;
                    }
                    else if (st.IsReceiver && ds.Status != "未配对")
                    {
                        await ProbeDeviceAsync(deviceKey, st, ds);
                        changed |= ds.Online;
                    }
                }
                if (changed) await Push(deviceKey, st);
            }
        }, cts.Token);
    }

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
            foreach (var role in roles)
                for (int i = 0; i < 32; i++)
                    if ((await _ctx.Device.ReadInputAsync(deviceKey, 1, role)).Status != DeviceIoStatus.Ok) break;

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
                }
            }
            return Hidpp.Reply.Timeout;
        }
        finally { gate.Release(); }
    }

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

    private Task Push(string deviceKey, LogitechState st) => _ctx.PushStateAsync(deviceKey, Json(st));

    private static int Int(JsonElement a, string p, int dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : dflt;
    private static bool Bool(JsonElement a, string p, bool dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : dflt;
    private static string Json(LogitechState s) => JsonSerializer.Serialize(s, J);
}
