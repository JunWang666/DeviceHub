using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Dispatching;

namespace DeviceHub.Plugin.Quectel.Ui;

/// <summary>Quectel 页面的 ViewModel：状态由后端推送，操作经 bridge 转发（需要确认的操作由页面先弹窗）。</summary>
public sealed class QuectelViewModel : INotifyPropertyChanged
{
    private readonly IPluginUiBridge _bridge;
    private readonly DispatcherQueue _ui;
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public QuectelState State { get; private set; } = new();

    public QuectelViewModel(IPluginUiBridge bridge)
    {
        _bridge = bridge;
        _ui = DispatcherQueue.GetForCurrentThread();
        _bridge.StatePushed += json => _ui.TryEnqueue(() => Apply(json));
    }

    public async Task LoadAsync()
    {
        try { Apply(await _bridge.GetStateAsync()); }
        catch (Exception ex) { _error = "读取失败：" + ex.Message; OnChanged(nameof(Status)); }
    }

    private static string D(string? s) => string.IsNullOrEmpty(s) ? "—" : s;
    private static string D(int? v, string unit = "") => v is { } x ? $"{x}{unit}" : "—";

    // —— 概览 ——

    public string Title => State.Module.Model.Length > 0 ? $"{State.Module.Manufacturer} {State.Module.Model}" : "4G 模块";
    public string ConnectionText => !State.Authorized ? "未授权" : !State.Connected ? "未连接"
        : State.Network.Cfun == 4 ? "飞行模式" : AtParse.RegText(State.Network.EpsRegistration);
    public bool ShowUnauthorized => !State.Authorized;
    public bool Busy => State.Busy;
    public bool CanOperate => State.Authorized && State.Connected && !State.Busy;

