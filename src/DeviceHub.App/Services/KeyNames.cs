using DeviceHub.Contracts;

namespace DeviceHub.App.Services;

/// <summary>虚拟键码 / 鼠标按键 / 触发与动作的显示文本，以及按键选择列表。</summary>
public static class KeyNames
{
    /// <summary>选择列表里的键（按分组排好）。</summary>
    public static readonly IReadOnlyList<(int Vk, string Name)> Keys = Build();

    private static List<(int, string)> Build()
    {
        var l = new List<(int, string)>();
        for (int c = 'A'; c <= 'Z'; c++) l.Add((c, ((char)c).ToString()));
        for (int c = '0'; c <= '9'; c++) l.Add((c, ((char)c).ToString()));
        for (int i = 1; i <= 24; i++) l.Add((0x6F + i, $"F{i}"));
        l.AddRange(new (int, string)[]
        {
            (0x1B, "Esc"), (0x09, "Tab"), (0x14, "Caps Lock"), (0x20, "空格"), (0x0D, "Enter"), (0x08, "Backspace"),
            (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"), (0x21, "Page Up"), (0x22, "Page Down"),
            (0x25, "←"), (0x26, "↑"), (0x27, "→"), (0x28, "↓"),
            (0x2C, "Print Screen"), (0x91, "Scroll Lock"), (0x13, "Pause"), (0x90, "Num Lock"), (0x5D, "菜单键"),
            (0xA2, "左 Ctrl"), (0xA3, "右 Ctrl"), (0xA0, "左 Shift"), (0xA1, "右 Shift"),
            (0xA4, "左 Alt"), (0xA5, "右 Alt"), (0x5B, "左 Win"), (0x5C, "右 Win"),
            (0xC0, "`"), (0xBD, "-"), (0xBB, "="), (0xDB, "["), (0xDD, "]"), (0xDC, "\\"),
            (0xBA, ";"), (0xDE, "'"), (0xBC, ","), (0xBE, "."), (0xBF, "/"),
            (0x60, "小键盘 0"), (0x61, "小键盘 1"), (0x62, "小键盘 2"), (0x63, "小键盘 3"), (0x64, "小键盘 4"),
            (0x65, "小键盘 5"), (0x66, "小键盘 6"), (0x67, "小键盘 7"), (0x68, "小键盘 8"), (0x69, "小键盘 9"),
            (0x6A, "小键盘 *"), (0x6B, "小键盘 +"), (0x6D, "小键盘 -"), (0x6E, "小键盘 ."), (0x6F, "小键盘 /"),
            (0xAD, "静音"), (0xAE, "音量 -"), (0xAF, "音量 +"),
            (0xB3, "播放 / 暂停"), (0xB0, "下一曲"), (0xB1, "上一曲"), (0xB2, "停止播放"),
            (0xA6, "浏览器后退"), (0xA7, "浏览器前进"), (0xA8, "浏览器刷新"), (0xAA, "浏览器搜索"), (0xAC, "浏览器主页"),
            (0xB4, "邮件"), (0xB6, "此电脑"), (0xB7, "计算器"),
        });
        return l;
    }

    private static readonly Dictionary<int, string> Map = Keys.ToDictionary(k => k.Vk, k => k.Name);

    public static string Key(int vk) => vk switch
    {
        0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
        _ => Map.TryGetValue(vk, out var n) ? n : $"VK 0x{vk:X2}",
    };

    public static readonly MouseButton[] TriggerButtons =
        [MouseButton.Middle, MouseButton.X1, MouseButton.X2, MouseButton.WheelLeft, MouseButton.WheelRight, MouseButton.WheelUp, MouseButton.WheelDown];
    public static readonly MouseButton[] ActionButtons =
        [MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.X1, MouseButton.X2, MouseButton.WheelUp, MouseButton.WheelDown, MouseButton.WheelLeft, MouseButton.WheelRight];

    public static string Mouse(MouseButton b) => b switch
    {
        MouseButton.Left => "鼠标左键",
        MouseButton.Right => "鼠标右键",
        MouseButton.Middle => "鼠标中键",
        MouseButton.X1 => "鼠标侧键（后退）",
        MouseButton.X2 => "鼠标侧键（前进）",
        MouseButton.WheelUp => "滚轮向上",
        MouseButton.WheelDown => "滚轮向下",
        MouseButton.WheelLeft => "滚轮向左（倾斜）",
        MouseButton.WheelRight => "滚轮向右（倾斜）",
        _ => b.ToString(),
    };

    public static string Mods(KeyModifiers m)
    {
        var parts = new List<string>();
        if (m.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (m.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    private static string With(KeyModifiers m, string main) => m == 0 ? main : main.Length == 0 ? Mods(m) : $"{Mods(m)} + {main}";

    public static string Trigger(InputTrigger t) => t.Kind switch
    {
        TriggerKind.Key => With(t.Modifiers, Key(t.Key)),
        TriggerKind.Mouse => With(t.Modifiers, Mouse(t.Button)),
        _ => With(t.Modifiers, t.Name ?? $"设备按键 {t.ControlId}"),
    };

    public static string Action(RemapAction a, IEnumerable<MacroDef> macros) => a.Kind switch
    {
        RemapActionKind.Keys => With(a.Modifiers, a.Key == 0 ? "" : Key(a.Key)),
        RemapActionKind.Mouse => Mouse(a.Button),
        RemapActionKind.Text => $"输入文本「{Short(a.Text)}」",
        RemapActionKind.Macro => $"宏「{macros.FirstOrDefault(m => m.Id == a.MacroId)?.Name ?? "（已删除）"}」",
        RemapActionKind.Run => $"启动 {Path.GetFileName(a.Path ?? "")}",
        RemapActionKind.Disable => "禁用",
        _ => "",
    };

    public static string Step(MacroStep s) => s.Kind switch
    {
        MacroStepKind.KeyTap => "按键  " + With(s.Modifiers, s.Key == 0 ? "" : Key(s.Key)),
        MacroStepKind.KeyDown => "按下  " + Key(s.Key),
        MacroStepKind.KeyUp => "抬起  " + Key(s.Key),
        MacroStepKind.Text => $"输入文本  「{Short(s.Text)}」",
        MacroStepKind.Delay => $"等待  {s.Ms} 毫秒",
        MacroStepKind.MouseClick => "点击  " + Mouse(s.Button),
        MacroStepKind.MouseDown => "按下  " + Mouse(s.Button),
        MacroStepKind.MouseUp => "抬起  " + Mouse(s.Button),
        _ => "",
    };

    private static string Short(string? s)
    {
        s = (s ?? "").ReplaceLineEndings("⏎");
        return s.Length > 24 ? s[..23] + "…" : s;
    }
}
