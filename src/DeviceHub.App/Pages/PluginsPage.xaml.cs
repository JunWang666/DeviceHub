using System.Diagnostics;
using DeviceHub.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeviceHub.App.Pages;

public sealed partial class PluginsPage : Page
{
    public PluginsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        await AppState.Instance.RefreshAsync();
        PluginList.ItemsSource = AppState.Instance.Plugins.Select(p => new PluginRow { Plugin = p }).ToList();
    }

    private async void Refresh_Click(object s, RoutedEventArgs e) => await ReloadAsync();

    private async void Start_Click(object s, RoutedEventArgs e)
    { if (Id(s) is { } id && AppState.Instance.Client.Agent is { } a) { await a.StartPluginAsync(id); await ReloadAsync(); } }

    private async void Stop_Click(object s, RoutedEventArgs e)
    { if (Id(s) is { } id && AppState.Instance.Client.Agent is { } a) { await a.StopPluginAsync(id); await ReloadAsync(); } }

    /// <summary>选择 .dhplugin 安装（新插件或已有插件的新版本），界面重启后插件 UI 生效。</summary>
    private async void Install_Click(object s, RoutedEventArgs e)
    {
        if (AppState.Instance.Client.Agent is not { } a || App.MainWindow is null) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".dhplugin");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var id = await a.InstallPluginPackageAsync(file.Path);
            await ReloadAsync();
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "插件已安装",
                Content = $"{id} 已安装。重启界面后，插件的界面才会生效。",
                PrimaryButtonText = "立即重启界面",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary) await RestartUiAsync();
        }
        catch (Exception ex) { await Msg("安装失败", ex.Message); }
    }

    private async void Restart_Click(object s, RoutedEventArgs e) => await RestartUiAsync();

    /// <summary>Agent 先应用待定更新并重启插件后端；新进程启动时资源回退会扫描到新的 active 版本。</summary>
    private static async Task RestartUiAsync()
    {
        if (AppState.Instance.Client.Agent is { } a) { try { await a.RequestUiRestartAsync(); } catch { } }
        // AppInstance.Restart 同时支持打包（保持包身份）与非打包；成功时不返回。
        Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
        var exe = Environment.ProcessPath;
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        Application.Current.Exit();
    }

    private static string? Id(object sender) => (sender as Button)?.Tag as string;

    private async Task Msg(string title, string body)
    {
        var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = body, CloseButtonText = "确定" };
        await dlg.ShowAsync();
    }
}
