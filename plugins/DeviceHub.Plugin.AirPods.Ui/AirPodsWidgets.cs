using DeviceHub.Contracts;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeviceHub.Plugin.AirPods.Ui;

/// <summary>主页小组件：battery（左 / 右 / 充电盒电量）、listening（降噪模式切换，需要 AAP 驱动）。</summary>
public sealed class AirPodsWidgets : IPluginWidgetProvider
{
    public FrameworkElement CreateWidget(string widgetId, WidgetSize size, IPluginUiBridge bridge) => widgetId switch
    {
        "listening" => Listening(bridge),
        _ => Battery(size, bridge),
    };

    private static string? Unavailable(AirPodsStateDto? s) =>
        s is null ? "读取中…" : !s.Authorized ? "未授权" : !s.Connected ? "未连接" : null;

    private static FrameworkElement Battery(WidgetSize size, IPluginUiBridge bridge)
    {
        var root = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        WidgetKit.Bind<AirPodsStateDto>(root, bridge, s => root.Content = RenderBattery(s, size));
        return root;
    }

    private static FrameworkElement RenderBattery(AirPodsStateDto? s, WidgetSize size)
    {
        if (Unavailable(s) is { } why) return WidgetKit.Notice(why);
        var parts = new (string Name, PartDto Part)[] { ("左", s!.Left), ("右", s.Right), ("盒", s.Case) };
        if (parts.All(p => p.Part.Battery is null)) return WidgetKit.Notice("等待耳机广播…");

        if (size == WidgetSize.Small)
        {
            // 小卡片：耳机取两只里低的那只，再加充电盒
            var buds = new[] { s.Left, s.Right }.Where(p => p.Battery is not null).ToList();
            var sp = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (buds.Count > 0)
            {
                var low = buds.MinBy(p => p.Battery)!;
                sp.Children.Add(WidgetKit.Metric(WidgetKit.BatteryGlyph(low.Battery!.Value, low.Charging), $"{low.Battery}%", 24));
            }
            sp.Children.Add(WidgetKit.Caption(s.Case.Battery is { } c ? $"充电盒 {c}%{(s.Case.Charging ? " · 充电中" : "")}" : s.ModelName));
            return sp;
        }

        var grid = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < parts.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (name, part) = parts[i];
            var col = new StackPanel { Spacing = 2, Opacity = part.Battery is null ? 0.5 : 1 };
            col.Children.Add(WidgetKit.Caption(name + (part.InEar ? " · 佩戴中" : "")));
            col.Children.Add(WidgetKit.Metric(part.Battery is { } v ? WidgetKit.BatteryGlyph(v, part.Charging) : "",
                part.Battery is { } p ? $"{p}%" : "—", 20));
            Grid.SetColumn(col, i);
            grid.Children.Add(col);
        }
        if (size != WidgetSize.Large) return grid;
        var big = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        big.Children.Add(grid);
        big.Children.Add(WidgetKit.Caption($"{s.ModelName} · {(s.BatterySource == "aap" ? "AAP 1% 精度" : "BLE 广播 10% 精度")}"));
        return big;
    }

    /// <summary>降噪模式：一排按钮，当前模式高亮；没有 AAP 驱动时说明原因。</summary>
    private static FrameworkElement Listening(IPluginUiBridge bridge)
    {
        var modes = new (int Mode, string Name, string Glyph)[] { (1, "关闭", ""), (2, "降噪", ""), (3, "通透", ""), (4, "自适应", "") };
        var bar = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new List<(int Mode, ToggleButton Button)>();
        var notice = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var root = new Grid();
        root.Children.Add(bar);
        root.Children.Add(notice);

        AirPodsStateDto? state = null;
        bool applying = false;
        void Apply(AirPodsStateDto? s)
        {
            state = s;
            string? why = Unavailable(s) ?? (!s!.SupportsAnc ? "这副耳机没有降噪" : !s.AapConnected ? "需要 AAP 驱动（见设备页说明）" : null);
            bar.Visibility = why is null ? Visibility.Visible : Visibility.Collapsed;
            notice.Content = why is null ? null : WidgetKit.Notice(why);
            if (why is not null || s is null) return;

            var visible = modes.Where(m => m.Mode != 4 || s.SupportsAdaptive).ToList();
            if (buttons.Select(b => b.Mode).SequenceEqual(visible.Select(m => m.Mode)) is false)
            {
                bar.Children.Clear(); bar.ColumnDefinitions.Clear(); buttons.Clear();
                foreach (var (m, name, glyph) in visible)
                {
                    var content = new StackPanel { Spacing = 2 };
                    content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
                    content.Children.Add(new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
                    var btn = new ToggleButton { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 6, 4, 6) };
                    int mode = m;
                    btn.Click += (_, _) =>
                    {
                        if (applying) return;
                        if (state?.ListeningMode == mode) { btn.IsChecked = true; return; }
                        WidgetKit.Invoke<AirPodsStateDto>(bridge, "SetListeningMode", new { mode }, Apply);
                    };
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(btn, bar.Children.Count);
                    bar.Children.Add(btn);
                    buttons.Add((m, btn));
                }
            }
            applying = true;
            foreach (var (m, b) in buttons) b.IsChecked = s.ListeningMode == m;
            applying = false;
        }
        WidgetKit.Bind<AirPodsStateDto>(root, bridge, Apply);
        return root;
    }
}
