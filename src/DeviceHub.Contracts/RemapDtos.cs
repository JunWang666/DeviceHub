namespace DeviceHub.Contracts;

// —— 按键映射（核心内置工具，参照 PowerToys 键盘管理器）——
// 规则在 Agent 里用低级键盘 / 鼠标钩子执行，App 只负责编辑配置；插件可以把设备上系统看不到的按键
// （如罗技鼠标的手势键）作为"设备按键"事件交给核心，同样能被映射。

/// <summary>修饰键位：不区分左右。</summary>
[Flags]
public enum KeyModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>可作为触发或动作的鼠标按键。左右键只能作为动作（避免把鼠标映射没了）；滚轮上下 / 左右为单次脉冲。</summary>
public enum MouseButton { Left, Right, Middle, X1, X2, WheelUp, WheelDown, WheelLeft, WheelRight }

public enum TriggerKind
{
    /// <summary>键盘按键（Modifiers 为 0 时是"单键映射"，否则是"快捷键映射"）。</summary>
    Key,
    /// <summary>鼠标按键（中键、侧键、滚轮倾斜），可带修饰键。</summary>
    Mouse,
    /// <summary>插件上报的设备按键（系统收不到的按键，如罗技手势键）。</summary>
    Device,
}

/// <summary>规则的触发条件。</summary>
public sealed class InputTrigger
{
    public TriggerKind Kind { get; set; }
    public KeyModifiers Modifiers { get; set; }
    /// <summary>虚拟键码（区分左右修饰键，如 VK_LCONTROL = 0xA2）。</summary>
    public int Key { get; set; }
    public MouseButton Button { get; set; }

    // —— 设备按键 ——
    public string? PluginId { get; set; }
    public string? DeviceKey { get; set; }
    /// <summary>插件内的按键标识（罗技为 CID 十六进制，如 "00C3"）。</summary>
    public string? ControlId { get; set; }
    /// <summary>按键的显示名（插件提供，如"MX Master 3 · 手势键"）。</summary>
    public string? Name { get; set; }

    public bool SameSource(InputTrigger o) => Kind == o.Kind && Kind switch
    {
        TriggerKind.Key => Key == o.Key && Modifiers == o.Modifiers,
        TriggerKind.Mouse => Button == o.Button && Modifiers == o.Modifiers,
        _ => PluginId == o.PluginId && DeviceKey == o.DeviceKey && ControlId == o.ControlId,
    };
}

public enum RemapActionKind
{
    /// <summary>按键或组合键（Modifiers + Key；Key 为 0 时只按修饰键）。按住触发键时保持按下。</summary>
    Keys,
    /// <summary>鼠标按键（按住触发键时保持按下；滚轮为单次）。</summary>
    Mouse,
    /// <summary>输入一段文本（Unicode）。</summary>
    Text,
    /// <summary>执行一个宏。</summary>
    Macro,
    /// <summary>启动程序 / 打开文件或网址。</summary>
    Run,
    /// <summary>禁用：吞掉这个按键。</summary>
    Disable,
}

public sealed class RemapAction
{
    public RemapActionKind Kind { get; set; }
    public KeyModifiers Modifiers { get; set; }
    public int Key { get; set; }
    public MouseButton Button { get; set; }
    public string? Text { get; set; }
    public string? MacroId { get; set; }
    public string? Path { get; set; }
    public string? Args { get; set; }
}

public sealed class RemapRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public InputTrigger Trigger { get; set; } = new();
    public RemapAction Action { get; set; } = new();
    /// <summary>只在这些程序（进程名，如 "chrome.exe"，分号分隔）在前台时生效；空 = 全局。</summary>
    public string? Apps { get; set; }
}

public enum MacroStepKind { KeyTap, KeyDown, KeyUp, Text, Delay, MouseClick, MouseDown, MouseUp }

public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; }
    /// <summary>KeyTap 时可带修饰键（按下组合键）。</summary>
    public KeyModifiers Modifiers { get; set; }
    public int Key { get; set; }
    public MouseButton Button { get; set; }
    public string? Text { get; set; }
    /// <summary>Delay 的毫秒数。</summary>
    public int Ms { get; set; }
}

public sealed class MacroDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<MacroStep> Steps { get; set; } = new();
    /// <summary>执行次数（≥1）。</summary>
    public int Repeat { get; set; } = 1;
}

public sealed class RemapConfig
{
    /// <summary>总开关。</summary>
    public bool Enabled { get; set; } = true;
    public List<RemapRule> Rules { get; set; } = new();
    public List<MacroDef> Macros { get; set; } = new();
}

/// <summary>录制模式：Trigger = 录一个触发键（单键 / 组合键 / 鼠标键 / 设备按键）；Macro = 连续录制宏步骤。</summary>
public enum CaptureMode { Trigger, Keys, Macro }

/// <summary>录制期间 Agent 推给 App 的事件（录制期间被录到的输入不会传给其他程序）。</summary>
public sealed class InputCaptureEvent
{
    /// <summary>Trigger / Keys 模式：录到的按键（组合键在主键按下时完成）。</summary>
    public InputTrigger? Trigger { get; set; }
    /// <summary>Macro 模式：录到的一步（按下 / 抬起，或两步之间的延时）。</summary>
    public MacroStep? Step { get; set; }
    /// <summary>录制已结束（录到了、超时或被取消）。</summary>
    public bool Done { get; set; }
    /// <summary>录制中当前按住的修饰键（供界面实时显示 "Ctrl + …"）。</summary>
    public KeyModifiers Held { get; set; }
}

/// <summary>插件上报的设备按键事件（插件 → 核心）。</summary>
public sealed class DeviceInputEvent
{
    /// <summary>插件内的按键标识。</summary>
    public required string ControlId { get; init; }
    /// <summary>按键显示名（如"手势键"），核心会在前面加上设备名。</summary>
    public required string Name { get; init; }
    public bool Pressed { get; init; }
}
