using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeviceHub.Plugin.Gk65.Ui;

public sealed partial class Gk65Page : UserControl
{
    public Gk65ViewModel Vm { get; }

    public Gk65Page(IPluginUiBridge bridge)
    {
        Vm = new Gk65ViewModel(bridge);
        InitializeComponent();
        BuildKeyboard();
        Loaded += async (_, _) => { AttachKeyTest(true); await Vm.LoadAsync(); };
        Unloaded += (_, _) => AttachKeyTest(false);
    }

    private void Sections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var sel = sender.SelectedItem;
        LightingPanel.Visibility = sel == LightingTab ? Visibility.Visible : Visibility.Collapsed;
        KeysPanel.Visibility = sel == KeysTab ? Visibility.Visible : Visibility.Collapsed;
    }

    // —— 键位图 ——

    /// <summary>键位图坐标单位 → 有效像素（键帽约 50px，Viewbox 再按可用宽度缩小）。</summary>
    private const double KeyScale = 1.5;

    private readonly Dictionary<int, (Border Cap, TextBlock Text, string Label)> _keys = new();

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];

    /// <summary>按官方键位图（positions.xml）在 Canvas 上摆放每个物理键，只做展示。</summary>
    private void BuildKeyboard()
    {
        var layout = Vm.Catalog.Layout.Keys;
        KeyCanvas.Width = layout.Max(k => k.X + k.W) * KeyScale;
        KeyCanvas.Height = layout.Max(k => k.Y + k.H) * KeyScale;

        foreach (var pos in layout)
        {
            var text = new TextBlock
            {
                Text = pos.Label,
                Style = Res<Style>("BodyTextBlockStyle"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Res<Brush>(pos.Id == 0 ? "TextFillColorDisabledBrush" : "TextFillColorPrimaryBrush"),
            };
            if (pos.Label.Length > 3 && pos.W <= 33) text.FontSize = 12; // 1u 键帽放不下 "Menu" 这类长标签
            var cap = new Border
            {
                Width = pos.W * KeyScale - 4,
                Height = pos.H * KeyScale - 4,
                Padding = new Thickness(5, 4, 5, 4),
                CornerRadius = Res<CornerRadius>("ControlCornerRadius"),
                BorderThickness = new Thickness(1),
                BorderBrush = Res<Brush>("ControlStrokeColorDefaultBrush"),
                Background = Res<Brush>("ControlFillColorDefaultBrush"),
                Child = text,
            };
            Canvas.SetLeft(cap, pos.X * KeyScale);
            Canvas.SetTop(cap, pos.Y * KeyScale);
            if (pos.Id != 0) _keys[pos.Id] = (cap, text, pos.Label); // Fn 由固件处理，系统收不到
            KeyCanvas.Children.Add(cap);
        }
    }

    // —— 按键测试：只处理本窗口的按键事件，打开开关时才生效，不记录 ——

    private readonly SortedSet<int> _pressed = new();

    private bool KeyTestActive => KeyTestSwitch.IsOn && KeysPanel.Visibility == Visibility.Visible;

    /// <summary>
    /// 挂在本窗口内容根上：窗口重新激活后焦点可能不在本页，挂在页面内收不到事件。
    /// 仍然只是本窗口的按键事件，窗口不在前台时收不到任何按键。
    /// </summary>
    private void AttachKeyTest(bool attach)
    {
        if (XamlRoot?.Content is not UIElement root) return;
        root.PreviewKeyDown -= Root_PreviewKeyDown;
        root.PreviewKeyUp -= Root_PreviewKeyUp;
        if (!attach) return;
        root.PreviewKeyDown += Root_PreviewKeyDown;
        root.PreviewKeyUp += Root_PreviewKeyUp;
    }

    private void KeyTest_Toggled(object sender, RoutedEventArgs e)
    {
        foreach (var id in _pressed.ToList()) SetPressed(id, false);
        _pressed.Clear();
        PressedText.Text = KeyTestSwitch.IsOn ? "请按键…" : "";
    }

    private void Root_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!KeyTestActive) return;
        e.Handled = true; // 测试时不让空格/方向键/Tab 去操作界面
        if (VirtualKeyMap.ToHid(e.Key, e.KeyStatus) is { } hid && _pressed.Add(hid)) SetPressed(hid, true);
        PressedText.Text = Describe(e.Key);
    }

    private void Root_PreviewKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!KeyTestActive) return;
        e.Handled = true;
        if (VirtualKeyMap.ToHid(e.Key, e.KeyStatus) is { } hid && _pressed.Remove(hid)) SetPressed(hid, false);
    }

    private void SetPressed(int hid, bool down)
    {
        if (!_keys.TryGetValue(hid, out var k)) return;
        k.Cap.Background = Res<Brush>(down ? "AccentFillColorDefaultBrush" : "ControlFillColorDefaultBrush");
        k.Text.Foreground = Res<Brush>(down ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
    }

    /// <summary>"Shift + A" 形式（修饰键在前）；不在键位图上的键（如经 Fn 层得到的 F1）用 VirtualKey 名称。</summary>
    private string Describe(Windows.System.VirtualKey last)
    {
        var names = _pressed.OrderBy(id => id >= 224 ? 0 : 1).ThenBy(id => id)
            .Select(id => _keys.TryGetValue(id, out var k) ? (id == 44 ? "Space" : k.Label) : "")
            .Where(n => n != "").ToList();
        var lastHid = VirtualKeyMap.ToHid(last, default);
        if (lastHid is null || !_keys.ContainsKey(lastHid.Value)) names.Add(last.ToString());
        return names.Count == 0 ? last.ToString() : string.Join(" + ", names.Distinct());
    }
}

/// <summary>插件 UI 入口。</summary>
public sealed class Gk65PageEntry : IPluginPage
{
    public FrameworkElement CreatePage(IPluginUiBridge bridge) => new Gk65Page(bridge);
}
