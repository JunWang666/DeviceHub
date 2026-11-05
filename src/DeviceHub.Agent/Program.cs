using System.IO.Pipes;
using DeviceHub.Agent.Core;
using DeviceHub.Agent.Rpc;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceHub.Agent;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // 调试：打印一次 USB 拓扑扫描结果（JSON）后退出，不启动 Agent
        if (args.Contains("--usb-dump"))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(Native.NativeUsb.Scan(), new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }));
            return;
        }

        // 调试：按键映射引擎自检（临时配置 + 注入 F13–F24），不启动 Agent、可与正在运行的 Agent 并存
        if (args.Contains("--remap-selftest"))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            using var tlf = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
            Environment.Exit(await RemapSelfTest.RunAsync(tlf));
        }

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceHub");
        Directory.CreateDirectory(dataDir);

        using var lf = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs")))
            .SetMinimumLevel(LogLevel.Information));
        var log = lf.CreateLogger("Agent");
        log.LogInformation("DeviceHub Agent 启动，数据目录 {Dir}", dataDir);

        // 单实例
        using var mutex = new Mutex(true, "DeviceHub.Agent.Singleton", out var isNew);
        if (!isNew) { log.LogError("Agent 已在运行，退出"); return; }

        var perms = new PermissionService(lf.CreateLogger<PermissionService>(), dataDir);
        var plugins = new PluginManager(lf.CreateLogger<PluginManager>(), dataDir);
        var bluetooth = new BluetoothService(lf.CreateLogger<BluetoothService>());
        var broker = new DeviceBroker(lf.CreateLogger<DeviceBroker>(), perms, plugins, bluetooth);
        var storage = new StorageService(dataDir);
        var appHub = new AppHub(lf.CreateLogger<AppHub>());
        using var remap = new RemapService(lf.CreateLogger("Remap"), dataDir, appHub);

        perms.RequestRaised += appHub.PermissionRequested;

        var hostExe = ResolveHostExe(log);
        log.LogInformation("PluginHost 可执行文件：{Exe}", hostExe ?? "(未找到)");

        var supervisor = new PluginSupervisor(lf.CreateLogger<PluginSupervisor>(), lf, plugins, broker, perms, storage, appHub, remap, hostExe ?? "DeviceHub.PluginHost.exe");
        var agentApi = new AgentApiService(broker, supervisor, perms, plugins, remap, lf.CreateLogger("AgentApi"));

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

        // —— 随程序附带的插件（安装包里的 plugins\*.dhplugin）：首次运行或附带版本更新时安装 ——
        plugins.InstallBundled(Path.Combine(AppContext.BaseDirectory, "plugins"));

        // —— 重启式更新生效点：把所有插件的 pending 版本提升为 active ——
        foreach (var id in plugins.PluginIds())
            if (plugins.ApplyPendingIfAny(id)) log.LogInformation("已应用 {Plugin} 的待定更新", id);

        // —— 蓝牙设备：等首轮配对设备枚举完，插件启动时才能接管已授权的 AirPods；
        //    之后配对/连接变化（合并 0.5s 内的连续事件）通知 UI，并让插件接管新出现的已授权设备 ——
        bluetooth.Start();
        await bluetooth.WaitReadyAsync(TimeSpan.FromSeconds(3));
        using var btDebounce = new Timer(_ =>
        {
            appHub.DevicesChanged(broker.Enumerate());
            _ = supervisor.SyncDevicesAsync();
        });
        bluetooth.Changed += () => btDebounce.Change(500, Timeout.Infinite);

        // —— USB 插拔：通知 UI 重新扫描拓扑（USB 页打开时才会真的去扫） ——
        using var usbWatcher = new Native.UsbWatcher();
        usbWatcher.Changed += appHub.UsbChanged;

        // 启动已安装且未禁用的插件
        foreach (var id in plugins.PluginIds())
            _ = supervisor.StartAsync(id);

        // —— App 连接的管道服务器 ——
        var pipeTask = AcceptAppConnectionsAsync(appHub, agentApi, remap, lf, log, shutdown.Token);

        log.LogInformation("Agent 就绪。Ctrl+C 退出。");
        try { await Task.Delay(Timeout.Infinite, shutdown.Token); } catch (OperationCanceledException) { }

        log.LogInformation("正在停止…");
        supervisor.Dispose();
        broker.Dispose();
        bluetooth.Dispose();
    }

    private static async Task AcceptAppConnectionsAsync(AppHub appHub, AgentApiService agentApi, RemapService remap,
        ILoggerFactory lf, ILogger log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream("devicehub-agent", PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(ct);
                log.LogInformation("UI 已连接");
                var (rpc, callback) = RpcFactory.CreateWithProxy<IAppCallback>(server, agentApi);
                appHub.SetCallback(callback);
                rpc.Disconnected += (_, _) => { appHub.ClearCallback(callback); remap.StopCapture(); log.LogInformation("UI 已断开"); try { server.Dispose(); } catch { } };
                rpc.StartListening();
            }
            catch (OperationCanceledException) { server.Dispose(); break; }
            catch (Exception ex) { log.LogWarning(ex, "接受 UI 连接失败"); server.Dispose(); await Task.Delay(500, ct); }
        }
    }

    /// <summary>定位 PluginHost.exe：环境变量 → 同目录 → 向上找 sln 后在构建输出里取最新。</summary>
    private static string? ResolveHostExe(ILogger log)
    {
        var env = Environment.GetEnvironmentVariable("DEVICEHUB_HOST_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var sibling = Path.Combine(AppContext.BaseDirectory, "DeviceHub.PluginHost.exe");
        if (File.Exists(sibling)) return sibling;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DeviceHub.slnx"))
                           && !File.Exists(Path.Combine(dir.FullName, "DeviceHub.sln"))) dir = dir.Parent;
        if (dir == null) return null;

        var candidates = Directory.GetFiles(Path.Combine(dir.FullName, "src", "DeviceHub.PluginHost"),
            "DeviceHub.PluginHost.exe", SearchOption.AllDirectories);
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
}
