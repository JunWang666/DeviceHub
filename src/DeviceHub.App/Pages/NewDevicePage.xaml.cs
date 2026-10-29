using DeviceHub.App.Services;
using DeviceHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeviceHub.App.Pages;

public sealed partial class NewDevicePage : Page
{
    public NewDevicePage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        await AppState.Instance.RefreshAsync();
        var rows = AppState.Instance.NewDevices.Select(d => new NewDeviceRow { Device = d }).ToList();
        NewList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (AppState.Instance.Client.Agent is { } a)
        {
            var grants = (await a.ListGrantsAsync()).Where(g => g.Scope == AccessScope.Always)
                .Select(g => new GrantRow { Grant = g }).ToList();
            GrantList.ItemsSource = grants;
            NoGrantHint.Visibility = grants.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void Refresh_Click(object s, RoutedEventArgs e) => await ReloadAsync();

    /// <summary>点「添加」：弹出该设备的建议插件，用户选一个并授权。</summary>
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DeviceInfo dev || AppState.Instance.Client.Agent is not { } agent) return;

        var candidates = await agent.ListCandidatePluginsAsync(dev.DeviceKey);
        if (candidates.Count == 0)
        {
            await Msg("没有可用插件", "未找到能管理此设备的插件。"); return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = candidates.ToList(),
            ItemTemplate = (DataTemplate)Resources["CandidateTemplate"],
            MaxHeight = 280,
        };
        list.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"添加 {dev.DisplayName}",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "选择用来管理此设备的插件。授权后，该插件可以通过 DeviceHub 访问这台设备。", TextWrapping = TextWrapping.Wrap },
                    list,
                }
            },
            PrimaryButtonText = "授权并添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (list.SelectedItem is not PluginInfo chosen) return;

        await AppState.Instance.AuthorizeAndUseAsync(chosen.Id, dev.DeviceKey);
        await ReloadAsync();

        // 跳转到左侧新出现的被管理设备
        if (App.MainWindow is MainWindow mw) mw.NavigateToManagedDevice(dev.DeviceKey);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is GrantInfo g
            && await RenameDeviceDialog.ShowAsync(XamlRoot, g.PluginId, g.DeviceKey, g.CustomName, g.DefaultName))
            await ReloadAsync();
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is GrantInfo g) await DeviceDetailsDialog.ShowAsync(XamlRoot, g.DeviceKey, g);
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not GrantInfo g || AppState.Instance.Client.Agent is not { } a) return;
        var name = string.IsNullOrEmpty(g.DeviceDisplayName) ? g.DeviceKey : g.DeviceDisplayName;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤销授权？",
            Content = $"「{g.PluginDisplayName}」将无法再访问 {name}，设备会回到新设备列表。",
            PrimaryButtonText = "撤销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        await a.RevokeGrantAsync(g.PluginId, g.DeviceKey);
        await ReloadAsync();
    }

    private async Task Msg(string title, string body)
    {
        var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = body, CloseButtonText = "确定" };
        await dlg.ShowAsync();
    }
}
