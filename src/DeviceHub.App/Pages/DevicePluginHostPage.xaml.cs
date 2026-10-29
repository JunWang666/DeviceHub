using DeviceHub.App.Services;
using DeviceHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeviceHub.App.Pages;

public sealed partial class DevicePluginHostPage : Page
{
    private DeviceInfo? _dev;

    public DevicePluginHostPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not DeviceInfo dev) return;
        _dev = dev;
        // 标题与副标题由插件决定（DescribeAsync / manifest 名称），核心补上插件名与接入时间。
        TitleText.Text = dev.DisplayName;
        SubtitleText.Text = Fmt.Managed(dev.Subtitle, dev.OwnerPluginName, dev.GrantedAt);

        var plugin = AppState.Instance.Plugins.FirstOrDefault(p => p.Id == dev.OwnerPluginId);
        if (plugin?.UiAssemblyPath is null || plugin.UiPageTypeName is null)
        {
            PluginHost.Content = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = "该插件未提供界面" };
            return;
        }

        try
        {
            var bridge = new PluginUiBridge(AppState.Instance.Client, plugin.Id, dev.DeviceKey);
            PluginHost.Content = PluginUiLoader.LoadPage(plugin.UiAssemblyPath, plugin.UiPageTypeName, bridge);
        }
        catch (Exception ex)
        {
            // 动态加载/资源解析失败会在此显现。
            PluginHost.Content = new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error,
                Title = "插件界面加载失败",
                Message = ex.ToString(),
            };
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_dev?.OwnerPluginId is not { } pluginId) return;
        var custom = _dev.DisplayName != _dev.DefaultName ? _dev.DisplayName : null;
        if (!await RenameDeviceDialog.ShowAsync(XamlRoot, pluginId, _dev.DeviceKey, custom, _dev.DefaultName ?? _dev.DisplayName)) return;
        // 刷新后左侧菜单会重建，页头直接取新的设备信息
        if (AppState.Instance.ManagedDevices.FirstOrDefault(d => d.DeviceKey == _dev.DeviceKey) is { } fresh)
        {
            _dev = fresh;
            TitleText.Text = fresh.DisplayName;
        }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (_dev is not null) await DeviceDetailsDialog.ShowAsync(XamlRoot, _dev.DeviceKey);
    }
}
