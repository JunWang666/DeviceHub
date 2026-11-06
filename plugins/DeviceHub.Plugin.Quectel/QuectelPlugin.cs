using System.Collections.Concurrent;
using System.Text.Json;
using DeviceHub.Contracts;
using DeviceHub.Sdk;

namespace DeviceHub.Plugin.Quectel;

/// <summary>
/// Quectel 4G 模块插件后端（EG25-G 及大疆一代 4G 模块）。经核心代理的 AT 口（manifest 里的 "at" 串口通道）
/// 读取状态、管理频段/小区锁定、GPS、短信等；见 <see cref="ModuleSession"/>。
/// </summary>
public sealed class QuectelPlugin : IDevicePlugin
{
    public int SchemaVersion => 1;

    private IPluginContext _ctx = null!;
    private readonly ConcurrentDictionary<string, QuectelState> _states = new();
    private readonly ConcurrentDictionary<string, ModuleSession> _sessions = new();
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Task InitializeAsync(IPluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        return Task.CompletedTask;
    }

    public async Task AttachAsync(DeviceInfo device, CancellationToken ct)
    {
        var key = device.DeviceKey;
        var st = _states.GetOrAdd(key, _ => new QuectelState());
        st.Connected = true;
        var layout = ModuleLayout.From(device.Channels);
        st.Driver = new DriverInfo
        {
            AtPortReady = layout.AtRole is not null, AtPortName = layout.AtPort ?? "", Missing = layout.Missing.ToList(), LayoutShifted = layout.Shifted,
        };
        st.Module.AtPort = layout.AtPort is { } p
            ? $"{p}（USB 接口 {(layout.Shifted ? ModuleLayout.ShiftedAtInterface : ModuleLayout.AtInterface)}）"
            : "没有驱动";
        var scope = await _ctx.Device.RequestAccessAsync(key, "通过 AT 口读取 4G 模块的信号、SIM 与网络状态，并按你的操作修改频段、小区锁定、GPS 与短信");
        st.Authorized = scope != AccessScope.Denied;
        if (_sessions.TryRemove(key, out var old)) await old.DisposeAsync();

        if (!st.Driver.AtPortReady)
        {
            // 驱动没装：模块的 AT 口还不是 COM 口，没法通信。装好驱动后核心会让插件重新接管。
            st.PcLink = PcLink.Evaluate(layout, st, PcLink.Lookup(layout.NetGuid));
            st.Status = !st.Authorized ? "用户拒绝授权"
                : layout.Shifted ? "模块处于 RNDIS / MBIM 模式，驱动对不上，联系不上 AT 口（见页面顶部说明）"
                : "模块的 AT 口没有驱动，装好驱动后自动连接";
            await Push(key, st);
            return;
        }
        st.Status = st.Authorized ? "正在读取模块…" : "用户拒绝授权";
        await Push(key, st);
        if (!st.Authorized) return;

        var session = new ModuleSession(new BrokerTransport(_ctx.Device, key, layout.AtRole!), st, () => Push(key, st),
            k => _ctx.Storage.GetAsync<string>(k), (k, v) => _ctx.Storage.SetAsync(k, v)) { Layout = layout };
        session.Notice += (kind, text) => _ = _ctx.Notify.ShowOsdAsync(key, new OsdContent { Kind = kind, Text = text });
        _sessions[key] = session;
        _ = Task.Run(session.StartAsync, CancellationToken.None);
    }

    public async Task DetachAsync(string deviceKey)
    {
        if (_sessions.TryRemove(deviceKey, out var s)) await s.DisposeAsync();
        if (_states.TryGetValue(deviceKey, out var st))
        {
            st.Connected = false;
            st.Busy = false;
            st.Status = "模块已断开";
            await Push(deviceKey, st);
        }
    }

