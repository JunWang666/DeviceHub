using System.Collections.Concurrent;
using System.Text.Json;
using DeviceHub.Contracts;
using DeviceHub.Sdk;

namespace DeviceHub.Plugin.AirPods;

/// <summary>
/// AirPods 插件后端。参考三个开源项目：
/// <list type="bullet">
/// <item>AirPodsDesktop（SpriteOvO）：BLE 接近配对广播解析、左右耳广播合并 → 电量 / 充电 / 入耳 / 开盖，免驱动。</item>
/// <item>PodBridge（bhemsen）：连接门控、入耳位异或修正、AAP 降噪帧与“先握手再订阅”的启动顺序。</item>
/// <item>NTPods（arctumn）：AAP 控制命令表、电量 / 入耳 / 设备信息推送的解析。</item>
/// </list>
/// 两档能力：continuity 通道（核心的 BLE 扫描，免驱动）提供状态；aap 通道（用户自行安装的第三方 L2CAP 驱动）
/// 提供 1% 精度电量、降噪模式与对话感知。没有驱动时高级功能明确显示为不可用，不发任何帧。
/// </summary>
public sealed class AirPodsPlugin : IDevicePlugin
{
    public int SchemaVersion => 1;

    private IPluginContext _ctx = null!;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Session
    {
        public required string Key;
        public readonly AirPodsState State = new();
        public readonly ProximityTracker Tracker = new();
        public readonly CancellationTokenSource Stop = new();
        public string LastPushed = "";
        public DateTimeOffset LastPushedAt;
        /// <summary>AAP 电量最近一次更新；新鲜时优先于 BLE 广播的 10% 精度电量。</summary>
        public DateTimeOffset? AapBatteryAt;
    }

    public Task InitializeAsync(IPluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        return Task.CompletedTask;
    }

    public async Task AttachAsync(DeviceInfo device, CancellationToken ct)
    {
        var s = new Session { Key = device.DeviceKey };
        if (_sessions.TryRemove(device.DeviceKey, out var old)) old.Stop.Cancel();
        _sessions[device.DeviceKey] = s;
        lock (s.State)
        {
            s.State.ApplyModel(AirPodsModel.Find(device.Pid));
            s.State.Connected = device.Connected;
        }

        var scope = await _ctx.Device.RequestAccessAsync(device.DeviceKey, "读取电量与佩戴状态；装有 AAP 驱动时切换降噪模式");
        lock (s.State)
        {
            s.State.Authorized = scope != AccessScope.Denied;
            s.State.Status = s.State.Authorized ? "" : "用户拒绝授权";
        }
        await Push(s, force: true);
        if (!s.State.Authorized) return;

        _ = Task.Run(() => ContinuityLoop(s));
        _ = Task.Run(() => AapLoop(s));
    }

    public Task DetachAsync(string deviceKey)
    {
        if (_sessions.TryRemove(deviceKey, out var s))
        {
            s.Stop.Cancel();
            lock (s.State) { s.State.Connected = false; s.State.AapConnected = false; s.State.Status = "设备已移除"; }
            _ = Push(s);
        }
        return Task.CompletedTask;
    }

    // —— continuity：BLE 广播 ——

    private async Task ContinuityLoop(Session s)
    {
        var ct = s.Stop.Token;
        while (!ct.IsCancellationRequested)
        {
            var r = await _ctx.Device.ReadInputAsync(s.Key, 3000, "status");
            var now = DateTimeOffset.Now;
            switch (r.Status)
            {
                case DeviceIoStatus.Ok when r.Data is { } d && ContinuityParser.TryParse(d) is { } p:
                    if (s.Tracker.Add(p, now)) lock (s.State) { s.State.Rssi = r.Rssi; ApplyProximity(s, now); }
                    break;
                case DeviceIoStatus.Ok:
                case DeviceIoStatus.Timeout:
                    lock (s.State) ApplyProximity(s, now);
                    break;
                case DeviceIoStatus.DeviceGone:
                    s.Tracker.Reset();
                    lock (s.State) { s.State.Connected = false; s.State.LastSeen = null; ClearParts(s.State); s.State.BatterySource = ""; }
                    await Push(s);
                    await Delay(2000, ct);
                    continue;
                case DeviceIoStatus.AccessDenied or DeviceIoStatus.AccessRevoked:
                    lock (s.State) { s.State.Authorized = false; s.State.Status = "授权已被撤销"; }
                    await Push(s);
                    s.Stop.Cancel();
                    return;
                default:
                    lock (s.State) s.State.Status = r.Message ?? $"读取失败：{r.Status}";
                    await Push(s);
                    await Delay(5000, ct);
                    continue;
            }
            await Push(s);
        }
    }

