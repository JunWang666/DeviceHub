using Microsoft.UI.Xaml;

namespace DeviceHub.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        // 插件 UI 资源回退：XAML 在应用资源表里找不到插件的 xbf 时，到插件仓库里找（见 PluginResourceResolver）。
        ResourceManagerRequested += (_, e) => e.CustomResourceManager = Services.PluginResourceResolver.Create();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
