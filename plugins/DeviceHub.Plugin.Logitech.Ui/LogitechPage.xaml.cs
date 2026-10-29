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
        Loaded += async (_, _) => await Vm.LoadAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm.Refresh();
}

/// <summary>插件 UI 入口。</summary>
public sealed class LogitechPageEntry : IPluginPage
{
    public FrameworkElement CreatePage(IPluginUiBridge bridge) => new LogitechPage(bridge);
}
