using System.Collections.Concurrent;
using System.Text.Json;
using DeviceHub.Contracts;
using DeviceHub.Sdk;

namespace DeviceHub.Plugin.GameSir;

/// <summary>
/// GameSir 手柄插件后端。协议取自 GameSir Connect 官方 DLL 的收发日志（见 <see cref="GameSirProtocol"/>）；
/// 设备 I/O 经核心 Broker 代理（授权后），只下发官方软件发过、已核实含义的帧。
/// </summary>
public sealed class GameSirPlugin : IDevicePlugin
{
    public int SchemaVersion => 1;

    private const int ReplyTimeoutMs = 600;
    private static readonly TimeSpan BatteryPoll = TimeSpan.FromSeconds(60);

    private IPluginContext _ctx = null!;
    private readonly ConcurrentDictionary<string, GameSirState> _states = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollers = new();
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Task InitializeAsync(IPluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        return Task.CompletedTask;
    }

    public async Task AttachAsync(DeviceInfo device, CancellationToken ct)
    {
        var st = _states.GetOrAdd(device.DeviceKey, _ => new GameSirState());
        st.Wireless = device.Pid != 0x100F;
        st.CanWriteVibration = GameSirProtocol.WriteVibration(0, 0).Verified;
        st.CanWriteSleep = GameSirProtocol.WriteSleep(10, true).Verified;

        var scope = await _ctx.Device.RequestAccessAsync(device.DeviceKey, "读取电量与手柄设置");
        st.Authorized = scope != AccessScope.Denied;
        st.Connected = true;
        st.Status = st.Authorized ? "" : "用户拒绝授权";
        await Push(device.DeviceKey, st);
        if (!st.Authorized) return;

        await RefreshAsync(device.DeviceKey, st);
        StartBatteryPoll(device.DeviceKey, st);
    }