    /// <summary>把合成后的广播状态写进 State（AAP 电量新鲜时只更新入耳/盒盖，不覆盖电量）。</summary>
    private static void ApplyProximity(Session s, DateTimeOffset now)
    {
        var st = s.State;
        st.Connected = true;
        if (st.Status == "设备已移除") st.Status = "";
        var p = s.Tracker.Current(now);
        st.LastSeen = s.Tracker.LastSeen;
        if (p is null)
        {
            if (s.Tracker.IsLost(now) && !AapBatteryFresh(s, now)) { ClearParts(st); st.BatterySource = ""; }
            return;
        }
        if (p.ModelId != 0 && p.ModelId != st.ModelId) st.ApplyModel(AirPodsModel.Find(p.ModelId));
        st.Left.InEar = p.LeftInEar; st.Right.InEar = p.RightInEar;
        st.BothInCase = p.BothInCase; st.LidOpen = p.LidOpen;
        if (AapBatteryFresh(s, now)) return;
        Set(st.Left, p.Left, p.LeftCharging);
        Set(st.Right, p.Right, p.RightCharging);
        Set(st.Case, p.Case, p.CaseCharging);
        st.BatterySource = "ble";
    }

    private static bool AapBatteryFresh(Session s, DateTimeOffset now) => s.State.AapConnected && s.AapBatteryAt is { } t && now - t < TimeSpan.FromMinutes(5);

    // —— aap：经驱动的控制通道 ——

    private async Task AapLoop(Session s)
    {
        var ct = s.Stop.Token;
        while (!ct.IsCancellationRequested)
        {
            // 每次（重新）连上都要先握手、解锁功能、订阅推送，AirPods 才会回应后续命令。
            var r = await SendAap(s, AapProtocol.Handshake());
            if (r.Status != DeviceIoStatus.Ok)
            {
                bool noDriver = r.Message?.Contains("驱动") == true;
                lock (s.State)
                {
                    s.State.AapConnected = false;
                    s.State.AapNote = noDriver ? "未安装 AAP 驱动" : r.Status == DeviceIoStatus.DeviceGone ? "耳机未连接" : r.Message ?? r.Status.ToString();
                }
                await Push(s);
                if (r.Status is DeviceIoStatus.AccessDenied or DeviceIoStatus.AccessRevoked) return;
                await Delay(noDriver ? 30_000 : 3000, ct);
                continue;
            }
            await SendAap(s, AapProtocol.SetSpecificFeatures());
            await SendAap(s, AapProtocol.RequestNotifications());
            lock (s.State) { s.State.AapConnected = true; s.State.AapNote = ""; }
            await Push(s);

            while (!ct.IsCancellationRequested)
            {
                var rx = await _ctx.Device.ReadInputAsync(s.Key, 2000, "aap");
                if (rx.Status == DeviceIoStatus.Timeout) continue;
                if (rx.Status != DeviceIoStatus.Ok || rx.Data is null) break; // 通道断开：回到外层重新握手
                lock (s.State) HandleAap(s, rx.Data);
                await Push(s);
            }
            lock (s.State) { s.State.AapConnected = false; s.State.AapNote = "AAP 通道已断开，正在重连"; }
            await Push(s);
            await Delay(2000, ct);
        }
    }

    private static void HandleAap(Session s, byte[] f)
    {
        var st = s.State;
        if (AapProtocol.ParseBattery(f) is { } b)
        {
            if (b.Headphone is not null) Set(st.Left, b.Headphone, b.HeadphoneCharging); // 头戴式只有一个电池，放在 Left 上显示
            else { Set(st.Left, b.Left, b.LeftCharging); Set(st.Right, b.Right, b.RightCharging); }
            Set(st.Case, b.Case, b.CaseCharging);
            st.BatterySource = "aap";
            s.AapBatteryAt = DateTimeOffset.Now;
        }
        else if (AapProtocol.ParseControl(f) is { } c)
        {
            if (c.Id == AapProtocol.ControlListeningMode && Enum.IsDefined(typeof(ListeningMode), c.Value)) st.ListeningMode = c.Value;
            else if (c.Id == AapProtocol.ControlConversationAwareness) st.ConversationAwareness = c.Value == 1;
        }
        else if (AapProtocol.ParseMetadata(f) is { } m)
        {
            st.Firmware = m.Firmware;
        }
    }

    private Task<DeviceIoResult> SendAap(Session s, byte[] frame) => _ctx.Device.WriteOutputAsync(s.Key, frame, "aap");

    // —— UI 调用 ——

