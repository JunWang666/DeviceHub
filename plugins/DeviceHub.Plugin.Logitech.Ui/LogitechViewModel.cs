using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Dispatching;

namespace DeviceHub.Plugin.Logitech.Ui;

/// <summary>罗技页面的 ViewModel：状态由后端推送，操作经 bridge 转发。接收器上有多个设备时按所选设备显示。</summary>
public sealed class LogitechViewModel : INotifyPropertyChanged
{
    private readonly IPluginUiBridge _bridge;
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _debounce;
    private Action? _pending;
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Read = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public LogitechStateDto State { get; private set; } = new();
    private int _selected;

    public LogitechViewModel(IPluginUiBridge bridge)
    {
        _bridge = bridge;
        _ui = DispatcherQueue.GetForCurrentThread();
        _bridge.StatePushed += json => _ui.TryEnqueue(() => Apply(json));
        // 滑块拖动时只在停下 300ms 后写一次，避免一次拖动发几十帧。
        _debounce = _ui.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(300);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => { var a = _pending; _pending = null; a?.Invoke(); };
    }

    public async Task LoadAsync()
    {
        try { Apply(await _bridge.GetStateAsync()); } catch (Exception ex) { _error = "读取失败：" + ex.Message; OnChanged(nameof(Status)); }
    }

    private HidppDeviceDto? D => _selected >= 0 && _selected < State.Devices.Count ? State.Devices[_selected] : null;
    private bool Editable => State.Authorized && D?.Online == true;

    // —— 概览 ——

    public string Title => State.IsReceiver ? ReceiverTitle : D?.Name is { Length: > 0 } n ? n : State.ProductName;
    private string ReceiverTitle => State.ReceiverKind switch
    {
        "unifying" => "Unifying 接收器", "bolt" => "Bolt 接收器", "lightspeed" => "LIGHTSPEED 接收器",
        "nano" => "Nano 接收器", _ => "罗技接收器",
    };
    public string ConnectionText => !State.Authorized ? "未授权"
        : !State.Connected ? "未连接"
        : State.IsReceiver ? $"已连接 · {State.Link} · {State.Devices.Count} 个已配对设备" + (State.ReceiverFirmware.Length > 0 ? $" · 固件 {State.ReceiverFirmware}" : "")
        : $"已连接 · {State.Link}";
    public bool ShowUnauthorized => !State.Authorized;
    public string HeaderGlyph => State.IsReceiver ? "" : KindGlyph(D?.Kind);

    public bool ShowDevicePicker => State.IsReceiver && State.Devices.Count > 0;
    public bool ShowNoDevices => State.Authorized && State.IsReceiver && State.Devices.Count == 0 && State.LastReadAt is not null;
    public List<string> DeviceNames => State.Devices.Select(d =>
        $"{(d.Name.Length > 0 ? d.Name : $"设备 {d.Index}")}{(d.Online ? "" : "（未连接）")}").ToList();
    public int SelectedDevice
    {
        get => State.Devices.Count == 0 ? -1 : Math.Clamp(_selected, 0, State.Devices.Count - 1);
        set
        {
            if (value < 0 || value == _selected || _applying) return;
            _selected = value;
            NotifyAll();
        }
    }

    public bool HasDevice => D is not null;
    public bool DeviceOnline => D?.Online == true;
    public bool DeviceOffline => D is { Online: false };
    public string DeviceName => D is null ? "" : D.Name.Length > 0 ? D.Name : $"设备 {D.Index}";
    public string DeviceGlyph => KindGlyph(D?.Kind);
    public string DeviceInfoText
    {
        get
        {
            if (D is null) return "";
            var parts = new List<string> { KindText(D.Kind) };
            if (D.Protocol.Length > 0) parts.Add($"HID++ {D.Protocol}");
            if (D.Firmware.Length > 0) parts.Add($"固件 {D.Firmware}");
            if (D.Wpid.Length > 0) parts.Add($"WPID {D.Wpid}");
            if (State.LastReadAt is { } t) parts.Add($"最近读取 {t.ToLocalTime():HH:mm:ss}");
            return string.Join(" · ", parts);
        }
    }
    public string OfflineText => D?.Status is { Length: > 0 } s ? s : "设备未连接：移动鼠标或按任意键唤醒后点「重新读取」";

    private static string KindGlyph(string? k) => k switch
    {
        "mouse" or "trackball" or "touchpad" => "",
        "keyboard" or "numpad" => "",
        "headset" => "",
        "presenter" or "remote" => "",
        "webcam" => "",
        "gamepad" or "joystick" or "wheel" => "",
        "receiver" => "",
        _ => "",
    };
    private static string KindText(string k) => k switch
    {
        "mouse" => "鼠标", "trackball" => "轨迹球", "touchpad" => "触控板", "keyboard" => "键盘", "numpad" => "数字键盘",
        "headset" => "耳机", "presenter" => "演示器", "remote" => "遥控器", "webcam" => "摄像头", "gamepad" => "手柄",
        "wheel" => "方向盘", "joystick" => "摇杆", "speaker" => "音箱", "microphone" => "麦克风", "dock" => "扩展坞",
        _ => "设备",
    };

