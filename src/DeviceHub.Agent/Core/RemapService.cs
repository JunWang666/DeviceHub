using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DeviceHub.Agent.Native;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;
using static DeviceHub.Agent.Native.InputNative;

namespace DeviceHub.Agent.Core;

/// <summary>
/// 按键映射引擎（参照 PowerToys 键盘管理器）：在专用线程上装低级键盘 / 鼠标钩子，按规则吞掉触发键并用 SendInput 发出目标输入。
/// · 单键映射（触发键不带修饰键）：无论当时按着什么修饰键都生效，修饰键原样保留（A→B 时 Ctrl+A 变成 Ctrl+B）。
/// · 快捷键映射（带修饰键）：修饰键须完全一致；执行时先放开触发用的修饰键，抬起触发键后再按回仍按着的那些。
/// · 鼠标中键 / 侧键 / 滚轮倾斜、以及插件上报的设备按键（如罗技手势键）同样可以作为触发。
/// 钩子回调必须很快（超时会被系统摘掉），所以：逻辑在锁内只算出要发的输入，锁外再 SendInput；文本、宏、启动程序放到后台执行。
/// 自己注入的输入带 <see cref="Marker"/>，钩子直接放行。
/// 没有规则、也没在录制时不装钩子。
/// </summary>
public sealed class RemapService : IDisposable
{
    private const nuint Marker = 0x44484D50; // "DHMP"
    private const int DummyKey = 0xFF;        // 放开 Win / Alt 前先按一下，免得弹开始菜单 / 激活菜单栏（PowerToys 同样做法）
    private const uint WmUpdateHooks = 0x8001; // WM_APP + 1

    private readonly ILogger _log;
    private readonly AppHub _hub;
    private readonly string _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions J = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private RemapConfig _config = new();
    private string? _error;

    // —— 钩子线程 ——
    private readonly Thread _thread;
    private uint _threadId;
    private nint _kbHook, _msHook;
    private readonly HookProc _kbProc, _msProc; // 保持委托存活

    // —— 状态（_gate 内）——
    /// <summary>当前按下的键（物理输入及其他程序注入的，不含本引擎注入的）。</summary>
    private readonly HashSet<int> _down = new();
    /// <summary>正在执行的映射：源（"k:VK" / "m:按键" / "d:插件|设备|按键"）→ 抬起时要撤销的东西。</summary>
    private readonly Dictionary<string, Active> _active = new();
    /// <summary>按下被吞掉的键，抬起也要吞掉。</summary>
    private readonly HashSet<int> _swallow = new();
    private readonly HashSet<MouseButton> _swallowMouse = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _macros = new();

    // —— 录制 ——
    private CaptureMode? _capture;
    private int _captureSession;
    private int _capLoneModifier;   // 只按了一个修饰键（还没按别的键）时记下它：抬起时录成"单个修饰键"
    private long _capLastTick;
    private readonly Channel<InputCaptureEvent> _emit = Channel.CreateUnbounded<InputCaptureEvent>(new UnboundedChannelOptions { SingleReader = true });

    // 前台进程名缓存（按程序生效的规则用）
    private uint _fgPid;
    private string _fgName = "";

    private sealed class Active
    {
        public required RemapAction Action;
        /// <summary>为满足目标组合键而按下的修饰键（抬起时放开）。</summary>
        public List<int> Pressed = new();
        /// <summary>为执行快捷键映射而临时放开的物理修饰键（抬起时若仍按着就按回去）。</summary>
        public List<int> Released = new();
    }

    public RemapService(ILogger log, string dataDir, AppHub hub)
    {
        _log = log;
        _hub = hub;
        _file = Path.Combine(dataDir, "remap.json");
        _config = Load();
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "RemapHooks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ = Task.Run(EmitLoopAsync);
    }

    public string? Error => _error;

    // ———————————————————— 配置 ————————————————————

    public RemapConfig GetConfig()
    {
        lock (_gate) return Clone(_config);
    }

