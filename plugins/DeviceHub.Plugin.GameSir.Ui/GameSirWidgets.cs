using DeviceHub.Contracts;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.Plugin.GameSir.Ui;

/// <summary>主页小组件：battery（手柄电量）。</summary>
public sealed class GameSirWidgets : IPluginWidgetProvider
{
    public FrameworkElement CreateWidget(string widgetId, WidgetSize size, IPluginUiBridge bridge)
    {
        var root = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        WidgetKit.Bind<GameSirStateDto>(root, bridge, s => root.Content = Render(s, size));
        return root;
    }

    private static FrameworkElement Render(GameSirStateDto? s, WidgetSize size)
    {
        if (s is null) return WidgetKit.Notice("读取中…");
        if (!s.Authorized) return WidgetKit.Notice("未授权");
        if (!s.Connected) return WidgetKit.Notice("未连接");
        if (s.Battery is not { } b) return WidgetKit.Notice(s.Wireless ? "正在读取电量…" : "有线连接，无电池信息");
        var sp = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(WidgetKit.Metric(WidgetKit.BatteryGlyph(b, false), $"{b}%"));
        if (size != WidgetSize.Small) sp.Children.Add(new ProgressBar { Value = b, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) });
        sp.Children.Add(WidgetKit.Caption(s.Wireless ? "2.4G 无线" : "有线"));
        return sp;
    }
}
