using System.Collections.Concurrent;
using System.Text.Json;
using DeviceHub.Contracts;
using DeviceHub.Sdk;

namespace DeviceHub.Plugin.Gk65;

/// <summary>
/// GK65 真实设备插件后端。功能定义来自驱动配置文件（键位/灯效/宏/功能键）；
/// 设备写入经核心 Broker 代理（授权后），只下发实抓核实过的帧（见 <see cref="Gk65Protocol"/>）。
/// </summary>
public sealed class Gk65Plugin : IDevicePlugin
{
    public int SchemaVersion => 1;

    private IPluginContext _ctx = null!;
    private readonly ConcurrentDictionary<string, Gk65DeviceState> _states = new();
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task InitializeAsync(IPluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        await ctx.Notify.ShowAsync("GK65 驱动", "插件后端已在独立进程启动");
    }

    public async Task AttachAsync(DeviceInfo device, CancellationToken ct)
    {
        // 键盘没有读命令（只写、不回包），当前设置只能靠记忆：
        // 优先用 DeviceHub 上次保存的状态；首次接管时尝试导入官方驱动记下的灯效（ledeffect.xml）。
        var saved = await _ctx.Storage.GetAsync<Gk65DeviceState>("gk65:" + device.DeviceKey);
        var st = saved ?? OfficialDriverImport.TryLoad() ?? new Gk65DeviceState { Source = StateSource.Default };
        _states[device.DeviceKey] = st;

        var scope = await _ctx.Device.RequestAccessAsync(device.DeviceKey, "设置灯效、按键、宏");
        st.Authorized = scope != AccessScope.Denied;
        st.Status = st.Authorized ? "" : "用户拒绝授权";

        // 接管时不主动下发，只在用户改设置时写入。
        await _ctx.Storage.SetAsync("gk65:" + device.DeviceKey, st);
        await Push(device.DeviceKey, st);
    }