    public Task<DeviceDescription?> DescribeAsync(string deviceKey)
    {
        if (!_states.TryGetValue(deviceKey, out var st)) return Task.FromResult<DeviceDescription?>(null);
        var n = st.Network;
        var parts = new List<string>();
        if (n.Operator.Length > 0) parts.Add(n.Operator);
        if (n.Band.Length > 0) parts.Add(n.Band.Replace("LTE BAND ", "B"));
        if (st.Signal.Rsrp is { } p) parts.Add($"{p} dBm");
        return Task.FromResult<DeviceDescription?>(new DeviceDescription
        {
            Glyph = "", // 信号格
            // 核心只在设备列表变化时来取副标题（刚授权时模块还没读完），没数据时不给，免得侧边栏显示"—"。
            Subtitle = !st.Connected ? "未连接" : !st.Driver.AtPortReady ? (st.Driver.LayoutShifted ? "驱动与网卡模式不匹配" : "需要安装驱动") : parts.Count > 0 ? string.Join(" · ", parts)
                : n.EpsRegistration is { } reg ? AtParse.RegText(reg) : null,
            Details = new DeviceDetailItem[]
            {
                new("模块", $"{st.Module.Manufacturer} {st.Module.Model}".Trim() is { Length: > 0 } mm ? mm : "未读取"),
                new("固件", st.Module.Revision.Length > 0 ? st.Module.Revision : "未读取"),
                new("IMEI", st.Module.Imei.Length > 0 ? st.Module.Imei : "未读取"),
                new("USB 网卡模式", AtParse.UsbNetName(st.Module.UsbNet)),
                new("控制通道", st.Module.AtPort),
                new("驱动", st.Driver.Missing.Count == 0 ? "正常" : string.Join("；", st.Driver.Missing)),
                new("电脑联网", st.PcLink.Summary.Length > 0 ? st.PcLink.Summary : "未检查"),
                new("SIM", st.Sim.State.Length > 0 ? st.Sim.State : "未读取"),
                new("最近刷新", st.LastPollAt is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未读取"),
                new("已支持", "信号与小区信息、邻区、频段锁定、小区/频点锁定、网络制式、漫游、飞行模式、重启、USB 网卡模式、GPS、短信收发、AT 控制台"),
                new("注意", "插件运行时由 DeviceHub 独占 AT 口；要用其他串口工具，先在隐私页撤销授权"),
            },
        });
    }

    private async Task<QuectelState> StateAsync(string deviceKey)
    {
        if (_states.TryGetValue(deviceKey, out var st)) return st;
        var fresh = new QuectelState { Authorized = await _ctx.Device.GetAccessAsync(deviceKey) != AccessScope.Denied };
        return _states.GetOrAdd(deviceKey, fresh);
    }

    public async Task<string> InvokeAsync(string deviceKey, string method, string argsJson, CancellationToken ct)
    {
        var st = await StateAsync(deviceKey);
        if (!_sessions.TryGetValue(deviceKey, out var s))
        {
            st.Status = !st.Authorized ? "未授权" : !st.Driver.AtPortReady ? "AT 口没有驱动，无法操作模块" : "模块未连接";
            return Json(st);
        }
        var a = string.IsNullOrWhiteSpace(argsJson) ? default : JsonDocument.Parse(argsJson).RootElement;

        switch (method)
        {
            case "Refresh": await s.ReadInfoAsync(); await s.PollAsync(); st.Status = "已刷新"; break;
            case "SetBands": await s.SetBandsAsync(IntList(a, "bands")); break;
            case "RestoreBands": await s.RestoreBandsAsync(); break;
            case "LockCell": await s.LockCellAsync(Int(a, "earfcn", -1), Int(a, "pci", -1)); break;
            case "LockFrequency": await s.LockFrequencyAsync(Int(a, "earfcn", -1)); break;
            case "Unlock": await s.UnlockAsync(); break;
            case "SetScanMode": await s.SetScanModeAsync(Int(a, "mode", -1)); break;
            case "SetRoaming": await s.SetRoamingAsync(Int(a, "mode", -1)); break;
            case "SetRadio": await s.SetRadioAsync(Bool(a, "on", true)); break;
            case "Reboot": await s.RebootAsync(); break;
            case "SetUsbNet": await s.SetUsbNetAsync(Int(a, "mode", -1)); break;
            case "SetGps": await s.SetGpsAsync(Bool(a, "on", false)); break;
            case "LoadSms": await s.LoadSmsAsync(); break;
            case "SendSms": await s.SendSmsAsync(Str(a, "to"), Str(a, "text")); break;
            case "DeleteSms": await s.DeleteSmsAsync(Int(a, "index", -1)); break;
            case "EsimRead": await s.EsimReadAsync(); break;
            case "EsimEnable": await s.EsimSwitchAsync(Str(a, "iccid"), true); break;
            case "EsimDisable": await s.EsimSwitchAsync(Str(a, "iccid"), false); break;
            case "EsimDelete": await s.EsimDeleteAsync(Str(a, "iccid")); break;
            case "EsimRename": await s.EsimRenameAsync(Str(a, "iccid"), Str(a, "nickname")); break;
            case "EsimRemoveNotification": await s.EsimRemoveNotificationAsync(Int(a, "seq", -1)); break;
            case "RunAt": await s.RunAtAsync(Str(a, "command")); break;
            case "ClearConsole": s.ClearConsole(); break;
            default: st.Status = "未知操作 " + method; break;
        }
        await Push(deviceKey, st);
        return Json(st);
    }

    public async Task<string> GetStateAsync(string deviceKey) => Json(await StateAsync(deviceKey));

    public async Task PrepareShutdownAsync()
    {
        foreach (var k in _sessions.Keys.ToList())
            if (_sessions.TryRemove(k, out var s)) await s.DisposeAsync();
    }

    private Task Push(string deviceKey, QuectelState st) => _ctx.PushStateAsync(deviceKey, Json(st));

    /// <summary>轮询线程和 UI 操作可能同时改状态里的列表，序列化撞上时重试。</summary>
    public static string Json(QuectelState s)
    {
        for (int i = 0; ; i++)
        {
            try { return JsonSerializer.Serialize(s, J); }
            catch (InvalidOperationException) when (i < 5) { Thread.Sleep(5); }
        }
    }

    private static int Int(JsonElement a, string p, int dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : dflt;
    private static bool Bool(JsonElement a, string p, bool dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : dflt;
    private static string Str(JsonElement a, string p) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static List<int> IntList(JsonElement a, string p) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToList() : new();
}