    // —— 电量 ——

    public bool BatteryKnown => D?.Battery?.Percent is not null;
    public bool HasBattery => D?.Battery is not null;
    public string BatteryText => D?.Battery?.Percent is { } p ? (D.Battery.Estimated ? $"约 {p}%" : $"{p}%") : "—";
    public double BatteryValue => D?.Battery?.Percent ?? 0;
    /// <summary>Segoe Fluent Icons：Battery0–10 = E850–E85A，充电 BatteryCharging0–10 = E85B–E865。</summary>
    public string BatteryGlyph => char.ConvertFromUtf32((D?.Battery?.Charging == true ? 0xE85B : 0xE850) + Math.Clamp((D?.Battery?.Percent ?? 0) / 10, 0, 10));
    public string BatteryDetail => D?.Battery is not { } b ? ""
        : string.Join(" · ", new[] { b.Status, b.VoltageMv is { } mv ? $"{mv / 1000.0:0.00} V" : "" }.Where(s => s.Length > 0));

    // —— DPI ——

    public bool HasDpi => D?.Dpi is not null;
    public bool DpiDiscrete => D?.Dpi is { Choices.Count: > 0 };
    public bool DpiRange => D?.Dpi is { Choices.Count: 0, Max: > 0 };
    public List<string> DpiChoices => D?.Dpi?.Choices.Select(c => c.ToString()).ToList() ?? new();
    public int DpiIndex
    {
        get => D?.Dpi is { } d ? d.Choices.IndexOf(d.Value) : -1;
        set { if (!_applying && value >= 0 && D?.Dpi is { } d && value < d.Choices.Count && d.Choices[value] != d.Value) Invoke("SetDpi", new { index = D.Index, dpi = d.Choices[value] }); }
    }
    public double DpiValue
    {
        get => D?.Dpi?.Value ?? 0;
        set { if (!_applying && !double.IsNaN(value) && D?.Dpi is { } d && (int)value != d.Value) Invoke("SetDpi", new { index = D.Index, dpi = (int)value }); }
    }
    public double DpiMin => D?.Dpi?.Min ?? 0;
    public double DpiMax => D?.Dpi?.Max ?? 0;
    public double DpiStep => Math.Max(1, D?.Dpi?.Step ?? 1);
    public string DpiDescription => D?.Dpi is not { } d ? ""
        : (d.Choices.Count > 0 ? $"{d.Choices.Count} 档" : $"{d.Min}–{d.Max}，步进 {d.Step}") + (d.Default > 0 ? $" · 默认 {d.Default}" : "")
          + (D.Onboard?.Onboard == true ? " · 板载模式下可能被配置覆盖" : "");

    // —— 回报率 ——

    public bool HasReportRate => D?.ReportRate is not null;
    public List<string> ReportRateChoices => D?.ReportRate?.Choices.Select(ms => $"{1000 / ms} Hz（{ms} ms）").ToList() ?? new();
    public int ReportRateIndex
    {
        get => D?.ReportRate is { } r ? r.Choices.IndexOf(r.Ms) : -1;
        set { if (!_applying && value >= 0 && D?.ReportRate is { } r && value < r.Choices.Count && r.Choices[value] != r.Ms) Invoke("SetReportRate", new { index = D.Index, ms = r.Choices[value] }); }
    }

    // —— 指针速度 ——

    public bool HasPointerSpeed => D?.PointerSpeed is not null;
    public double PointerSpeed
    {
        get => D?.PointerSpeed ?? 256;
        set
        {
            if (_applying || D is not { PointerSpeed: { } cur } d || (int)value == cur) return;
            int v = (int)value, index = d.Index;
            Debounce(() => Invoke("SetPointerSpeed", new { index, value = v }));
        }
    }
    public string PointerSpeedText => D?.PointerSpeed is { } v ? $"{v / 256.0:0.00}×" : "—";

    // —— 滚轮 ——

    public bool HasSmartShift => D?.SmartShift is not null;
    public bool Ratchet
    {
        get => D?.SmartShift?.Ratchet == true;
        set { if (!_applying && D?.SmartShift is { } s && value != s.Ratchet) Invoke("SetSmartShift", new { index = D.Index, ratchet = value, threshold = s.Threshold }); }
    }
    public double Threshold
    {
        get => D?.SmartShift?.Threshold ?? 0;
        set
        {
            if (_applying || D?.SmartShift is not { } s || (int)value == s.Threshold) return;
            int v = (int)value, index = D.Index;
            bool ratchet = s.Ratchet;
            Debounce(() => Invoke("SetSmartShift", new { index, ratchet, threshold = v }));
        }
    }
    public string ThresholdText => D?.SmartShift is { } s ? s.Threshold.ToString() : "—";

