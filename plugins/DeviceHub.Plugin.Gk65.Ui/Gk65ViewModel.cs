using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Dispatching;

namespace DeviceHub.Plugin.Gk65.Ui;

/// <summary>GK65 页面的 ViewModel：状态与后端双向同步。</summary>
public sealed class Gk65ViewModel : INotifyPropertyChanged
{
    private readonly IPluginUiBridge _bridge;
    private readonly DispatcherQueue _ui;
    private bool _applying;
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Gk65Catalog Catalog => Gk65Catalog.Instance;
    public Gk65StateDto State { get; private set; } = new();

    /// <summary>官方界面可选的灯效（模式 1 在官方界面被注释掉，未核实，不提供）。</summary>
    public List<LedMode> PanelModes { get; }

    public Gk65ViewModel(IPluginUiBridge bridge)
    {
        _bridge = bridge;
        _ui = DispatcherQueue.GetForCurrentThread();
        _bridge.StatePushed += json => _ui.TryEnqueue(() => Apply(json));
        PanelModes = Catalog.LedModes.Where(m => m.I != 1).ToList();
    }

    public async Task LoadAsync()
    {
        try { Apply(await _bridge.GetStateAsync()); } catch (Exception ex) { Status = "读取失败：" + ex.Message; }
    }

    // —— 灯光（已核实协议：模式 / 亮度 / 速度）——

    public int PanelModeIndex
    {
        get => PanelModes.FindIndex(m => m.I == State.Current.Panel.Mode);
        set
        {
            if (value < 0 || value >= PanelModes.Count || PanelModes[value].I == State.Current.Panel.Mode) return;
            State.Current.Panel.Mode = PanelModes[value].I;
            OnChanged(nameof(SpeedEnabled));
            PushPanel();
        }
    }
    public double Brightness { get => State.Current.Panel.Brightness; set { var v = (int)Math.Round(value); if (v != State.Current.Panel.Brightness) { State.Current.Panel.Brightness = v; PushPanel(); } } }
    public double Speed { get => State.Current.Panel.Speed; set { var v = (int)Math.Round(value); if (v != State.Current.Panel.Speed) { State.Current.Panel.Speed = v; PushPanel(); } } }
    /// <summary>常亮没有速度可调。</summary>
    public bool SpeedEnabled => Authorized && State.Current.Panel.Mode != 0;

    public bool Authorized => State.Authorized;
    public bool ShowUnauthorized => !State.Authorized;

    private string _status = "";
    public string Status { get => string.IsNullOrEmpty(_status) ? State.Status : _status; set => Set(ref _status, value); }

    /// <summary>页面顶部的同步说明。</summary>
    public string SyncText => State.LastSyncedAt is { } t
        ? $"{(string.IsNullOrEmpty(Status) ? "已同步到键盘" : Status)} · {t.ToLocalTime():HH:mm:ss}"
        : string.IsNullOrEmpty(Status) ? "尚未向键盘下发设置" : Status;

    /// <summary>键盘不支持读取当前设置，提示用户显示值来自哪里。</summary>
    public string SourceNote => State.Source switch
    {
        1 => "键盘不支持读取当前设置。这里的值是首次接管时从官方驱动记录导入的；如果你用键盘快捷键改过灯效，可能与键盘实际状态不一致。",
        2 => "键盘不支持读取当前设置。这里是默认值，改动后才会同步到键盘。",
        _ => "键盘不支持读取当前设置。这里显示的是 DeviceHub 上次保存的设置；用键盘快捷键改过的灯效不会反映在这里。",
    };

    private void PushPanel()
    {
        if (_applying) return;
        var p = State.Current.Panel;
        Invoke("SetPanelLighting", new { mode = p.Mode, speed = p.Speed, brightness = p.Brightness });
    }

    private async void Invoke(string method, object args) { if (!_applying) await InvokeAwait(method, args); }
    private async Task InvokeAwait(string method, object args)
    {
        try { Apply(await _bridge.InvokeAsync(method, JsonSerializer.Serialize(args, J))); }
        catch (Exception ex) { Status = "操作失败：" + ex.Message; OnChanged(nameof(SyncText)); }
    }

    private void Apply(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        _applying = true;
        try
        {
            var s = JsonSerializer.Deserialize<Gk65StateDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (s is null) return;
            State = s;
            _status = "";
            OnAll();
        }
        catch (Exception ex) { _status = "解析失败：" + ex.Message; OnChanged(nameof(Status)); }
        finally { _applying = false; }
    }

    private void OnAll()
    {
        foreach (var n in new[] { nameof(PanelModeIndex), nameof(Brightness), nameof(Speed), nameof(SpeedEnabled),
            nameof(Authorized), nameof(ShowUnauthorized), nameof(Status), nameof(SyncText), nameof(SourceNote) })
            OnChanged(n);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null) { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; OnChanged(n); return true; }
    private void OnChanged(string? n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