    public Task<DeviceDescription?> DescribeAsync(string deviceKey)
    {
        if (!_states.TryGetValue(deviceKey, out var st)) return Task.FromResult<DeviceDescription?>(null);
        var link = st.Wireless ? "2.4G 接收器（USB 3537:1098）" : "有线（USB 3537:100F）";
        return Task.FromResult<DeviceDescription?>(new DeviceDescription
        {
            Glyph = "", // 手柄
            Subtitle = st.Battery is { } b ? $"{(st.Wireless ? "2.4G" : "有线")} · 电量 {b}%" : (st.Wireless ? "2.4G 接收器" : "有线"),
            Details = new DeviceDetailItem[]
            {
                new("连接方式", link),
                new("配置通道", "接口 2 · 厂商自定义 0xFF7A · 64 字节输入/输出报告"),
                new("固件", st.Firmware.Length > 0 ? st.Firmware : "未读取"),
                new("MAC", st.Mac.Length > 0 ? st.Mac : "未读取"),
                new("最近读取", st.LastReadAt is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未读取"),
                new("已支持", "电量、固件、震动强度与自动休眠（读写）、扳机/摇杆死区（只读）"),
                new("尚未支持", "修改死区、改键、宏、灯光、固件升级"),
                new("注意", "GameSir Connect 同时运行时两边都会收发配置通道，建议先退出官方软件"),
            },
        });
    }

    public Task DetachAsync(string deviceKey)
    {
        if (_pollers.TryRemove(deviceKey, out var cts)) cts.Cancel();
        if (_states.TryGetValue(deviceKey, out var s)) { s.Connected = false; s.Status = "设备已移除"; }
        return Task.CompletedTask;
    }

    public async Task<string> InvokeAsync(string deviceKey, string method, string argsJson, CancellationToken ct)
    {
        var st = _states.GetOrAdd(deviceKey, _ => new GameSirState());
        var a = string.IsNullOrWhiteSpace(argsJson) ? default : JsonDocument.Parse(argsJson).RootElement;

        switch (method)
        {
            case "Refresh":
                await RefreshAsync(deviceKey, st);
                break;

            case "SetVibration":
            {
                int l = Math.Clamp(Int(a, "left", st.VibrationLeft ?? 3), 0, 4);
                int r = Math.Clamp(Int(a, "right", st.VibrationRight ?? 3), 0, 4);
                if (await SendAsync(deviceKey, st, GameSirProtocol.WriteVibration(l, r))) { st.VibrationLeft = l; st.VibrationRight = r; }
                break;
            }

            case "SetSleep":
            {
                int m = Math.Clamp(Int(a, "minutes", st.SleepMinutes ?? 10), 1, 255);
                bool auto = Bool(a, "auto", st.AutoSleep ?? true);
                if (await SendAsync(deviceKey, st, GameSirProtocol.WriteSleep(m, auto))) { st.SleepMinutes = m; st.AutoSleep = auto; }
                break;
            }
        }

        await Push(deviceKey, st);
        return Json(st);
    }

    public Task<string> GetStateAsync(string deviceKey)
        => Task.FromResult(Json(_states.GetOrAdd(deviceKey, _ => new GameSirState())));

    public Task PrepareShutdownAsync()
    {
        foreach (var cts in _pollers.Values) cts.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>读一遍所有已核实的读命令（与官方软件绑定设备时的顺序一致）。</summary>
    private async Task RefreshAsync(string deviceKey, GameSirState st)
    {
        if (!st.Authorized) { st.Status = "未授权"; return; }
        st.Status = "";

        if (await TransactAsync(deviceKey, st, GameSirProtocol.ReadDeviceInfo()) is { } info)
            (st.Mac, st.Firmware, st.Battery) = GameSirProtocol.ParseDeviceInfo(info);

        if (await TransactAsync(deviceKey, st, GameSirProtocol.ReadVibration()) is { } vib)
        {
            st.VibrationLeft = GameSirProtocol.ValueToLevel(vib[4]);
            st.VibrationRight = GameSirProtocol.ValueToLevel(vib[8]);
        }

        if (await TransactAsync(deviceKey, st, GameSirProtocol.ReadSleep()) is { } sl)
        {
            st.SleepMinutes = sl[3];
            st.AutoSleep = sl[4] != 0;
        }

        st.L2.Begin = await ReadTrigger(GameSirProtocol.L2Begin) ?? st.L2.Begin;
        st.L2.End = await ReadTrigger(GameSirProtocol.L2End) ?? st.L2.End;
        st.R2.Begin = await ReadTrigger(GameSirProtocol.R2Begin) ?? st.R2.Begin;
        st.R2.End = await ReadTrigger(GameSirProtocol.R2End) ?? st.R2.End;

        for (int stick = 0; stick < 2; stick++)
        {
            if (await TransactAsync(deviceKey, st, GameSirProtocol.SelectStick(stick)) is null) continue;
            if (await TransactAsync(deviceKey, st, GameSirProtocol.ReadStickCurve(stick)) is { } c)
                (stick == 0 ? st.LeftStick : st.RightStick).Begin = c[4];
        }

        if (st.Status.Length == 0) { st.Status = "已从手柄读取"; st.LastReadAt = DateTimeOffset.Now; }
        await Push(deviceKey, st);

        async Task<int?> ReadTrigger(byte p) =>
            await TransactAsync(deviceKey, st, GameSirProtocol.ReadTriggerParam(p)) is { } r ? r[4] : null;
    }

    private void StartBatteryPoll(string deviceKey, GameSirState st)
    {
        var cts = new CancellationTokenSource();
        if (_pollers.TryRemove(deviceKey, out var old)) old.Cancel();
        _pollers[deviceKey] = cts;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(BatteryPoll);
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (await TransactAsync(deviceKey, st, GameSirProtocol.ReadDeviceInfo()) is not { } info) continue;
                var battery = GameSirProtocol.ParseDeviceInfo(info).Battery;
                if (battery == st.Battery) continue;
                st.Battery = battery;
                await Push(deviceKey, st);
            }
        }, cts.Token);
    }

    /// <summary>写一帧并返回是否成功（未核实的帧不下发）。</summary>
    private async Task<bool> SendAsync(string deviceKey, GameSirState st, GameSirProtocol.Frame frame)
    {
        if (!st.Authorized) { st.Status = "未授权"; return false; }
        // 安全约束：只下发从官方软件抓到、已核实含义的帧。
        if (!frame.Verified) { st.Status = $"未下发：{frame.Note}"; return false; }
        if (await TransactAsync(deviceKey, st, frame) is null) return false;
        st.Status = "已同步到手柄";
        return true;
    }

    /// <summary>
    /// 一问一答：先清空积压的输入（多是 F4 状态推送），写命令，再读到第一个匹配的回包。
    /// 同一设备串行，避免两条命令的回包交错。
    /// </summary>
    private async Task<byte[]?> TransactAsync(string deviceKey, GameSirState st, GameSirProtocol.Frame frame)
    {
        var gate = _locks.GetOrAdd(deviceKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            for (int i = 0; i < 64; i++)
                if ((await _ctx.Device.ReadInputAsync(deviceKey, 1)).Status != DeviceIoStatus.Ok) break;

            var w = await _ctx.Device.WriteOutputAsync(deviceKey, frame.Data);
            if (w.Status != DeviceIoStatus.Ok) { Fail(st, w.Status, frame.Note); return null; }

            var deadline = Environment.TickCount64 + ReplyTimeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var r = await _ctx.Device.ReadInputAsync(deviceKey, (int)Math.Max(1, deadline - Environment.TickCount64));
                if (r.Status == DeviceIoStatus.Timeout) break;
                if (r.Status != DeviceIoStatus.Ok) { Fail(st, r.Status, frame.Note); return null; }
                // 有 report id 的集合首字节是 id（0），去掉后与官方日志对齐。
                var data = r.Data is { Length: 65 } d ? d[1..] : r.Data ?? [];
                if (data.Length > 0 && data[0] != GameSirProtocol.StatePush && frame.IsReply(data)) return data;
            }
            st.Status = $"{frame.Note}：手柄无响应（手柄是否已开机并连上接收器？）";
            return null;
        }
        finally { gate.Release(); }
    }

    private static void Fail(GameSirState st, DeviceIoStatus s, string what)
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

    private Task Push(string deviceKey, GameSirState st) => _ctx.PushStateAsync(deviceKey, Json(st));

    private static int Int(JsonElement a, string p, int dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : dflt;
    private static bool Bool(JsonElement a, string p, bool dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : dflt;
    private static string Json(GameSirState s) => JsonSerializer.Serialize(s, J);
}