    public Task<DeviceDescription?> DescribeAsync(string deviceKey)
    {
        if (!_states.TryGetValue(deviceKey, out var st)) return Task.FromResult<DeviceDescription?>(null);
        var p = st.Current.Panel;
        var mode = Gk65Names.LedMode(p.Mode);
        return Task.FromResult<DeviceDescription?>(new DeviceDescription
        {
            Subtitle = $"2.4G 接收器 · {mode}",
            Details = new DeviceDetailItem[]
            {
                new("连接方式", "2.4G 无线接收器（USB 1A2C:7FFF）"),
                new("配置通道", "接口 2 · 厂商自定义 0xFF01 · 64 字节输出报告"),
                new("当前灯效", $"{mode} · 亮度 {p.Brightness}/5 · 速度 {p.Speed}/4"),
                new("设置来源", st.Source switch
                {
                    StateSource.Saved => "DeviceHub 上次保存（键盘不支持读取当前设置）",
                    StateSource.OfficialDriver => "首次接管时从官方驱动记录导入（ledeffect.xml）",
                    _ => "默认值（键盘不支持读取当前设置）",
                }),
                new("最近同步", st.LastSyncedAt is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未下发"),
                new("已支持", "面板灯效：模式 / 亮度 / 速度"),
                new("尚未支持", "灯效方向、侧灯、恢复出厂（协议未抓取）"),
                new("不提供", "改键与宏：键盘固件不保存，官方软件是在电脑端转换的"),
            },
        });
    }

    public Task DetachAsync(string deviceKey)
    {
        if (_states.TryGetValue(deviceKey, out var s)) { s.Status = "设备已移除"; s.Authorized = false; }
        return Task.CompletedTask;
    }

    public async Task<string> InvokeAsync(string deviceKey, string method, string argsJson, CancellationToken ct)
    {
        var st = _states.GetOrAdd(deviceKey, _ => new Gk65DeviceState());
        var a = string.IsNullOrWhiteSpace(argsJson) ? default : JsonDocument.Parse(argsJson).RootElement;

        switch (method)
        {
            case "SwitchProfile":
                st.CurrentProfile = Clamp(a, "index", 0, 2);
                await Send(deviceKey, st, Gk65Protocol.SwitchProfile(st.CurrentProfile));
                await ApplyProfileAsync(deviceKey, st);
                break;

            case "SetDpi":
                st.Dpi = Math.Clamp(Int(a, "value", st.Dpi), 100, 25600);
                await Send(deviceKey, st, Gk65Protocol.SetDpi(st.Dpi));
                break;

            case "SetPanelLighting":
            {
                var p = st.Current.Panel;
                p.Mode = Int(a, "mode", p.Mode); p.Speed = Int(a, "speed", p.Speed);
                p.Brightness = Int(a, "brightness", p.Brightness); p.Direction = Int(a, "direction", p.Direction);
                p.R = Int(a, "r", p.R); p.G = Int(a, "g", p.G); p.B = Int(a, "b", p.B);
                await Send(deviceKey, st, Gk65Protocol.PanelLighting(p));
                break;
            }

            case "SetSideLighting":
            {
                var s = st.Current.Side;
                s.Mode = Int(a, "mode", s.Mode); s.Brightness = Int(a, "brightness", s.Brightness);
                await Send(deviceKey, st, Gk65Protocol.SideLighting(s));
                break;
            }

            // 改键与宏不做：实测官方软件改键/宏不向键盘发送任何数据（只写本地 record.xml），
            // 是电脑端的按键转换，不属于设备能力。

            case "RestoreFactory":
                _states[deviceKey] = st = new Gk65DeviceState { Authorized = true, Status = "已恢复出厂" };
                await Send(deviceKey, st, Gk65Protocol.RestoreFactory());
                break;
        }

        st.Source = StateSource.Saved; // 用户改过之后，状态就是 DeviceHub 记住的设置
        await _ctx.Storage.SetAsync("gk65:" + deviceKey, st);
        return Json(st);
    }

    public Task<string> GetStateAsync(string deviceKey)
        => Task.FromResult(Json(_states.GetOrAdd(deviceKey, _ => new Gk65DeviceState())));

    public async Task PrepareShutdownAsync()
    {
        foreach (var (k, s) in _states) await _ctx.Storage.SetAsync("gk65:" + k, s);
    }

    /// <summary>把当前配置整体下发到设备（切换配置后调用）。</summary>
    private async Task ApplyProfileAsync(string deviceKey, Gk65DeviceState st)
    {
        if (!st.Authorized) return;
        await Send(deviceKey, st, Gk65Protocol.PanelLighting(st.Current.Panel));
        await Send(deviceKey, st, Gk65Protocol.SideLighting(st.Current.Side));
    }

    private async Task Send(string deviceKey, Gk65DeviceState st, Gk65Protocol.Frame frame)
    {
        if (!st.Authorized) { st.Status = "未授权"; return; }
        // 安全约束：只下发从官方软件抓包核实过的帧；未验证帧绝不写入真键盘。
        if (!frame.Verified) { st.Status = $"未下发：{frame.Note}"; await Push(deviceKey, st); return; }
        var res = await _ctx.Device.WriteOutputAsync(deviceKey, frame.Data);
        st.Status = res.Status switch
        {
            DeviceIoStatus.Ok => "已同步到键盘",
            DeviceIoStatus.AccessRevoked => "授权已被撤销",
            DeviceIoStatus.AccessDenied => "未授权",
            _ => $"写入失败：{res.Status}",
        };
        if (res.Status == DeviceIoStatus.Ok) st.LastSyncedAt = DateTimeOffset.Now;
        if (res.Status is DeviceIoStatus.AccessRevoked or DeviceIoStatus.AccessDenied) st.Authorized = false;
        await Push(deviceKey, st);
    }

    private Task Push(string deviceKey, Gk65DeviceState st) => _ctx.PushStateAsync(deviceKey, Json(st));

    private static int Int(JsonElement a, string p, int dflt) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : dflt;
    private static int Clamp(JsonElement a, string p, int lo, int hi) => Math.Clamp(Int(a, p, lo), lo, hi);
    private static string Json(Gk65DeviceState s) => JsonSerializer.Serialize(s, J);
}
