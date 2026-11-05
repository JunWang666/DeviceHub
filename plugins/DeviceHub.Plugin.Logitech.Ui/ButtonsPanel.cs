using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeviceHub.Plugin.Logitech.Ui;

/// <summary>
/// 「按键」分页：设备上的可编程按键（HID++ 0x1B04）。可接管的按键有开关；接管后按下时高亮，
/// 并交给 DeviceHub 的「工具 › 按键映射」处理（例如把手势键映射成 Win+Tab）。
/// 纯代码构建：App 运行时解析插件 XAML 时认不出插件自己定义的控件类型。
/// </summary>
public sealed class ButtonsPanel : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly TextBlock _empty;
    private readonly Dictionary<int, Row> _rows = new();
    private readonly Action<int, bool> _setDivert;

    public ButtonsPanel(Action<int, bool> setDivert)
    {
        _setDivert = setDivert;
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextFillColorSecondaryBrush"),
            Text = "手势键等按键默认由鼠标自己处理，系统收不到，所以「按键测试」里看不到。打开「接管」后，按键改由 DeviceHub 处理："
                 + "按下时这里会高亮，并可以在左侧「工具 › 按键映射」里把它映射成任意按键、快捷键或宏。"
                 + "接管后按键原来的功能不再生效；设备重新连接时会自动重新接管。",
        };
        _empty = new TextBlock { Text = "这个设备没有报告可编程按键（不支持 HID++ 0x1B04），或设备未连接。", Foreground = Res("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        Content = new StackPanel { Spacing = 12, Children = { intro, _empty, _list } };
    }

    public void Update(IReadOnlyList<ControlDto> controls, bool online)
    {
        _empty.Visibility = controls.Count == 0 || !online ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        // 按键列表没变（只是按下状态 / 接管状态变了）时原地更新，免得每按一下都重建、开关闪动
        if (controls.Count != _rows.Count || controls.Any(c => !_rows.ContainsKey(c.Cid)))
        {
            _rows.Clear();
            _list.Children.Clear();
            // 能接管的在前，再按 CID 排
            foreach (var c in controls.OrderBy(c => c.Divertable ? 0 : 1).ThenBy(c => c.Cid))
            {
                var row = new Row(c.Cid, _setDivert);
                _rows[c.Cid] = row;
                _list.Children.Add(row.Root);
            }
        }
        foreach (var c in controls) _rows[c.Cid].Set(c);
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private sealed class Row
    {
        public readonly Border Root;
        private readonly TextBlock _name, _detail, _pressed;
        private readonly ToggleSwitch _toggle;
        private readonly Border _dot;
        private bool _setting;

        public Row(int cid, Action<int, bool> setDivert)
        {
            _name = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            _detail = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Res("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
            _pressed = new TextBlock { Text = "按下", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
            _dot = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1), BorderBrush = Res("ControlStrongStrokeColorDefaultBrush"),
            };
            _toggle = new ToggleSwitch { OnContent = "接管", OffContent = "原功能", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            _toggle.Toggled += (_, _) => { if (!_setting) setDivert(cid, _toggle.IsOn); };

            var indicator = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _dot, _pressed }, VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _name, _detail } };
            var grid = new Grid
            {
                ColumnSpacing = 16,
                ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } },
            };
            Grid.SetColumn(indicator, 1);
            Grid.SetColumn(_toggle, 2);
            grid.Children.Add(text);
            grid.Children.Add(indicator);
            grid.Children.Add(_toggle);
            Root = new Border
            {
                Padding = new Thickness(16, 10, 16, 10),
                MinHeight = 64,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = Res("CardStrokeColorDefaultBrush"),
                Background = Res("CardBackgroundFillColorDefaultBrush"),
                Child = grid,
            };
        }

        public void Set(ControlDto c)
        {
            _name.Text = c.Name;
            var tags = new List<string> { $"CID 0x{c.Cid:X4}" };
            if (c.IsMouse) tags.Add("鼠标按键");
            tags.Add(c.Divertable ? (c.Diverted ? "已接管：由 DeviceHub 处理" : "可接管") : "不能接管");
            _detail.Text = string.Join(" · ", tags);
            _setting = true;
            _toggle.IsOn = c.Diverted;
            _toggle.IsEnabled = c.Divertable;
            _setting = false;
            _toggle.Visibility = c.Divertable ? Visibility.Visible : Visibility.Collapsed;
            // 只有接管后才收得到按下事件
            _dot.Background = c.Pressed ? Res("AccentFillColorDefaultBrush") : Res("ControlFillColorTransparentBrush");
            _pressed.Foreground = Res(c.Pressed ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            _pressed.Text = c.Diverted ? (c.Pressed ? "按下" : "未按") : "—";
            Root.BorderBrush = c.Pressed ? Res("AccentFillColorDefaultBrush") : Res("CardStrokeColorDefaultBrush");
        }
    }
}
