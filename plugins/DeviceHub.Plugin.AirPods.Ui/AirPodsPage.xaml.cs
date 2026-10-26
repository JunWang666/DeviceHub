using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.Plugin.AirPods.Ui;

public sealed partial class AirPodsPage : UserControl
{
    public AirPodsViewModel Vm { get; }

    public AirPodsPage(IPluginUiBridge bridge)
    {
        Vm = new AirPodsViewModel(bridge);
        InitializeComponent();
        Loaded += async (_, _) => await Vm.LoadAsync();
    }

    /// <summary>单选按钮只负责发请求；选中状态跟随后端回推（耳机回显）。</summary>
    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var mode)) Vm.SetListeningMode(mode);
    }
}

/// <summary>插件 UI 入口。</summary>
public sealed class AirPodsPageEntry : IPluginPage
{
    public FrameworkElement CreatePage(IPluginUiBridge bridge) => new AirPodsPage(bridge);
}
