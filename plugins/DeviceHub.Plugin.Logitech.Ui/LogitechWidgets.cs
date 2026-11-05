using DeviceHub.Contracts;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.Plugin.Logitech.Ui;

/// <summary>主页小组件：battery（电量，接收器上显示全部配对设备）、dpi（DPI 快速切换）。</summary>
public sealed class LogitechWidgets : IPluginWidgetProvider
{
    public FrameworkElement CreateWidget(string widgetId, WidgetSize size, IPluginUiBridge bridge) => widgetId switch
    {
        "dpi" => Dpi(bridge),
        _ => Battery(size, bridge),
    };

    /// <summary>不能用时的提示；null 表示可以显示。</summary>
    private static string? Unavailable(LogitechStateDto? s) =>
        s is null ? "读取中…" : !s.Authorized ? "未授权" : !s.Connected ? "未连接" : null;

    private static FrameworkElement Battery(WidgetSize size, IPluginUiBridge bridge)
    {
        var root = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        WidgetKit.Bind<LogitechStateDto>(root, bridge, s => root.Content = RenderBattery(s, size));
        return root;
    }

    private static FrameworkElement RenderBattery(LogitechStateDto? s, WidgetSize size)
    {
        if (Unavailable(s) is { } why) return WidgetKit.Notice(why);
        // 接收器：每个配对设备一行；直连设备 / 子设备页：只有它自己。
        if (s!.IsReceiver && !s.IsChild)
        {
            if (s.Devices.Count == 0) return WidgetKit.Notice("接收器上没有配对设备");
            var list = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            foreach (var d in s.Devices.Take(size == WidgetSize.Large ? 6 : 2))
            {
                var row = new Grid { ColumnSpacing = 8, Opacity = d.Online ? 1 : 0.55 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = d.Name.Length > 0 ? d.Name : $"设备 {d.Index}", TextTrimming = TextTrimming.CharacterEllipsis });
                var value = !d.Online ? WidgetKit.Caption("未连接")
                    : d.Battery?.Percent is { } p ? (FrameworkElement)WidgetKit.Metric(WidgetKit.BatteryGlyph(p, d.Battery.Charging), $"{p}%", 14)
                    : WidgetKit.Caption("—");
                Grid.SetColumn(value, 1); row.Children.Add(value);
                list.Children.Add(row);
            }
            if (s.Devices.Count > list.Children.Count) list.Children.Add(WidgetKit.Caption($"还有 {s.Devices.Count - list.Children.Count} 个设备"));
            return list;
        }

        var dev = s.Devices.FirstOrDefault();
        if (dev?.Battery is not { } b || b.Percent is not { } pct)
            return WidgetKit.Notice(dev is { Online: false } ? "未连接" : "这个设备不报告电量");
        var sp = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(WidgetKit.Metric(WidgetKit.BatteryGlyph(pct, b.Charging), $"{pct}%"));
        var note = b.Charging ? "充电中" : b.Estimated ? "按电压估算" : b.Status.Length > 0 ? b.Status : "";
        if (size != WidgetSize.Small) sp.Children.Add(new ProgressBar { Value = pct, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) });
        if (note.Length > 0) sp.Children.Add(WidgetKit.Caption(note));
        return sp;
    }

    /// <summary>DPI：当前值 + 上一档 / 下一档（有预设档位时按档位走，否则按步进）。</summary>
    private static FrameworkElement Dpi(IPluginUiBridge bridge)
    {
        var value = new TextBlock { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var note = WidgetKit.Caption("");
        var down = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8) };
        var up = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8) };
        ToolTipService.SetToolTip(down, "降低 DPI");
        ToolTipService.SetToolTip(up, "提高 DPI");
        var notice = new ContentControl { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(value);
        text.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(down);
        buttons.Children.Add(up);
        var main = new Grid { ColumnSpacing = 12 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.Children.Add(text);
        Grid.SetColumn(buttons, 1); main.Children.Add(buttons);
        var root = new Grid();
        root.Children.Add(main);
        root.Children.Add(notice);

        HidppDeviceDto? dev = null;
        void Apply(LogitechStateDto? s)
        {
            dev = s is null || (s.IsReceiver && !s.IsChild) ? null : s.Devices.FirstOrDefault();
            string? why = Unavailable(s) ?? (s!.IsReceiver && !s.IsChild ? "请为接收器上的具体设备添加这个小组件"
                : dev?.Dpi is null ? (dev is { Online: false } ? "未连接" : "这个设备不支持调 DPI") : null);
            main.Visibility = why is null ? Visibility.Visible : Visibility.Collapsed;
            notice.Visibility = why is null ? Visibility.Collapsed : Visibility.Visible;
            notice.Content = why is null ? null : WidgetKit.Notice(why);
            if (why is not null) return;
            var d = dev!.Dpi!;
            value.Text = $"{d.Value} DPI";
            note.Text = d.Choices.Count > 0 ? $"{d.Choices.Count} 档预设" : $"{d.Min}–{d.Max}";
            down.IsEnabled = Step(d, -1) is not null;
            up.IsEnabled = Step(d, +1) is not null;
        }
        void Go(int dir)
        {
            if (dev?.Dpi is not { } d || Step(d, dir) is not { } next) return;
            down.IsEnabled = up.IsEnabled = false; // 等设备回显
            WidgetKit.Invoke<LogitechStateDto>(bridge, "SetDpi", new { index = dev.Index, dpi = next }, Apply);
        }
        down.Click += (_, _) => Go(-1);
        up.Click += (_, _) => Go(+1);
        WidgetKit.Bind<LogitechStateDto>(root, bridge, Apply);
        return root;
    }

    private static int? Step(DpiDto d, int dir)
    {
        if (d.Choices.Count > 0)
        {
            var sorted = d.Choices.Distinct().OrderBy(x => x).ToList();
            return dir > 0 ? sorted.Where(x => x > d.Value).Cast<int?>().FirstOrDefault()
                           : sorted.Where(x => x < d.Value).Cast<int?>().LastOrDefault();
        }
        if (d.Step <= 0) return null;
        // 没有预设档位：每次走 100（或设备步进的整数倍），不越界。
        int step = Math.Max(d.Step, (int)Math.Ceiling(100.0 / d.Step) * d.Step);
        int next = d.Value + dir * step;
        return next < d.Min || next > d.Max ? null : next;
    }
}
