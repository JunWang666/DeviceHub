using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Dispatching;

namespace DeviceHub.Plugin.AirPods.Ui;

/// <summary>一个电量卡片（左耳 / 充电盒 / 右耳）。</summary>
public sealed class PartView : INotifyPropertyChanged
{
    public string Title { get; private set; } = "";
    public string Percent { get; private set; } = "—";
    public double Value { get; private set; }
    public string Detail { get; private set; } = "";
    /// <summary>Segoe Fluent Icons 电池字形（0–10 档，充电时用充电字形）。</summary>
    public string Glyph { get; private set; } = "";
    public bool Known { get; private set; }

    public void Update(string title, PartDto p, string? extra = null)
    {
        Title = title;
        Known = p.Battery is not null;
        Percent = p.Battery is { } v ? $"{v}%" : "—";
        Value = p.Battery ?? 0;
        var bits = new List<string>();
        if (p.Charging) bits.Add("充电中");
        if (p.InEar) bits.Add("已佩戴");
        if (extra is { Length: > 0 }) bits.Add(extra);
        Detail = bits.Count > 0 ? string.Join(" · ", bits) : Known ? "" : "暂无数据";
        int step = Math.Clamp((p.Battery ?? 0) / 10, 0, 10);
        // Battery0–10 = E850–E85A；BatteryCharging0–9 = E85B–E864，满电充电是 EA93。
        Glyph = char.ConvertFromUtf32(!p.Charging ? 0xE850 + step : step < 10 ? 0xE85B + step : 0xEA93);
        foreach (var n in new[] { nameof(Title), nameof(Percent), nameof(Value), nameof(Detail), nameof(Glyph), nameof(Known) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>AirPods 页面的 ViewModel：状态由后端推送，操作经 bridge 转发。</summary>
public sealed class AirPodsViewModel : INotifyPropertyChanged
{
    private readonly IPluginUiBridge _bridge;
    private readonly DispatcherQueue _ui;
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    public AirPodsStateDto State { get; private set; } = new();
    public PartView Left { get; } = new();
    public PartView Right { get; } = new();
    public PartView Case { get; } = new();

    public AirPodsViewModel(IPluginUiBridge bridge)
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
        : !State.Connected ? "未连接 —— 请先在 Windows 蓝牙设置里连接耳机"
        : State.LastSeen is null ? "已连接 · 正在等待耳机广播（打开充电盒或戴上耳机）"
        : "已连接";
    public bool ShowUnauthorized => !State.Authorized;
    public bool ShowBuds => !State.HeadWorn;
    public bool ShowCase => !State.HeadWorn;

    /// <summary>"电量来自 BLE 广播（10% 精度）· 最近收到 12:00:01 · 信号 -58 dBm"。</summary>
    public string SourceText
    {
        get
        {
            var parts = new List<string>();
            if (State.BatterySource == "aap") parts.Add("电量来自 AAP 通道（1% 精度）");
            else if (State.BatterySource == "ble") parts.Add("电量来自 BLE 广播（10% 精度）");
            if (State.LastSeen is { } t) parts.Add($"最近收到广播 {t.ToLocalTime():HH:mm:ss}");
            if (State.Rssi is { } r) parts.Add($"信号 {r} dBm");
            if (State.LidOpen is { } lid && !State.HeadWorn) parts.Add(lid ? "充电盒已打开" : "充电盒已合上");
            if (State.Firmware.Length > 0) parts.Add($"固件 {State.Firmware}");
            return string.Join(" · ", parts);
        }
    }

    // —— 降噪 / 对话感知（需要 AAP 驱动）——

    public bool AncVisible => State.SupportsAnc;
    public bool AncEnabled => State.Authorized && State.AapConnected;
    public bool AdaptiveEnabled => AncEnabled && State.SupportsAdaptive;
    /// <summary>
    /// 没连上 AAP 就提示，不论型号是否带降噪：1% 精度电量对所有型号都要走 AAP，
    /// 而且广播还没给出型号时（Unknown）SupportsAnc 恒为 false，按降噪门控会把提示整个藏掉。
    /// </summary>
    public bool ShowDriverNote => State.Authorized && State.Connected && !State.AapConnected;
    public bool ShowAdvancedHeader => AncVisible || ShowDriverNote;
    public string AdvancedHeader => AncVisible ? "噪声控制" : "高级功能";
    private string AapFeatures => State.SupportsAnc
        ? State.SupportsConversationAwareness ? "降噪模式、对话感知和 1% 精度电量" : "降噪模式和 1% 精度电量"
        : "1% 精度电量与固件版本";
    public string DriverNote => State.AapNote switch
    {
        "未安装 AAP 驱动" => $"{AapFeatures}要经 AirPods 的 AAP 控制通道（L2CAP），Windows 不对普通程序开放，需要另装第三方驱动（NTPods 或 PodBridge 的 AAP 驱动）。这两个驱动都只有测试签名，要先开启 Windows 测试模式，有安全风险，请阅读其说明后自行决定。电量（10% 精度）与佩戴状态不受影响。",
        { Length: > 0 } n => n,
        _ => "正在连接 AAP 控制通道…",
    };
    public string DriverNoteTitle => State.AapNote == "未安装 AAP 驱动" ? "需要 AAP 驱动" : "高级功能暂不可用";
    public string AncDescription => AncEnabled ? "切换后立即生效，以耳机回显为准" : "需要 AAP 驱动";

    public bool ModeOff => State.ListeningMode == 1;
    public bool ModeAnc => State.ListeningMode == 2;
    public bool ModeTransparency => State.ListeningMode == 3;
    public bool ModeAdaptive => State.ListeningMode == 4;

    public void SetListeningMode(int mode)
    {
        if (mode == State.ListeningMode) return;
        Invoke("SetListeningMode", new { mode });
    }

    public bool ConversationVisible => State.SupportsConversationAwareness;
    public bool ConversationOn
    {
        get => State.ConversationAwareness == true;
        set { if (value != ConversationOn && !_applying) Invoke("SetConversationAwareness", new { on = value }); }
    }

    // —— 状态 ——

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
            var s = JsonSerializer.Deserialize<AirPodsStateDto>(json, Read);
            if (s is null) return;
            State = s;
            _error = "";
            if (s.HeadWorn) Left.Update("耳机", s.Left);
            else
            {
                Left.Update("左耳", s.Left);
                Right.Update("右耳", s.Right);
                Case.Update("充电盒", s.Case, s.BothInCase ? "两只耳机都在盒内" : null);
            }
            foreach (var p in typeof(AirPodsViewModel).GetProperties().Where(p => p.Name is not (nameof(Left) or nameof(Right) or nameof(Case))))
                OnChanged(p.Name);
        }
        catch (Exception ex) { _error = "解析失败：" + ex.Message; OnChanged(nameof(Status)); }
        finally { _applying = false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
