using DeviceHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace DeviceHub.App.Services;

/// <summary>
/// 设备详情弹窗：核心信息（设备、授权、HID 通道）+ 接管插件通过 DescribeAsync 提供的信息。
/// 列表/页头只放名称、插件、接入时间，其余都收在这里。
/// </summary>
public static class DeviceDetailsDialog
{
    public static async Task ShowAsync(XamlRoot root, string deviceKey, GrantInfo? fallbackGrant = null)
    {
        DeviceDetails? d = null;
        try { if (AppState.Instance.Client.Agent is { } a) d = await a.GetDeviceDetailsAsync(deviceKey); }
        catch { /* Agent 断开时只显示授权记录 */ }

        var panel = new StackPanel { Spacing = 4, MinWidth = 440 };
        var grant = d?.Grant ?? fallbackGrant;

        if (d is not null)
        {
            var dev = d.Device;
            Section(panel, "设备", first: true);
            Row(panel, "名称", dev.DisplayName);
            Row(panel, "状态", dev.Connected ? "已连接" : "未连接");
            Row(panel, "接口", Fmt.Bus(dev));
            if (!string.IsNullOrEmpty(dev.Serial)) Row(panel, "序列号", dev.Serial!);
            Row(panel, "设备标识", dev.DeviceKey);
        }
        else
        {
            Section(panel, "设备", first: true);
            Row(panel, "状态", "未连接");
            Row(panel, "设备标识", deviceKey);
        }

        if (grant is not null)
        {
            Section(panel, "管理");
            var plugin = d?.Plugin;
            Row(panel, "插件", plugin is null ? grant.PluginDisplayName : $"{plugin.DisplayName}（v{plugin.Version}，{Fmt.State(plugin.State)}）");
            Row(panel, "插件 Id", grant.PluginId);
            Row(panel, "授权", Fmt.Scope(grant.Scope));
            Row(panel, "接入时间", Fmt.Time(grant.GrantedAt));
        }

        if (d?.Description is { Details.Count: > 0 } desc)
        {
            Section(panel, "插件提供的信息");
            foreach (var item in desc.Details) Row(panel, item.Name, item.Value);
        }

        if (d?.Device.Channels is { Count: > 0 } chans)
        {
            Section(panel, "HID 通道");
            foreach (var c in chans)
                Row(panel, c.Id, $"输入 {c.InputLength} · 输出 {c.OutputLength} · 特性 {c.FeatureLength} 字节");
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = d?.Device.DisplayName ?? grant?.DeviceDisplayName ?? "设备详情",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private static void Section(Panel p, string title, bool first = false) =>
        p.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, first ? 0 : 16, 0, 4),
        });

    private static void Row(Panel p, string name, string value)
    {
        var g = new Grid { ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = name,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var val = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        val.Inlines.Add(new Run { Text = value });
        Grid.SetColumn(val, 1);
        g.Children.Add(label);
        g.Children.Add(val);
        p.Children.Add(g);
    }
}