    public async Task<string> InvokeAsync(string deviceKey, string method, string argsJson, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(deviceKey, out var s)) return "{}";
        var a = string.IsNullOrWhiteSpace(argsJson) ? default : JsonDocument.Parse(argsJson).RootElement;
        switch (method)
        {
            case "SetListeningMode":
            {
                var mode = (ListeningMode)Int(a, "mode", 0);
                string? reject;
                lock (s.State)
                    reject = !Enum.IsDefined(mode) ? "未知模式"
                        : !s.State.SupportsAnc ? "此型号不支持降噪"
                        : mode == ListeningMode.Adaptive && !s.State.SupportsAdaptive ? "此型号不支持自适应"
                        : !s.State.AapConnected ? "需要 AAP 驱动" : null;
                if (reject is not null) { lock (s.State) s.State.Status = reject; break; }
                var r = await SendAap(s, AapProtocol.SetListeningMode(mode));
                // 先按请求显示，AirPods 的回显（操作码 0x09）到达后以回显为准。
                lock (s.State) { if (r.Status == DeviceIoStatus.Ok) s.State.ListeningMode = (int)mode; s.State.Status = r.Status == DeviceIoStatus.Ok ? "" : $"切换失败：{r.Message ?? r.Status.ToString()}"; }
                break;
            }
            case "SetConversationAwareness":
            {
                var on = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("on", out var v) && v.ValueKind == JsonValueKind.True;
                bool ok;
                lock (s.State) ok = s.State.AapConnected && s.State.SupportsConversationAwareness;
                if (!ok) { lock (s.State) s.State.Status = "需要 AAP 驱动且型号支持对话感知"; break; }
                var r = await SendAap(s, AapProtocol.SetToggle(AapProtocol.ControlConversationAwareness, on));
                lock (s.State) { if (r.Status == DeviceIoStatus.Ok) s.State.ConversationAwareness = on; s.State.Status = r.Status == DeviceIoStatus.Ok ? "" : $"设置失败：{r.Message ?? r.Status.ToString()}"; }
                break;
            }
        }
        await Push(s, force: true);
        return Json(s);
    }

    public Task<string> GetStateAsync(string deviceKey) =>
        Task.FromResult(_sessions.TryGetValue(deviceKey, out var s) ? Json(s) : JsonSerializer.Serialize(new AirPodsState(), J));

    public Task<DeviceDescription?> DescribeAsync(string deviceKey)
    {
        if (!_sessions.TryGetValue(deviceKey, out var s)) return Task.FromResult<DeviceDescription?>(null);
        lock (s.State)
        {
            var st = s.State;
            var details = new List<DeviceDetailItem>
            {
                new("型号", st.ModelId == 0 ? st.ModelName : $"{st.ModelName}（0x{st.ModelId:X4}）"),
                new("连接", st.Connected ? "已连接" : "未连接"),
                new("电量", Battery(st) is { Length: > 0 } b ? $"{b}（{(st.BatterySource == "aap" ? "AAP，1% 精度" : "BLE 广播，10% 精度")}）" : "暂无"),
                new("高级功能", st.AapConnected ? "可用（AAP 通道已连接）" : st.AapNote is { Length: > 0 } n ? n : "不可用"),
            };
            if (st.Firmware.Length > 0) details.Add(new("固件", st.Firmware));
            if (st.Rssi is { } rssi) details.Add(new("广播信号", $"{rssi} dBm"));
            return Task.FromResult<DeviceDescription?>(new DeviceDescription
            {
                Subtitle = !st.Connected ? "未连接" : Battery(st) is { Length: > 0 } sub ? sub : "已连接 · 等待广播",
                Details = details,
            });
        }
    }

    private static string Battery(AirPodsState st)
    {
        string P(string n, PartState p) => p.Battery is { } v ? $"{n} {v}%{(p.Charging ? "⚡" : "")}" : "";
        var parts = st.HeadWorn
            ? new[] { P("电量", st.Left) }
            : new[] { P("左", st.Left), P("右", st.Right), P("盒", st.Case) };
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }

    public Task PrepareShutdownAsync()
    {
        foreach (var s in _sessions.Values) s.Stop.Cancel();
        return Task.CompletedTask;
    }

    // —— 工具 ——

    private static void Set(PartState p, int? battery, bool charging) { p.Battery = battery; p.Charging = battery is not null && charging; }

    private static void ClearParts(AirPodsState st)
    {
        foreach (var p in new[] { st.Left, st.Right, st.Case }) { p.Battery = null; p.Charging = false; p.InEar = false; }
        st.BothInCase = false; st.LidOpen = null;
    }

    /// <summary>
    /// 只在内容变化时推送：广播每秒好几条，大多不变。比较时忽略每条都会变的 LastSeen / Rssi，
    /// 但至少每 5 秒推一次，让界面上的“最近收到”保持新鲜。
    /// </summary>
    private Task Push(Session s, bool force = false)
    {
        string json, key;
        lock (s.State)
        {
            json = JsonSerializer.Serialize(s.State, J);
            var (seen, rssi) = (s.State.LastSeen, s.State.Rssi);
            s.State.LastSeen = null; s.State.Rssi = null;
            key = JsonSerializer.Serialize(s.State, J);
            s.State.LastSeen = seen; s.State.Rssi = rssi;
        }
        var now = DateTimeOffset.Now;
        if (!force && key == s.LastPushed && now - s.LastPushedAt < TimeSpan.FromSeconds(5)) return Task.CompletedTask;
        s.LastPushed = key;
        s.LastPushedAt = now;
        return _ctx.PushStateAsync(s.Key, json);
    }

    private static string Json(Session s) { lock (s.State) return JsonSerializer.Serialize(s.State, J); }

    private static async Task Delay(int ms, CancellationToken ct) { try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { } }

    private static int Int(JsonElement a, string p, int dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : dflt;
}
