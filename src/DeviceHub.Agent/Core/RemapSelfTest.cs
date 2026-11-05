using System.Runtime.InteropServices;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;
using static DeviceHub.Agent.Native.InputNative;

namespace DeviceHub.Agent.Core;

/// <summary>
/// 按键映射引擎自检（DeviceHub.Agent.exe --remap-selftest）：用临时配置起一个引擎，注入 F13–F24 这类没有副作用的键，
/// 再用一个后装的观察钩子（先于引擎被调用）记下所有键盘事件，核对引擎注入（带标记）的输出。不需要 UI，不碰设备。
/// </summary>
internal static class RemapSelfTest
{
    private const nuint Marker = 0x44484D50;
    private const int F13 = 0x7C, F14 = 0x7D, F15 = 0x7E, F16 = 0x7F, F17 = 0x80, F18 = 0x81, F19 = 0x82, F20 = 0x83, F21 = 0x84, LShift = 0xA0, LCtrl = 0xA2;

    private static readonly List<(int Vk, bool Down, bool Ours)> Seen = new();
    private static HookProc? _proc;

    public static async Task<int> RunAsync(ILoggerFactory lf)
    {
        var dir = Path.Combine(Path.GetTempPath(), "devicehub-remap-selftest");
        Directory.CreateDirectory(dir);
        File.Delete(Path.Combine(dir, "remap.json"));
        var hub = new AppHub(lf.CreateLogger<AppHub>());
        var cb = new FakeUi();
        hub.SetCallback(cb);
        using var remap = new RemapService(lf.CreateLogger("Remap"), dir, hub);

        var macro = new MacroDef
        {
            Name = "t", Steps =
            [
                new MacroStep { Kind = MacroStepKind.KeyTap, Key = F18 },
                new MacroStep { Kind = MacroStepKind.Delay, Ms = 30 },
                new MacroStep { Kind = MacroStepKind.KeyTap, Key = F19 },
            ],
        };
        remap.SaveConfig(new RemapConfig
        {
            Rules =
            [
                Rule(new InputTrigger { Kind = TriggerKind.Key, Key = F13 }, new RemapAction { Kind = RemapActionKind.Keys, Key = F14 }),
                Rule(new InputTrigger { Kind = TriggerKind.Key, Key = F15, Modifiers = KeyModifiers.Shift }, new RemapAction { Kind = RemapActionKind.Keys, Key = F16 }),
                Rule(new InputTrigger { Kind = TriggerKind.Key, Key = F17 }, new RemapAction { Kind = RemapActionKind.Macro, MacroId = macro.Id }),
                Rule(new InputTrigger { Kind = TriggerKind.Device, PluginId = "p", DeviceKey = "d", ControlId = "00C3" }, new RemapAction { Kind = RemapActionKind.Keys, Key = F20, Modifiers = KeyModifiers.Ctrl }),
            ],
            Macros = [macro],
        });

        // 观察钩子：在自己的线程上装（比引擎的钩子后装，所以先被调用，能看到原始输入和引擎的输出）
        await Task.Delay(300);
        var observer = new Thread(() =>
        {
            _proc = Observe;
            SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            while (GetMessage(out var m, 0, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }) { IsBackground = true };
        observer.Start();
        await Task.Delay(300);

        int fails = 0;
        void Check(string name, IEnumerable<(int, bool)> expected)
        {
            List<(int, bool)> ours;
            lock (Seen) { ours = Seen.Where(s => s.Ours).Select(s => (s.Vk, s.Down)).ToList(); Seen.Clear(); }
            var exp = expected.ToList();
            bool ok = ours.SequenceEqual(exp);
            if (!ok) fails++;
            Console.WriteLine($"REMAPTEST {(ok ? "PASS" : "FAIL")} {name}: got [{Fmt(ours)}] want [{Fmt(exp)}]");
        }

        // 1. 单键映射 F13 → F14
        Inject((F13, true), (F13, false));
        await Task.Delay(150);
        Check("single key F13->F14", [(F14, true), (F14, false)]);

        // 2. 快捷键 Shift+F15 → F16：先放开 Shift，按 F16，抬起后按回 Shift
        Inject((LShift, true), (F15, true), (F15, false), (LShift, false));
        await Task.Delay(150);
        Check("shortcut Shift+F15->F16", [(LShift, false), (F16, true), (F16, false), (LShift, true)]);

        // 3. 宏
        Inject((F17, true), (F17, false));
        await Task.Delay(400);
        Check("macro F17", [(F18, true), (F18, false), (F19, true), (F19, false)]);

        // 4. 设备按键 → Ctrl+F20（按住期间保持）
        remap.OnDeviceInput("p", "d", "测试鼠标", new DeviceInputEvent { ControlId = "00C3", Name = "手势键", Pressed = true });
        remap.OnDeviceInput("p", "d", "测试鼠标", new DeviceInputEvent { ControlId = "00C3", Name = "手势键", Pressed = false });
        await Task.Delay(150);
        Check("device button -> Ctrl+F20", [(LCtrl, true), (F20, true), (F20, false), (LCtrl, false)]);

        // 5. 录制：Ctrl + F21 → 触发键 Ctrl+F21；录制期间的键不应外泄（引擎不注入任何东西）
        remap.StartCapture(CaptureMode.Trigger);
        await Task.Delay(200);
        Inject((LCtrl, true), (F21, true), (F21, false), (LCtrl, false));
        await Task.Delay(300);
        Check("capture emits nothing", []);
        var t = cb.Events.FirstOrDefault(e => e.Trigger is not null)?.Trigger;
        bool capOk = t is { Kind: TriggerKind.Key, Key: F21, Modifiers: KeyModifiers.Ctrl } && cb.Events.Any(e => e.Done);
        if (!capOk) fails++;
        Console.WriteLine($"REMAPTEST {(capOk ? "PASS" : "FAIL")} capture Ctrl+F21: {(t is null ? "无" : $"{t.Kind} {t.Modifiers} 0x{t.Key:X2}")}");

        // 6. 录到设备按键
        cb.Events.Clear();
        remap.StartCapture(CaptureMode.Trigger);
        remap.OnDeviceInput("p", "d", "测试鼠标", new DeviceInputEvent { ControlId = "00C3", Name = "手势键", Pressed = true });
        await Task.Delay(200);
        var dt = cb.Events.FirstOrDefault(e => e.Trigger is not null)?.Trigger;
        bool devOk = dt is { Kind: TriggerKind.Device, ControlId: "00C3", Name: "测试鼠标 · 手势键" };
        if (!devOk) fails++;
        Console.WriteLine($"REMAPTEST {(devOk ? "PASS" : "FAIL")} capture device button: {dt?.Name}");
        remap.OnDeviceInput("p", "d", "测试鼠标", new DeviceInputEvent { ControlId = "00C3", Name = "手势键", Pressed = false });

        // 7. 按设备生效：同一个 F13，指定键盘上映射成 F15，其他键盘仍走全局的 F13 → F14；指定程序的规则不匹配前台时不生效
        var cfg = remap.GetConfig();
        cfg.Rules.Add(new RemapRule
        {
            Trigger = new InputTrigger { Kind = TriggerKind.Key, Key = F13 },
            Action = new RemapAction { Kind = RemapActionKind.Keys, Key = F15 },
            Devices = [new DeviceFilter { Id = "1234:5678", Kind = InputDeviceKind.Keyboard }],
        });
        cfg.Rules.Add(new RemapRule
        {
            Trigger = new InputTrigger { Kind = TriggerKind.Key, Key = F13 },
            Action = new RemapAction { Kind = RemapActionKind.Keys, Key = F16 },
            Apps = "no-such-app.exe",
        });
        remap.SaveConfig(cfg);
        await Task.Delay(300);
        remap.TestKeyboard = new InputDeviceInfo { Kind = InputDeviceKind.Keyboard, Id = "1234:5678" };
        lock (Seen) Seen.Clear();
        Inject((F13, true), (F13, false));
        await Task.Delay(150);
        Check("device-scoped rule wins on matching keyboard", [(F15, true), (F15, false)]);
        remap.TestKeyboard = new InputDeviceInfo { Kind = InputDeviceKind.Keyboard, Id = "1111:2222" };
        Inject((F13, true), (F13, false));
        await Task.Delay(150);
        Check("other keyboard falls back to global rule", [(F14, true), (F14, false)]);
        remap.TestKeyboard = null;

        // 8. 停用后不再映射
        cfg = remap.GetConfig();
        cfg.Enabled = false;
        remap.SaveConfig(cfg);
        await Task.Delay(300);
        lock (Seen) Seen.Clear();
        Inject((F13, true), (F13, false));
        await Task.Delay(150);
        Check("disabled passes through", []);

        foreach (var d in Native.RawInput.List()) Console.WriteLine($"REMAPTEST device {d.Kind} [{d.Id}] {d.Name}");
        Console.WriteLine($"REMAPTEST DONE fails={fails} engineError={remap.Error ?? "none"}");
        return fails == 0 ? 0 : 1;
    }

    private static RemapRule Rule(InputTrigger t, RemapAction a) => new() { Trigger = t, Action = a };

    private static unsafe nint Observe(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            bool down = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            lock (Seen) Seen.Add(((int)k->vkCode, down, k->dwExtraInfo == Marker));
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static void Inject(params (int Vk, bool Down)[] keys)
    {
        foreach (var (vk, down) in keys)
        {
            SendInputs([KeyInput(vk, down, 0)]);
            Thread.Sleep(15);
        }
    }

    private static string Fmt(List<(int Vk, bool Down)> l) => string.Join(" ", l.Select(x => $"{(x.Down ? "+" : "-")}{x.Vk:X2}"));

    private sealed class FakeUi : IAppCallback
    {
        public readonly List<InputCaptureEvent> Events = new();
        public void OnInputCaptured(InputCaptureEvent e) { lock (Events) Events.Add(e); }
        public void OnPermissionRequested(PermissionRequest request) { }
        public void OnDevicesChanged(IReadOnlyList<DeviceInfo> devices) { }
        public void OnPluginStateChanged(PluginInfo plugin) { }
        public void OnPluginStatePushed(string pluginId, string deviceKey, string stateJson) { }
        public void OnOsd(OsdMessage message) { }
        public void OnUsbChanged() { }
    }
}
