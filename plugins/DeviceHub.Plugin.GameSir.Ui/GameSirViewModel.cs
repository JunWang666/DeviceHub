using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Dispatching;

namespace DeviceHub.Plugin.GameSir.Ui;

/// <summary>GameSir 页面的 ViewModel：状态由后端推送，操作经 bridge 转发。</summary>
public sealed class GameSirViewModel : INotifyPropertyChanged
{
    private readonly IPluginUiBridge _bridge;
    private readonly DispatcherQueue _ui;
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Read = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public GameSirStateDto State { get; private set; } = new();

    public GameSirViewModel(IPluginUiBridge bridge)
    {
        _bridge = bridge;
        _ui = DispatcherQueue.GetForCurrentThread();
        _bridge.StatePushed += json => _ui.TryEnqueue(() => Apply(json));
    }

    public async Task LoadAsync()
    {
        try { Apply(await _bridge.GetStateAsync()); } catch (Exception ex) { _error = "读取失败：" + ex.Message; OnChanged(nameof(Status)); }
    }

    // —— 概览 ——

    public string ModelName => State.ModelName;
    public string ConnectionText => !State.Authorized ? "未授权"
        : !State.Connected ? "未连接"
        : State.Wireless ? "已连接 · 2.4G 接收器" : "已连接 · 有线";
    public bool ShowUnauthorized => !State.Authorized;

    public string BatteryText => State.Battery is { } b ? $"{b}%" : "—";
    public double BatteryValue => State.Battery ?? 0;
    public bool BatteryKnown => State.Battery is not null;
    /// <summary>Segoe Fluent Icons 电池字形 Battery0–10 = E850–E85A。</summary>
    public string BatteryGlyph => char.ConvertFromUtf32(0xE850 + Math.Clamp((State.Battery ?? 0) / 10, 0, 10));

    public string InfoText
    {
        get
        {
            var parts = new List<string>();
            if (State.Firmware.Length > 0) parts.Add($"固件 {State.Firmware}");
            if (State.Mac.Length > 0) parts.Add($"MAC {State.Mac}");
            if (State.LastReadAt is { } t) parts.Add($"最近读取 {t.ToLocalTime():HH:mm:ss}");
            return string.Join(" · ", parts);
        }
    }

    // —— 震动 ——

    private static readonly string[] Levels = ["关", "弱", "中", "强", "最强"];
    private static string Level(int? v) => v is { } l and >= 0 and <= 4 ? Levels[l] : "—";

    public double VibrationLeft
    {
        get => State.VibrationLeft ?? 0;
        set { if (!_applying && (int)value != State.VibrationLeft) Invoke("SetVibration", new { left = (int)value, right = State.VibrationRight ?? 3 }); }
    }
    public double VibrationRight
    {
        get => State.VibrationRight ?? 0;
        set { if (!_applying && (int)value != State.VibrationRight) Invoke("SetVibration", new { left = State.VibrationLeft ?? 3, right = (int)value }); }
    }
    public string VibrationLeftText => Level(State.VibrationLeft);
    public string VibrationRightText => Level(State.VibrationRight);
    public bool VibrationEditable => State.Authorized && State.CanWriteVibration && State.VibrationLeft is not null;
    public string VibrationDescription => State.CanWriteVibration ? "左右握把马达，0–4 档" : "只读：写入帧尚未从官方软件抓取核实";

    // —— 休眠 ——

    public string SleepText => State.SleepMinutes is { } m ? $"{m} 分钟无操作后休眠" : "—";
    public bool AutoSleep
    {
        get => State.AutoSleep == true;
        set { if (!_applying && value != AutoSleep) Invoke("SetSleep", new { minutes = State.SleepMinutes ?? 10, auto = value }); }
    }
    public bool SleepEditable => State.Authorized && State.CanWriteSleep && State.AutoSleep is not null;
    public string SleepDescription => State.CanWriteSleep ? SleepText : SleepText + "（只读）";

    // —— 死区（只读）——

    public string L2Text => $"{State.L2.Begin}% – {State.L2.End}%";
    public string R2Text => $"{State.R2.Begin}% – {State.R2.End}%";
    public string LeftStickText => $"{State.LeftStick.Begin}%";
    public string RightStickText => $"{State.RightStick.Begin}%";

    // —— 操作 ——

    public void Refresh() => Invoke("Refresh", new { });

    private string _error = "";
    public string Status => _error.Length > 0 ? _error : State.Status;

    private bool _applying;

    private async void Invoke(string method, object args)
    {
        try { Apply(await _bridge.InvokeAsync(method, JsonSerializer.Serialize(args, J))); }
        catch (Exception ex) { _error = "操作失败：" + ex.Message; OnChanged(nameof(Status)); }
    }

    private void Apply(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        _applying = true;
        try
        {
            var s = JsonSerializer.Deserialize<GameSirStateDto>(json, Read);
            if (s is null) return;
            State = s;
            _error = "";
            foreach (var p in typeof(GameSirViewModel).GetProperties()) OnChanged(p.Name);
        }
        catch (Exception ex) { _error = "解析失败：" + ex.Message; OnChanged(nameof(Status)); }
        finally { _applying = false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