    public void SaveConfig(RemapConfig config)
    {
        config.Rules ??= new();
        config.Macros ??= new();
        var copy = Clone(config);
        List<INPUT> outq = new();
        lock (_gate)
        {
            // 正在按着的映射先撤销，免得规则变了之后键卡在按下状态
            foreach (var a in _active.Values) Undo(a, outq);
            _active.Clear();
            _config = copy;
        }
        Send(outq);
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, J));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning(ex, "保存按键映射失败"); }
        _log.LogInformation("按键映射已更新：{Rules} 条规则、{Macros} 个宏，{State}", copy.Rules.Count, copy.Macros.Count, copy.Enabled ? "已启用" : "已停用");
        UpdateHooks();
    }

    private RemapConfig Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<RemapConfig>(File.ReadAllText(_file), J) ?? new();
        }
        catch (Exception ex) { _log.LogWarning(ex, "读取按键映射失败，使用空配置"); }
        return new();
    }

    private static RemapConfig Clone(RemapConfig c) =>
        JsonSerializer.Deserialize<RemapConfig>(JsonSerializer.Serialize(c, J), J)!;

    private bool NeedKeyboard() => _capture is not null || (_config.Enabled && _config.Rules.Any(r => r.Enabled && r.Trigger.Kind == TriggerKind.Key));
    private bool NeedMouse() => _capture is CaptureMode.Trigger || (_config.Enabled && _config.Rules.Any(r => r.Enabled && r.Trigger.Kind == TriggerKind.Mouse));

    // ———————————————————— 钩子线程 ————————————————————

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0); // 建消息队列，之后 PostThreadMessage 才收得到
        InstallHooks();
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WmUpdateHooks) { InstallHooks(); continue; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_kbHook != 0) UnhookWindowsHookEx(_kbHook);
        if (_msHook != 0) UnhookWindowsHookEx(_msHook);
    }

    private void UpdateHooks()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmUpdateHooks, 0, 0);
    }

    /// <summary>按需装 / 卸钩子（只在钩子线程上调用）。</summary>
    private void InstallHooks()
    {
        bool kb, ms;
        lock (_gate) { kb = NeedKeyboard(); ms = NeedMouse(); }
        var mod = GetModuleHandle(null);
        string? err = null;
        if (kb && _kbHook == 0)
        {
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            if (_kbHook == 0) err = $"安装键盘钩子失败（错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）";
        }
        else if (!kb && _kbHook != 0) { UnhookWindowsHookEx(_kbHook); _kbHook = 0; ReleaseAll(); }
        if (ms && _msHook == 0)
        {
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
            if (_msHook == 0) err ??= $"安装鼠标钩子失败（错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）";
        }
        else if (!ms && _msHook != 0) { UnhookWindowsHookEx(_msHook); _msHook = 0; }
        _error = err;
        if (err is not null) _log.LogError("{Error}", err);
        else _log.LogDebug("按键映射钩子：键盘 {Kb}，鼠标 {Ms}", _kbHook != 0, _msHook != 0);
    }

    /// <summary>卸掉键盘钩子时清空跟踪状态，免得之后错判按键状态。</summary>
    private void ReleaseAll()
    {
        List<INPUT> outq = new();
        lock (_gate)
        {
            foreach (var a in _active.Values) Undo(a, outq);
            _active.Clear();
            _down.Clear();
            _swallow.Clear();
        }
        Send(outq);
    }

    // ———————————————————— 键盘 ————————————————————

    private unsafe nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code < 0) return CallNextHookEx(0, code, wParam, lParam);
        var k = (KBDLLHOOKSTRUCT*)lParam;
        if (k->dwExtraInfo == Marker) return CallNextHookEx(0, code, wParam, lParam);
        bool down = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
        int vk = (int)k->vkCode;
        List<INPUT> outq = new();
        bool swallow;
        try
        {
            lock (_gate) swallow = OnKey(vk, down, outq);
        }
        catch (Exception ex) { _log.LogWarning(ex, "按键映射处理失败"); swallow = false; }
        Send(outq);
        return swallow ? 1 : CallNextHookEx(0, code, wParam, lParam);
    }

    private bool OnKey(int vk, bool down, List<INPUT> outq)
    {
        bool repeat = down && !_down.Add(vk);
        if (!down) _down.Remove(vk);

        if (_capture is { } mode) return CaptureKey(mode, vk, down, repeat);

        string src = "k:" + vk;
        if (!down)
        {
            if (_active.Remove(src, out var a)) { Undo(a, outq); return true; }
            return _swallow.Remove(vk);
        }
        if (_active.TryGetValue(src, out var held))
        {
            // 自动重复：目标键跟着重复，其他动作忽略
            if (held.Action is { Kind: RemapActionKind.Keys, Key: not 0 } act) Key(outq, act.Key, true);
            return true;
        }
        if (!_config.Enabled) return false;

        var mods = HeldModifiers(except: vk);
        string? fg = null;
        RemapRule? rule = null;
        if (!IsModifier(vk) && mods != 0)
            rule = Find(TriggerKind.Key, t => t.Key == vk, mods, ref fg);
        rule ??= Find(TriggerKind.Key, t => t.Key == vk, KeyModifiers.None, ref fg);
        if (rule is null) return false;
        _active[src] = Begin(rule, outq);
        return true;
    }

    // ———————————————————— 鼠标 ————————————————————

    private unsafe nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code < 0 || wParam == WM_MOUSEMOVE) return CallNextHookEx(0, code, wParam, lParam);
        var m = (MSLLHOOKSTRUCT*)lParam;
        if (m->dwExtraInfo == Marker) return CallNextHookEx(0, code, wParam, lParam);
        short hi = (short)(m->mouseData >> 16);
        (MouseButton b, bool down, bool pulse)? ev = (int)wParam switch
        {
            WM_MBUTTONDOWN => (MouseButton.Middle, true, false),
            WM_MBUTTONUP => (MouseButton.Middle, false, false),
            WM_XBUTTONDOWN => (hi == 2 ? MouseButton.X2 : MouseButton.X1, true, false),
            WM_XBUTTONUP => (hi == 2 ? MouseButton.X2 : MouseButton.X1, false, false),
            WM_MOUSEWHEEL => (hi > 0 ? MouseButton.WheelUp : MouseButton.WheelDown, true, true),
            WM_MOUSEHWHEEL => (hi > 0 ? MouseButton.WheelRight : MouseButton.WheelLeft, true, true),
            _ => null,
        };
        if (ev is not { } e) return CallNextHookEx(0, code, wParam, lParam);
        List<INPUT> outq = new();
        bool swallow;
        try
        {
            lock (_gate) swallow = OnMouse(e.b, e.down, e.pulse, outq);
        }
        catch (Exception ex) { _log.LogWarning(ex, "鼠标映射处理失败"); swallow = false; }
        Send(outq);
        return swallow ? 1 : CallNextHookEx(0, code, wParam, lParam);
    }

    private bool OnMouse(MouseButton b, bool down, bool pulse, List<INPUT> outq)
    {
        var mods = HeldModifiers();
        if (_capture is CaptureMode.Trigger)
        {
            if (!down) return _swallowMouse.Remove(b);
            // 普通上下滚动只有按着修饰键时才录（免得一滚页面就录进去）
            if (b is MouseButton.WheelUp or MouseButton.WheelDown && mods == 0) return false;
            if (!pulse) _swallowMouse.Add(b);
            SwallowHeldKeys();
            EndCapture(new InputCaptureEvent { Trigger = new InputTrigger { Kind = TriggerKind.Mouse, Button = b, Modifiers = mods }, Done = true });
            return true;
        }

        string src = "m:" + b;
        if (!down)
        {
            if (_active.Remove(src, out var a)) { Undo(a, outq); return true; }
            return _swallowMouse.Remove(b);
        }
        if (!_config.Enabled || _capture is not null) return false;
        string? fg = null;
        RemapRule? rule = null;
        if (mods != 0) rule = Find(TriggerKind.Mouse, t => t.Button == b, mods, ref fg);
        rule ??= Find(TriggerKind.Mouse, t => t.Button == b, KeyModifiers.None, ref fg);
        if (rule is null) return false;
        var act = Begin(rule, outq);
        if (pulse) Undo(act, outq);
        else _active[src] = act;
        return true;
    }

    // ———————————————————— 设备按键（插件上报）————————————————————

    public void OnDeviceInput(string pluginId, string deviceKey, string displayName, DeviceInputEvent e)
    {
        List<INPUT> outq = new();
        lock (_gate)
        {
            string src = $"d:{pluginId}|{deviceKey}|{e.ControlId}";
            if (_capture is CaptureMode.Trigger)
            {
                if (!e.Pressed) return;
                EndCapture(new InputCaptureEvent
                {
                    Trigger = new InputTrigger
                    {
                        Kind = TriggerKind.Device, PluginId = pluginId, DeviceKey = deviceKey, ControlId = e.ControlId,
                        Name = $"{displayName} · {e.Name}",
                    },
                    Done = true,
                });
                return;
            }
            if (!e.Pressed)
            {
                if (_active.Remove(src, out var a)) Undo(a, outq);
            }
            else if (_config.Enabled && !_active.ContainsKey(src))
            {
                string? fg = null;
                bool Match(InputTrigger t) => t.PluginId == pluginId && t.DeviceKey == deviceKey && t.ControlId == e.ControlId;
                var mods = HeldModifiers();
                RemapRule? rule = mods != 0 ? Find(TriggerKind.Device, Match, mods, ref fg) : null;
                rule ??= Find(TriggerKind.Device, Match, KeyModifiers.None, ref fg);
                if (rule is not null) _active[src] = Begin(rule, outq);
            }
        }
        Send(outq);
    }

    /// <summary>有没有规则用到这个设备按键（插件可据此决定是否需要接管）。</summary>
    public bool HasDeviceRules => _config.Rules.Any(r => r.Enabled && r.Trigger.Kind == TriggerKind.Device);

    // ———————————————————— 规则匹配与执行 ————————————————————

    private RemapRule? Find(TriggerKind kind, Func<InputTrigger, bool> match, KeyModifiers mods, ref string? fg)
    {
        RemapRule? global = null;
        foreach (var r in _config.Rules)
        {
            if (!r.Enabled || r.Trigger.Kind != kind || r.Trigger.Modifiers != mods || !match(r.Trigger)) continue;
            if (string.IsNullOrWhiteSpace(r.Apps)) { global ??= r; continue; }
            fg ??= ForegroundProcess();
            if (AppMatches(r.Apps, fg)) return r; // 指定程序的规则优先于全局规则
        }
        return global;
    }

    private static bool AppMatches(string apps, string fg)
    {
        foreach (var raw in apps.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var a = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".exe";
            if (string.Equals(a, fg, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>开始执行规则（触发键按下）：算出要发的输入放进 outq；文本 / 宏 / 程序放到后台。返回抬起时要撤销的记录。</summary>
    private Active Begin(RemapRule rule, List<INPUT> outq)
    {
        var act = rule.Action;
        var a = new Active { Action = act };
        var trig = rule.Trigger.Modifiers;
        switch (act.Kind)
        {
            case RemapActionKind.Keys:
            {
                // 快捷键映射：放开目标里没有的触发修饰键
                if (trig != 0)
                {
                    var keep = act.Modifiers;
                    var toRelease = _down.Where(v => IsModifier(v) && (ModOf(v) & trig) != 0 && (ModOf(v) & keep) == 0).ToList();
                    if (toRelease.Any(v => ModOf(v) is KeyModifiers.Win or KeyModifiers.Alt)) Tap(outq, DummyKey);
                    foreach (var v in toRelease) { Key(outq, v, false); a.Released.Add(v); }
                }
                var system = SystemModifiers(a.Released);
                foreach (var (flag, vk) in ModifierKeys)
                    if ((act.Modifiers & flag) != 0 && (system & flag) == 0) { Key(outq, vk, true); a.Pressed.Add(vk); }
                if (act.Key != 0) Key(outq, act.Key, true);
                break;
            }
            case RemapActionKind.Mouse:
                Mouse(outq, act.Button, true);
                break;
            case RemapActionKind.Text or RemapActionKind.Macro or RemapActionKind.Run:
            {
                var macro = act.Kind == RemapActionKind.Macro ? _config.Macros.FirstOrDefault(m => m.Id == act.MacroId) : null;
                var held = _down.Where(IsModifier).ToList();
                _ = Task.Factory.StartNew(() => RunAsync(act, macro, held), TaskCreationOptions.LongRunning);
                break;
            }
            case RemapActionKind.Disable:
                break;
        }
        return a;
    }

    /// <summary>撤销（触发键抬起）：放开目标键和临时按下的修饰键，按回仍按着的物理修饰键。</summary>
    private void Undo(Active a, List<INPUT> outq)
    {
        switch (a.Action.Kind)
        {
            case RemapActionKind.Keys:
                if (a.Action.Key != 0) Key(outq, a.Action.Key, false);
                if (a.Pressed.Any(v => ModOf(v) is KeyModifiers.Win or KeyModifiers.Alt) && a.Action.Key == 0) Tap(outq, DummyKey);
                for (int i = a.Pressed.Count - 1; i >= 0; i--) Key(outq, a.Pressed[i], false);
                foreach (var v in a.Released) if (_down.Contains(v)) Key(outq, v, true);
                break;
            case RemapActionKind.Mouse:
                if (a.Action.Button is not (MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight))
                    Mouse(outq, a.Action.Button, false);
                break;
        }
    }

    /// <summary>后台执行文本 / 宏 / 启动程序。先放开触发时按着的修饰键（否则 Ctrl 会和输入的字母组成快捷键），完了再按回仍按着的。</summary>
    private void RunAsync(RemapAction act, MacroDef? macro, List<int> held)
    {
        try
        {
            if (act.Kind == RemapActionKind.Run)
            {
                if (string.IsNullOrWhiteSpace(act.Path)) return;
                Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(act.Path.Trim().Trim('"')), act.Args ?? "") { UseShellExecute = true });
                return;
            }
            if (act.Kind == RemapActionKind.Macro && macro is null) { _log.LogWarning("宏 {Id} 不存在", act.MacroId); return; }

            // 同一个宏还在执行时再按一次 = 停止它（重复多次的宏可以随时停下）
            CancellationTokenSource? cts = null;
            if (macro is not null)
            {
                if (_macros.TryRemove(macro.Id, out var running)) { running.Cancel(); return; }
                cts = new CancellationTokenSource();
                _macros[macro.Id] = cts;
            }

            var pre = new List<INPUT>();
            if (held.Count > 0)
            {
                if (held.Any(v => ModOf(v) is KeyModifiers.Win or KeyModifiers.Alt)) Tap(pre, DummyKey);
                foreach (var v in held) Key(pre, v, false);
                Send(pre);
            }
            try
            {
                if (act.Kind == RemapActionKind.Text) SendText(act.Text ?? "");
                else if (macro is not null) RunMacro(macro, cts!.Token);
            }
            finally
            {
                if (macro is not null && _macros.TryGetValue(macro.Id, out var cur) && cur == cts) _macros.TryRemove(macro.Id, out _);
                var post = new List<INPUT>();
                lock (_gate) foreach (var v in held) if (_down.Contains(v)) Key(post, v, true);
                Send(post);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "执行按键映射动作失败"); }
    }

    private void RunMacro(MacroDef m, CancellationToken ct)
    {
        var downKeys = new HashSet<int>();
        var downButtons = new HashSet<MouseButton>();
        try
        {
            for (int r = 0; r < Math.Clamp(m.Repeat, 1, 10000) && !ct.IsCancellationRequested; r++)
                foreach (var s in m.Steps)
                {
                    if (ct.IsCancellationRequested) break;
                    var q = new List<INPUT>();
                    switch (s.Kind)
                    {
                        case MacroStepKind.KeyTap:
                            foreach (var (flag, vk) in ModifierKeys) if ((s.Modifiers & flag) != 0) Key(q, vk, true);
                            if (s.Key != 0) Tap(q, s.Key);
                            foreach (var (flag, vk) in Enumerable.Reverse(ModifierKeys)) if ((s.Modifiers & flag) != 0) Key(q, vk, false);
                            break;
                        case MacroStepKind.KeyDown: Key(q, s.Key, true); downKeys.Add(s.Key); break;
                        case MacroStepKind.KeyUp: Key(q, s.Key, false); downKeys.Remove(s.Key); break;
                        case MacroStepKind.Text: SendText(s.Text ?? ""); break;
                        case MacroStepKind.Delay: ct.WaitHandle.WaitOne(Math.Clamp(s.Ms, 0, 600_000)); break;
                        case MacroStepKind.MouseClick: Mouse(q, s.Button, true); Mouse(q, s.Button, false); break;
                        case MacroStepKind.MouseDown: Mouse(q, s.Button, true); downButtons.Add(s.Button); break;
                        case MacroStepKind.MouseUp: Mouse(q, s.Button, false); downButtons.Remove(s.Button); break;
                    }
                    Send(q);
                    // 每步之间给目标程序一点处理时间（很多程序按太快会丢键）
                    if (s.Kind != MacroStepKind.Delay) Thread.Sleep(2);
                }
        }
        finally
        {
            // 宏里按下没抬起的键 / 鼠标键，结束时统一放开
            var q = new List<INPUT>();
            foreach (var vk in downKeys) Key(q, vk, false);
            foreach (var b in downButtons) Mouse(q, b, false);
            Send(q);
        }
    }

    private static void SendText(string text)
    {
        var q = new List<INPUT>();
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n') { Tap(q, 0x0D); continue; }
            if (ch == '\t') { Tap(q, 0x09); continue; }
            q.Add(Unicode(ch, false));
            q.Add(Unicode(ch, true));
        }
        // 分批发，长文本一次塞太多部分程序会丢字
        for (int i = 0; i < q.Count; i += 64)
        {
            Send(q.GetRange(i, Math.Min(64, q.Count - i)));
            Thread.Sleep(1);
        }
    }

    // ———————————————————— 录制 ————————————————————

    public void StartCapture(CaptureMode mode)
    {
        int session;
        lock (_gate)
        {
            // 录制开始前已按着的键（比如还没松开的鼠标）不算
            _capture = mode;
            session = ++_captureSession;
            _capLoneModifier = 0;
            _capLastTick = 0;
            _capDowned.Clear();
        }
        UpdateHooks();
        // 超时自动结束：录触发键 15 秒，录宏 2 分钟
        var timeout = mode == CaptureMode.Macro ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(15);
        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            lock (_gate) { if (_captureSession != session || _capture is null) return; }
            StopCapture();
        });
    }

    public void StopCapture()
    {
        lock (_gate)
        {
            if (_capture is null) return;
            SwallowHeldKeys();
            EndCapture(new InputCaptureEvent { Done = true });
        }
    }

    /// <summary>（_gate 内）结束录制并推送最后一个事件。</summary>
    private void EndCapture(InputCaptureEvent e)
    {
        _capture = null;
        _captureSession++;
        _emit.Writer.TryWrite(e);
        UpdateHooks();
    }

    /// <summary>录制期间按下（被吞掉）的键：结束时仍按着的，抬起也要吞掉。录制前就按着的键抬起照常放行。</summary>
    private readonly HashSet<int> _capDowned = new();

    private void SwallowHeldKeys()
    {
        foreach (var v in _capDowned) if (_down.Contains(v)) _swallow.Add(v);
        _capDowned.Clear();
    }

    private bool CaptureKey(CaptureMode mode, int vk, bool down, bool repeat)
    {
        if (down) { if (!repeat) _capDowned.Add(vk); }
        else if (!_capDowned.Remove(vk)) return false;

        if (mode == CaptureMode.Macro)
        {
            if (repeat) return true;
            long now = Environment.TickCount64;
            if (_capLastTick != 0 && now - _capLastTick >= 10)
                _emit.Writer.TryWrite(new InputCaptureEvent { Step = new MacroStep { Kind = MacroStepKind.Delay, Ms = (int)(now - _capLastTick) } });
            _capLastTick = now;
            _emit.Writer.TryWrite(new InputCaptureEvent { Step = new MacroStep { Kind = down ? MacroStepKind.KeyDown : MacroStepKind.KeyUp, Key = vk } });
            return true;
        }

        if (down)
        {
            if (repeat) return true;
            if (IsModifier(vk))
            {
                _capLoneModifier = vk;
                _emit.Writer.TryWrite(new InputCaptureEvent { Held = HeldModifiers() });
                return true;
            }
            var mods = HeldModifiers(except: vk);
            SwallowHeldKeys();
            EndCapture(new InputCaptureEvent { Trigger = new InputTrigger { Kind = TriggerKind.Key, Key = vk, Modifiers = mods }, Done = true });
            return true;
        }

        // 抬起：单独按了一下修饰键 → 录成这个修饰键本身（区分左右，如右 Alt）
        if (vk == _capLoneModifier)
        {
            SwallowHeldKeys();
            EndCapture(new InputCaptureEvent { Trigger = new InputTrigger { Kind = TriggerKind.Key, Key = vk }, Done = true });
            return true;
        }
        _capLoneModifier = 0;
        _emit.Writer.TryWrite(new InputCaptureEvent { Held = HeldModifiers() });
        return true;
    }

    private async Task EmitLoopAsync()
    {
        await foreach (var e in _emit.Reader.ReadAllAsync())
            _hub.InputCaptured(e);
    }

    // ———————————————————— 修饰键 ————————————————————

    private static readonly (KeyModifiers Flag, int Vk)[] ModifierKeys =
        [(KeyModifiers.Ctrl, 0xA2), (KeyModifiers.Alt, 0xA4), (KeyModifiers.Shift, 0xA0), (KeyModifiers.Win, 0x5B)];

    public static bool IsModifier(int vk) => ModOf(vk) != KeyModifiers.None;

    public static KeyModifiers ModOf(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => KeyModifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => KeyModifiers.Alt,
        0x10 or 0xA0 or 0xA1 => KeyModifiers.Shift,
        0x5B or 0x5C => KeyModifiers.Win,
        _ => KeyModifiers.None,
    };

    private KeyModifiers HeldModifiers(int except = 0)
    {
        var m = KeyModifiers.None;
        foreach (var v in _down) if (v != except) m |= ModOf(v);
        return m;
    }

    /// <summary>系统眼里按着的修饰键 = 物理按着的减去刚被临时放开的。</summary>
    private KeyModifiers SystemModifiers(List<int> released)
    {
        var m = KeyModifiers.None;
        foreach (var v in _down) if (!released.Contains(v)) m |= ModOf(v);
        return m;
    }

    // ———————————————————— 注入 ————————————————————

    private static void Key(List<INPUT> q, int vk, bool down) => q.Add(KeyInput(vk, down, Marker));
    private static void Tap(List<INPUT> q, int vk) { Key(q, vk, true); Key(q, vk, false); }
    private static void Mouse(List<INPUT> q, MouseButton b, bool down) => q.Add(MouseInput(b, down, Marker));
    private static INPUT Unicode(char ch, bool up) => UnicodeInput(ch, up, Marker);

    private static void Send(List<INPUT> q)
    {
        if (q.Count == 0) return;
        SendInputs(q.ToArray());
    }

    // ———————————————————— 前台程序 ————————————————————

    private string ForegroundProcess()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return "";
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _fgPid) return _fgName;
        _fgPid = pid;
        _fgName = ProcessName(pid);
        return _fgName;
    }

    public void Dispose()
    {
        foreach (var c in _macros.Values) c.Cancel();
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, 0, 0);
        _emit.Writer.TryComplete();
    }
}