    public string SignalGlyph => State.Network.EpsRegistration is 1 or 5 && State.Network.Cfun != 4
        ? char.ConvertFromUtf32(0xE86C + Math.Clamp(State.Signal.Bars, 0, 4)) : "";
    public string RsrpText => State.Signal.Rsrp is { } p ? $"{p} dBm" : State.Signal.Rssi is { } r ? $"{r} dBm" : "—";
    public string SignalCaption => $"RSRQ {D(State.Signal.Rsrq, " dB")} · SINR {(State.Signal.Sinr is { } s ? $"{s:0.#} dB" : "—")}";
    public string OperatorText => D(State.Network.Operator);
    public string OperatorCaption => string.Join(" · ", new[] { State.Network.Act, State.Network.Band.Replace("LTE BAND ", "B") }.Where(x => x.Length > 0)) is { Length: > 0 } t ? t : "—";
    public string IpText => D(State.Network.Ip);
    public string IpCaption => $"USB 网卡 {AtParse.UsbNetName(State.Module.UsbNet)}";
    public string TempText => State.Temperatures.Count > 0 ? $"{State.Temperatures.Max()} ℃" : "—";
    public string TempCaption => State.LastPollAt is { } t ? $"刷新于 {t.ToLocalTime():HH:mm:ss}" : "";

    // —— 服务小区 ——

    public string ServingPlmn => State.Serving is { Mcc: > 0 } s ? $"{s.Mcc}-{s.Mnc:00}" : D(State.Network.Plmn);
    public string ServingCellText => State.Serving is { } s && s.CellId.Length > 0 ? $"{s.CellId}（TAC {s.Tac}）" : "—";
    public string ServingPciText => State.Serving is { Pci: { } p, Earfcn: { } e } s ? $"PCI {p} · EARFCN {e} · B{s.Band} · {s.Duplex} {s.DlBandwidthMhz:0.#} MHz" : "—";
    public string ServingStateText => State.Serving?.State switch
    {
        "CONNECT" => "连接中（有数据传输）", "NOCONN" => "空闲（已驻留）", "SEARCH" => "搜索中", "LIMSRV" => "受限服务", null => "—", var x => x,
    };
    public string RegistrationText => $"EPS（数据）{AtParse.RegText(State.Network.EpsRegistration)} · 电路域（语音/短信）{AtParse.RegText(State.Network.CsRegistration)}";
    public List<NeighbourRow> Neighbours => State.Neighbours.Select(n => new NeighbourRow(n, State.Serving)).ToList();
    public bool HasNeighbours => State.Neighbours.Count > 0;

    // —— 频段 ——

    /// <summary>频段勾选项。轮询推送不改变频段配置时沿用同一份列表，免得用户正勾选时被刷掉。</summary>
    public List<BandItem> Bands
    {
        get
        {
            var sig = string.Join(",", State.Bands.Supported) + "|" + State.Bands.LteMask;
            if (sig != _bandsSig)
            {
                _bandsSig = sig;
                _bands = State.Bands.Supported.Select(b => new BandItem(b, State.Bands.Enabled.Contains(b))).ToList();
            }
            return _bands;
        }
    }
    private string _bandsSig = "";
    private List<BandItem> _bands = new();
    public string BandsDescription => State.Bands.Supported.Count == 0 ? "未读取"
        : State.Bands.Restricted ? $"已限定为 B{string.Join("/B", State.Bands.Enabled)}（共支持 {State.Bands.Supported.Count} 个）"
        : $"全部 {State.Bands.Supported.Count} 个 LTE 频段可用";

    // —— 锁定 ——

    public string LockDescription
    {
        get
        {
            var l = State.Locks;
            var parts = new List<string>();
            if (l.Cells.Count > 0) parts.Add("锁定小区 " + string.Join("、", l.Cells.Select(c => $"EARFCN {c.Earfcn} / PCI {c.Pci}")));
            if (l.LteAction == 1) parts.Add($"锁定频点 EARFCN {l.LteEarfcn}");
            if (l.LteAction == 2) parts.Add($"锁定小区 EARFCN {l.LteEarfcn} / PCI {l.LtePci}");
            return parts.Count > 0 ? string.Join("；", parts) : "未锁定（自动选择小区）";
        }
    }
    public bool IsLocked => State.Locks.Locked;

    // —— 网络设置 ——

    /// <summary>ComboBox 项顺序：自动、仅 LTE、仅 WCDMA、仅 GSM。</summary>
    public int ScanModeIndex => State.Network.ScanMode switch { 0 => 0, 3 => 1, 2 => 2, 1 => 3, _ => -1 };
    public static int ScanModeFromIndex(int i) => i switch { 0 => 0, 1 => 3, 2 => 2, 3 => 1, _ => -1 };
    /// <summary>ComboBox 项顺序：自动、允许漫游、禁止漫游。</summary>
    public int RoamingIndex => State.Network.RoamService switch { 255 => 0, 2 => 1, 1 => 2, _ => -1 };
    public static int RoamingFromIndex(int i) => i switch { 0 => 255, 1 => 2, 2 => 1, _ => -1 };
    public bool AirplaneOn => State.Network.Cfun == 4;
    public int UsbNetIndex => State.Module.UsbNet ?? -1;
    public string ImsText => State.Network.ImsEnabled switch { true => "VoLTE（IMS）已开启", false => "VoLTE（IMS）未开启：在只用 LTE 时不能打电话", null => "" };

    // —— SIM ——

    public string SimState => State.Sim.State switch { "READY" => "就绪", "SIM PIN" => "需要 PIN 码", "SIM PUK" => "需要 PUK 码", "" => "—", var s => s };
    public string Imsi => D(State.Sim.Imsi);
    public string Iccid => D(State.Sim.Iccid);
    public string Smsc => D(State.Sim.Smsc);
    public string Number => D(State.Sim.Number);

    // —— GPS ——

    public bool GpsOn => State.Gps.On == true;
    public string GpsDescription => State.Gps.On != true ? "已关闭" : !State.Gps.Fix ? D(State.Gps.Note)
        : $"已定位 · {State.Gps.Satellites} 颗卫星 · HDOP {State.Gps.Hdop:0.0}";
    public string GpsPosition => State.Gps.Fix ? $"{State.Gps.Latitude:0.000000}, {State.Gps.Longitude:0.000000}" : "—";
    public string GpsDetail => State.Gps.Fix ? $"海拔 {State.Gps.Altitude:0.#} m · 速度 {State.Gps.SpeedKmh:0.#} km/h · {State.Gps.Utc}" : "";

    // —— 短信 ——

    public string SmsDescription
    {
        get
        {
            var s = State.Sms;
            var t = s.Used is { } u ? $"{s.Storage} 存储 {u}/{s.Total}" : "存储未读取";
            if (s.NewSinceLoad > 0) t += $" · {s.NewSinceLoad} 条新短信";
            return t;
        }
    }
    public bool SmsLoaded => State.Sms.Loaded;
    public bool SmsEmpty => State.Sms.Loaded && State.Sms.Messages.Count == 0;

    // 会话列表与气泡：短信内容不变时沿用同一份列表，轮询推送不会刷掉选中项和滚动位置。
    private string _smsSig = "";
    private List<ThreadRow> _threads = new();
    private string _bubbleSig = "";
    private List<BubbleRow> _bubbles = new();

    private string SmsSignature => string.Join(",", State.Sms.Messages.Select(m => $"{m.Index}:{m.Status}"));

    /// <summary>按号码分组的会话，最近的在上面。</summary>
    public List<ThreadRow> Threads
    {
        get
        {
            var sig = SmsSignature;
            if (sig != _smsSig)
            {
                _smsSig = sig;
                _threads = State.Sms.Messages.GroupBy(m => AtParse.PeerKey(m.Peer))
                    .Select(g => new ThreadRow(g.Key, g.OrderBy(m => m.Time, StringComparer.Ordinal).ToList()))
                    .OrderByDescending(t => t.LastTime, StringComparer.Ordinal).ToList();
            }
            return _threads;
        }
    }

    /// <summary>当前会话的号码键；null = 新消息（手动填号码）。</summary>
    public string? SelectedThread { get; private set; }

    public void SelectThread(string? key)
    {
        SelectedThread = key;
        if (key is not null) _composingNew = false;
        OnChanged(nameof(Bubbles)); OnChanged(nameof(ChatTitle)); OnChanged(nameof(IsNewChat)); OnChanged(nameof(HasChat)); OnChanged(nameof(ComposingNew));
    }

    public List<BubbleRow> Bubbles
    {
        get
        {
            var sig = SelectedThread + "|" + SmsSignature;
            if (sig != _bubbleSig)
            {
                _bubbleSig = sig;
                _bubbles = SelectedThread is null ? new()
                    : State.Sms.Messages.Where(m => AtParse.PeerKey(m.Peer) == SelectedThread)
                        .OrderBy(m => m.Time, StringComparer.Ordinal).Select(m => new BubbleRow(m)).ToList();
            }
            return _bubbles;
        }
    }

    public bool IsNewChat => SelectedThread is null;
    /// <summary>点了"新消息"、正在填号码。</summary>
    public bool ComposingNew => SelectedThread is null && _composingNew;
    public bool HasChat => SelectedThread is not null || _composingNew;
    public string ChatTitle => SelectedThread is { } k ? Threads.FirstOrDefault(t => t.Key == k)?.Peer ?? k : "新消息";
    /// <summary>当前会话里用来回复的号码（取最近一条的原始写法，带不带 +86 以对方为准）。</summary>
    public string? ReplyTo => SelectedThread is { } k ? Threads.FirstOrDefault(t => t.Key == k)?.Peer : null;

    private bool _composingNew;
    public void StartNewChat()
    {
        _composingNew = true;
        SelectThread(null);
    }

    // —— 模块信息 ——

    public string Revision => D(State.Module.Revision);
    public string Imei => D(State.Module.Imei);
    public string Mbn => D(State.Module.ActiveMbn);
    public string UsbConfig => D(State.Module.UsbConfig);
    public string ScanSequence => D(State.Network.ScanSequence);

    // —— 控制台 ——

    public string ConsoleText => string.Join("\n", State.Console);

    // —— 操作 ——

    private string _error = "";
    public string Status => _error.Length > 0 ? _error : State.Status;

    public Task InvokeAsync(string method, object args) => InvokeCore(method, args);

    private async Task InvokeCore(string method, object args)
    {
        try { Apply(await _bridge.InvokeAsync(method, JsonSerializer.Serialize(args, J))); }
        catch (Exception ex) { _error = "操作失败：" + ex.Message; OnChanged(nameof(Status)); }
    }

    /// <summary>页面在把后端状态写回控件（ComboBox、开关）期间为 true，此时控件事件不触发操作。</summary>
    public bool Applying { get; private set; }

    private void Apply(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        Applying = true;
        try
        {
            var s = JsonSerializer.Deserialize<QuectelState>(json, J);
            if (s is null) return;
            State = s;
            _error = "";
            OnChanged(string.Empty); // 刷新全部绑定
        }
        catch (Exception ex) { _error = "解析失败：" + ex.Message; OnChanged(nameof(Status)); }
        finally { Applying = false; }
    }

    /// <summary>重新写一遍全部绑定（用户在确认框里取消后，把下拉框、开关拉回模块的真实状态）。</summary>
    public void PropertyChangedAll()
    {
        Applying = true;
        try { OnChanged(string.Empty); } finally { Applying = false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>邻区列表的一行。</summary>
public sealed class NeighbourRow(NeighbourCell n, ServingCell? serving)
{
    public NeighbourCell Cell { get; } = n;
    public string Title => $"{(Cell.Kind == "inter" ? "异频" : "同频")} · EARFCN {Cell.Earfcn}{(Cell.Band is { } b ? $"（B{b}）" : "")}{(Cell.Pci is { } p ? $" · PCI {p}" : "")}";
    public string Detail => Cell.Rsrp is { } r ? $"RSRP {r} dBm · RSRQ {Cell.Rsrq} dB · RSSI {Cell.Rssi} dBm" : "未测量";
    public bool IsServing => serving is { } s && s.Earfcn == Cell.Earfcn && s.Pci == Cell.Pci;
    public string Badge => IsServing ? "当前" : "";
    public bool CanLockCell => Cell.Pci is not null;
}

/// <summary>频段勾选项。</summary>
public sealed class BandItem(int band, bool on)
{
    public int Band { get; } = band;
    public string Label => $"B{Band}";
    public bool IsOn { get; set; } = on;
}

/// <summary>会话列表的一项：一个号码的全部短信。</summary>
public sealed class ThreadRow(string key, List<SmsMessage> messages)
{
    public string Key { get; } = key;
    public List<SmsMessage> Messages { get; } = messages;
    private SmsMessage Last => Messages[^1];
    public string Peer => Messages.FirstOrDefault(m => !m.Outgoing)?.Peer ?? Last.Peer;
    public string Initial => Peer.TrimStart('+').Length > 0 ? Peer.TrimStart('+')[^Math.Min(2, Peer.TrimStart('+').Length)..] : "#";
    public string Preview => (Last.Outgoing ? "我：" : "") + Last.Text.Replace('\n', ' ');
    public string LastTime => Last.Time;
    public string TimeText => BubbleRow.ShortTime(Last.Time);
    public bool Unread => Messages.Any(m => m.Status == "未读");
    public Microsoft.UI.Xaml.Visibility UnreadVisibility => Unread ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}

/// <summary>聊天气泡：收到的靠左、发出的靠右用强调色。</summary>
public sealed class BubbleRow(SmsMessage m)
{
    public SmsMessage Message { get; } = m;
    public string Text => Message.Text;
    public string Meta => ShortTime(Message.Time) + (Message.Outgoing ? " · " + Message.Status : "");
    public Microsoft.UI.Xaml.HorizontalAlignment Align => Message.Outgoing ? Microsoft.UI.Xaml.HorizontalAlignment.Right : Microsoft.UI.Xaml.HorizontalAlignment.Left;
    public Microsoft.UI.Xaml.Media.Brush Background => Res(Message.Outgoing ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorSecondaryBrush");
    public Microsoft.UI.Xaml.Media.Brush Foreground => Res(Message.Outgoing ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
    public Microsoft.UI.Xaml.Media.Brush MetaForeground => Res(Message.Outgoing ? "TextOnAccentFillColorSecondaryBrush" : "TextFillColorSecondaryBrush");

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];

    /// <summary>"2026-11-05 22:00:00" → 今天显示 "22:00"，否则 "11-05 22:00"。</summary>
    public static string ShortTime(string t)
    {
        if (t.Length < 16) return t;
        return t[..10] == DateTime.Now.ToString("yyyy-MM-dd") ? t[11..16] : t[5..16];
    }
}
