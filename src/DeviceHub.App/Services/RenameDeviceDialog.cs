using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.App.Services;

/// <summary>给已授权的设备起名字。留空保存 = 恢复默认名。</summary>
public static class RenameDeviceDialog
{
    /// <summary>改名成功返回 true（调用方随后刷新列表）。</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, string pluginId, string deviceKey, string? customName, string defaultName)
    {
        if (AppState.Instance.Client.Agent is not { } agent) return false;
        var box = new TextBox
        {
            Text = customName ?? "",
            PlaceholderText = string.IsNullOrEmpty(defaultName) ? "设备名称" : defaultName,
            MaxLength = 64,
        };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "重命名设备",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = string.IsNullOrEmpty(defaultName) ? "名字只保存在 DeviceHub 里，不会写入设备。" : $"默认名：{defaultName}。留空即恢复默认名。名字只保存在 DeviceHub 里。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        await agent.RenameDeviceAsync(pluginId, deviceKey, box.Text);
        await AppState.Instance.RefreshAsync(); // 左侧导航与页头用新名字
        return true;
    }
}
