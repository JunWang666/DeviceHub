using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeviceHub.App;

/// <summary>
/// 自定义入口（DISABLE_XAML_GENERATED_MAIN）：在 XAML 启动前确保后台 Agent 在运行（安装版里由 App 负责拉起）。
/// 插件 UI 资源不复制到安装目录，由 <see cref="Services.PluginResourceResolver"/> 从插件仓库回退加载，
/// 因此 Inno（按用户安装）与 MSIX（安装目录只读）用同一套机制。
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        EnsureAgentRunning();

        XamlCheckProcessRequirements();
        Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            new App();
        });
    }

    private const string PipeName = "devicehub-agent";

    /// <summary>
    /// Agent 的管道不存在时，从 exe 同目录启动它（无窗口），最多等 10 秒管道就绪。
    /// Agent 先安装附带插件再开管道，所以等到管道时插件仓库已就绪。开发环境没有同目录 Agent 时跳过。
    /// </summary>
    private static void EnsureAgentRunning()
    {
        if (PipeExists()) return;
        var agent = Path.Combine(AppContext.BaseDirectory, "DeviceHub.Agent.exe");
        if (!File.Exists(agent)) return;
        try
        {
            Process.Start(new ProcessStartInfo(agent)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch { return; }
        var sw = Stopwatch.StartNew();
        while (!PipeExists() && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
    }

    /// <summary>只枚举管道名，不建立连接（避免占用 Agent 的 UI 连接槽）。</summary>
    private static bool PipeExists()
    {
        try { return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => p.EndsWith(@"\" + PipeName, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();
}
