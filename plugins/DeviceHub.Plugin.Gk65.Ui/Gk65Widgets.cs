using DeviceHub.Contracts;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.Plugin.Gk65.Ui;

/// <summary>主页小组件：lighting（面板灯效 + 亮度）。键盘不能回读设置，显示的是 DeviceHub 记住的值。</summary>
public sealed class Gk65Widgets : IPluginWidgetProvider
{
    public FrameworkElement CreateWidget(string widgetId, WidgetSize size, IPluginUiBridge bridge)
    {
        var modes = Gk65Catalog.Instance.LedModes.Where(m => m.I != 1).ToList(); // 与设备页一致：去掉不可用的模式
        var combo = new ComboBox { ItemsSource = modes, DisplayMemberPath = nameof(LedMode.Zh), HorizontalAlignment = HorizontalAlignment.Stretch };
        var bright = new Slider { Minimum = 0, Maximum = 5, StepFrequency = 1, TickFrequency = 1, TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.None };
        ToolTipService.SetToolTip(bright, "亮度（0 为关闭背光）");
        var note = WidgetKit.Caption("");

        var form = new Grid { ColumnSpacing = 12, RowSpacing = 2, VerticalAlignment = VerticalAlignment.Center };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brightRow = new Grid { ColumnSpacing = 6 };
        brightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brightRow.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(bright, 1); brightRow.Children.Add(bright);
        form.Children.Add(combo);
        Grid.SetColumn(brightRow, 1); form.Children.Add(brightRow);
        Grid.SetRow(note, 1); Grid.SetColumnSpan(note, 2); form.Children.Add(note);

        var root = new Grid();
        root.Children.Add(form);

        Gk65StateDto? state = null;
        bool applying = false;
        void Apply(Gk65StateDto? s)
        {
            if (s is null) { note.Text = "读取中…"; return; }
            state = s;
            applying = true;
            var p = s.Current.Panel;
            combo.SelectedIndex = modes.FindIndex(m => m.I == p.Mode);
            bright.Value = p.Brightness;
            bool editable = s.Authorized && s.Connected;
            combo.IsEnabled = bright.IsEnabled = editable;
            note.Text = !s.Authorized ? "未授权" : !s.Connected ? "未连接 · 上次保存的设置" : s.Status is { Length: > 0 } st ? st : "键盘不支持回读，显示的是上次保存的设置";
            applying = false;
        }
        void Push()
        {
            if (applying || state is null) return;
            var p = state.Current.Panel;
            if (combo.SelectedItem is LedMode m) p.Mode = m.I;
            p.Brightness = (int)Math.Round(bright.Value);
            WidgetKit.Invoke<Gk65StateDto>(bridge, "SetPanelLighting", new { mode = p.Mode, speed = p.Speed, brightness = p.Brightness }, Apply);
        }
        combo.SelectionChanged += (_, _) => Push();
        // 拖动滑块时只在松手 / 档位变化时下发：StepFrequency=1，ValueChanged 只在跨档时触发
        bright.ValueChanged += (_, e) => { if (Math.Round(e.NewValue) != Math.Round(e.OldValue)) Push(); };
        WidgetKit.Bind<Gk65StateDto>(root, bridge, Apply);
        return root;
    }
}
