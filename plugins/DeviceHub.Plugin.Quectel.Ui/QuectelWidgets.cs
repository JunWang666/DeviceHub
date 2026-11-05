using DeviceHub.Contracts;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.Plugin.Quectel.Ui;

/// <summary>主页小组件：signal（运营商、制式、频段与信号强度）。</summary>
public sealed class QuectelWidgets : IPluginWidgetProvider
{
    public FrameworkElement CreateWidget(string widgetId, WidgetSize size, IPluginUiBridge bridge)
    {
        var root = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        WidgetKit.Bind<QuectelState>(root, bridge, s => root.Content = Render(s, size));
        return root;
    }

    private static FrameworkElement Render(QuectelState? s, WidgetSize size)
    {
        if (s is null) return WidgetKit.Notice("读取中…");
        if (!s.Authorized) return WidgetKit.Notice("未授权");
        if (!s.Connected) return WidgetKit.Notice("未连接");
        if (!s.Driver.AtPortReady) return WidgetKit.Notice("需要安装驱动");
        if (s.Network.Cfun == 4) return WidgetKit.Notice("飞行模式");
        var n = s.Network;
        bool registered = n.EpsRegistration is 1 or 5;
        if (!registered) return WidgetKit.Notice(AtParse.RegText(n.EpsRegistration));

        var glyph = char.ConvertFromUtf32(0xE86C + Math.Clamp(s.Signal.Bars, 0, 4));
        var value = s.Signal.Rsrp is { } p ? $"{p}" : s.Signal.Rssi is { } r ? $"{r}" : "—";
        var sp = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var metric = WidgetKit.Metric(glyph, value, size == WidgetSize.Small ? 24 : 28);
        metric.Children.Add(new TextBlock { Text = "dBm", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4), Foreground = WidgetKit.SecondaryBrush });
        sp.Children.Add(metric);
        var band = n.Band.Replace("LTE BAND ", "B");
        sp.Children.Add(WidgetKit.Caption(size == WidgetSize.Small
            ? n.Operator
            : string.Join(" · ", new[] { n.Operator, n.Act, band, n.EpsRegistration == 5 ? "漫游" : "" }.Where(x => x.Length > 0))));
        if (size != WidgetSize.Small)
            sp.Children.Add(WidgetKit.Caption($"RSRQ {s.Signal.Rsrq} dB · SINR {s.Signal.Sinr:0.#} dB{(n.Ip.Length > 0 ? " · " + n.Ip : "")}"));
        return sp;
    }
}
