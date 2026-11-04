using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.Plugin.Logitech.Ui;

public sealed partial class LogitechPage : UserControl
{
    public LogitechViewModel Vm { get; }

    public LogitechPage(IPluginUiBridge bridge)
    {
        Vm = new LogitechViewModel(bridge);
        InitializeComponent();
        MouseTestHost.Child = new MouseTestPanel();
        Loaded += async (_, _) => await Vm.LoadAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm.Refresh();
    private void StartPairing_Click(object sender, RoutedEventArgs e) => Vm.StartPairing();
    private void StopPairing_Click(object sender, RoutedEventArgs e) => Vm.StopPairing();

    private async void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PairedRow row) return;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"取消配对 {row.Title}？",
            Content = "设备会从接收器上移除，它在本软件里的名称和设置也会清除。之后要用需要重新配对。",
            PrimaryButtonText = "取消配对",
            CloseButtonText = "保留",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) Vm.Unpair(row.Slot);
    }
}

/// <summary>插件 UI 入口。</summary>
public sealed class LogitechPageEntry : IPluginPage
{
    public FrameworkElement CreatePage(IPluginUiBridge bridge) => new LogitechPage(bridge);
}
