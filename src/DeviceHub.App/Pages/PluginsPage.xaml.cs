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
            var r = await a.InstallPluginPackageAsync(file.Path);
            await ReloadAsync();
            var name = AppState.Instance.Plugins.FirstOrDefault(p => p.Id == r.Id)?.DisplayName ?? r.Id;
            var what = r.Previous is null ? $"已安装 {name} v{r.Version}。"
                : r.Previous == r.Version ? $"已重新安装 {name} v{r.Version}。"
                : $"{name} 已从 v{r.Previous} 切换到 v{r.Version}，旧版本保留，可在本页回滚。";
            await AskRestartAsync("插件已安装", what);
        }
        catch (Exception ex) { await Msg("安装失败", ex.GetBaseException().Message + "\n\n如果重装的是同一个版本而它的界面已打开过，请先重启界面再安装。"); }
    }

    private async void Rollback_Click(object s, RoutedEventArgs e)
    {
        if (Id(s) is not { } id || AppState.Instance.Client.Agent is not { } a) return;
        var p = AppState.Instance.Plugins.FirstOrDefault(x => x.Id == id);
        try
        {
            var v = await a.RollbackPluginAsync(id);
            await ReloadAsync();
            if (v is null) { await Msg("无法回滚", "没有可回滚的版本。"); return; }
            await AskRestartAsync("已回滚", $"{p?.DisplayName ?? id} 已从 v{p?.Version} 回到 v{v}。");
        }
        catch (Exception ex) { await Msg("回滚失败", ex.GetBaseException().Message); }
    }

    /// <summary>后台已经切换；插件界面的程序集已加载进本进程，重启界面才会换成新版。</summary>
    private async Task AskRestartAsync(string title, string what)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = what + "\n\n插件后台已经换成这个版本；重启界面后，插件页面也会换成这个版本。",
            PrimaryButtonText = "立即重启界面",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) await RestartUiAsync();
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
