using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.Plugin.GameSir.Ui;

public sealed partial class GameSirPage : UserControl
{
    public GameSirViewModel Vm { get; }

    public GameSirPage(IPluginUiBridge bridge)
    {
        Vm = new GameSirViewModel(bridge);
        InitializeComponent();
        Loaded += async (_, _) => await Vm.LoadAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm.Refresh();
}

/// <summary>插件 UI 入口。</summary>
public sealed class GameSirPageEntry : IPluginPage
{
    public FrameworkElement CreatePage(IPluginUiBridge bridge) => new GameSirPage(bridge);
}