    public bool HasWheel => D?.Wheel is not null;
    /// <summary>没有 SmartShift 时由高精度滚轮卡片自己带"滚轮"分组标题。</summary>
    public bool HasWheelOnly => HasWheel && !HasSmartShift;
    public bool HasConnectionGroup => HasHosts || HasOnboard;
    public bool HiRes
    {
        get => D?.Wheel?.HiRes == true;
        set { if (!_applying && D?.Wheel is { } w && value != w.HiRes) Invoke("SetWheel", new { index = D.Index, hiRes = value, inverted = w.Inverted }); }
    }
    public bool Inverted
    {
        get => D?.Wheel?.Inverted == true;
        set { if (!_applying && D?.Wheel is { } w && value != w.Inverted) Invoke("SetWheel", new { index = D.Index, hiRes = w.HiRes, inverted = value }); }
    }
    public string HiResDescription => D?.Wheel is { } w
        ? $"每格发送 {Math.Max(1, w.Multiplier)} 个细分事件；不支持高精度滚动的程序会滚得很快" + (w.Diverted ? " · 滚轮当前被其他软件接管" : "")
        : "";

    // —— 键盘 ——

    public bool HasFnSwap => D?.FnSwap is not null;
    public bool FnSwap
    {
        get => D?.FnSwap == true;
        set { if (!_applying && D?.FnSwap is { } cur && value != cur) Invoke("SetFnSwap", new { index = D.Index, swap = value }); }
    }

    // —— Easy-Switch ——

    public bool HasHosts => D?.Hosts is not null;
    public List<string> HostChoices => D?.Hosts is { } h ? Enumerable.Range(1, h.Count).Select(i => $"通道 {i}").ToList() : new();
    public int HostIndex
    {
        get => D?.Hosts?.Current ?? -1;
        set { if (!_applying && value >= 0 && D?.Hosts is { } h && value != h.Current) Invoke("SetHost", new { index = D.Index, host = value }); }
    }

    // —— 板载配置 ——

    public bool HasOnboard => D?.Onboard is not null;
    public bool OnboardMode
    {
        get => D?.Onboard?.Onboard == true;
        set { if (!_applying && D?.Onboard is { } o && value != o.Onboard) Invoke("SetOnboardMode", new { index = D.Index, onboard = value }); }
    }
    public bool HasProfiles => D?.Onboard is { ProfileCount: > 1, Onboard: true };
    public List<string> ProfileChoices => D?.Onboard is { } o ? Enumerable.Range(1, o.ProfileCount).Select(i => $"配置 {i}").ToList() : new();
    public int ProfileIndex
    {
        get => D?.Onboard is { Profile: > 0 } o ? o.Profile - 1 : -1;
        set { if (!_applying && value >= 0 && D?.Onboard is { } o && value + 1 != o.Profile) Invoke("SetProfile", new { index = D.Index, profile = value + 1 }); }
    }

    // —— 诊断 ——

    public string FeaturesText => D is null || D.Features.Count == 0 ? "—"
        : string.Join("\n", D.Features.Select(f => $"{f.Id}  {f.Name}  (#{f.Index})"));
    public bool HasUnsupported => D?.Unsupported.Count > 0;
    public string UnsupportedText => D is null ? "" : "设备还支持：" + string.Join("、", D.Unsupported) + "（本插件暂未实现）";

    // —— 操作 ——

    public void Refresh() => Invoke("Refresh", new { });

    private string _error = "";
    public string Status => _error.Length > 0 ? _error
        : string.Join(" · ", new[] { D?.Online == true ? D.Status : "", State.Status }.Where(s => s.Length > 0));

    private bool _applying;

    private void Debounce(Action a)
    {
        _pending = a;
        _debounce.Stop();
        _debounce.Start();
    }

    private async void Invoke(string method, object args)
    {
        try { Apply(await _bridge.InvokeAsync(method, JsonSerializer.Serialize(args, J))); }
        catch (Exception ex) { _error = "操作失败：" + ex.Message; OnChanged(nameof(Status)); }
    }

    private void Apply(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var s = JsonSerializer.Deserialize<LogitechStateDto>(json, Read);
            if (s is null) return;
            // 设备列表变化时按设备号保持选中项。
            var keep = D?.Index;
            State = s;
            _selected = keep is { } k && s.Devices.FindIndex(d => d.Index == k) is >= 0 and var i ? i
                : Math.Max(0, s.Devices.FindIndex(d => d.Online));
            _error = "";
            NotifyAll();
        }
        catch (Exception ex) { _error = "解析失败：" + ex.Message; OnChanged(nameof(Status)); }
    }

    private void NotifyAll()
    {
        _applying = true;
        try { foreach (var p in typeof(LogitechViewModel).GetProperties()) OnChanged(p.Name); }
        finally { _applying = false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
