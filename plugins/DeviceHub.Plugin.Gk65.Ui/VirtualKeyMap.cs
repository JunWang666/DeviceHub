using Windows.System;
using Windows.UI.Core;

namespace DeviceHub.Plugin.Gk65.Ui;

/// <summary>
/// 窗口内按键事件（VirtualKey + 扫描码）→ HID 键盘页使用码，用于在键位图上高亮按下的键。
/// 只处理本窗口收到的按键事件，不做系统级监听。
/// </summary>
internal static class VirtualKeyMap
{
    private static readonly Dictionary<int, int> Map = Build();

    private static Dictionary<int, int> Build()
    {
        var m = new Dictionary<int, int>();
        for (int i = 0; i < 26; i++) m[0x41 + i] = 4 + i;         // A–Z
        for (int i = 1; i <= 9; i++) m[0x30 + i] = 29 + i;        // 1–9
        m[0x30] = 39;                                              // 0
        for (int i = 0; i < 12; i++) m[0x70 + i] = 58 + i;        // F1–F12
        int[,] pairs =
        {
            { 0x0D, 40 }, { 0x1B, 41 }, { 0x08, 42 }, { 0x09, 43 }, { 0x20, 44 },
            { 0xBD, 45 }, { 0xBB, 46 }, { 0xDB, 47 }, { 0xDD, 48 }, { 0xDC, 49 },
            { 0xBA, 51 }, { 0xDE, 52 }, { 0xC0, 53 }, { 0xBC, 54 }, { 0xBE, 55 }, { 0xBF, 56 },
            { 0x14, 57 }, { 0x2D, 73 }, { 0x24, 74 }, { 0x21, 75 }, { 0x2E, 76 }, { 0x23, 77 }, { 0x22, 78 },
            { 0x27, 79 }, { 0x25, 80 }, { 0x28, 81 }, { 0x26, 82 },
            { 0x5D, 101 }, { 0x5B, 227 }, { 0x5C, 231 },
        };
        for (int i = 0; i < pairs.GetLength(0); i++) m[pairs[i, 0]] = pairs[i, 1];
        return m;
    }

    /// <summary>返回 HID 使用码；无法识别时为 null。</summary>
    public static int? ToHid(VirtualKey key, CorePhysicalKeyStatus status) => key switch
    {
        // 通用修饰键：用扩展键标志 / 扫描码区分左右
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => status.IsExtendedKey || key == VirtualKey.RightControl ? 228 : 224,
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => status.IsExtendedKey || key == VirtualKey.RightMenu ? 230 : 226,
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => status.ScanCode == 0x36 || key == VirtualKey.RightShift ? 229 : 225,
        _ => Map.TryGetValue((int)key, out var hid) ? hid : null,
    };
}
