using System.IO.Pipes;
using System.Reflection;
using DeviceHub.Contracts;
using DeviceHub.Sdk;
using StreamJsonRpc;

namespace DeviceHub.PluginHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var a = ParseArgs(args);
        if (!a.TryGetValue("pipe", out var pipeName) || !a.TryGetValue("plugin", out var pluginId)
            || !a.TryGetValue("dir", out var dir) || !a.TryGetValue("asm", out var asmPath)
            || !a.TryGetValue("type", out var typeName))
        {
            Console.Error.WriteLine("用法: --pipe <name> --plugin <id> --dir <versionDir> --asm <backend.dll> --type <FullTypeName>");
            return 2;
        }

        Log($"启动，插件 {pluginId}，程序集 {asmPath}");

        IDevicePlugin plugin;
        try
        {
            var alc = new PluginLoadContext(asmPath);
            var asm = alc.LoadFromAssemblyPath(asmPath);
            // throwOnError：依赖（如 SDK 版本不匹配）加载失败时抛出真实原因，而不是返回 null
            var type = asm.GetType(typeName, throwOnError: true)!;
            plugin = (IDevicePlugin)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法实例化插件"));
        }
        catch (Exception ex) { Log("加载插件失败: " + ex); return 3; }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(10_000); }
        catch (Exception ex) { Log("连接 Agent 失败: " + ex.Message); return 4; }

        using var lifetime = new CancellationTokenSource();

        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(pipe, pipe, formatter);
        var rpc = new JsonRpc(handler);
        var agentProxy = rpc.Attach<IHostToAgentApi>();

        var ctx = new PluginContext(pluginId, dir, agentProxy);
        var hostApi = new HostApiService(plugin, ctx, lifetime.Token);
        rpc.AddLocalRpcTarget(hostApi, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });

        rpc.Disconnected += (_, e) => { Log("与 Agent 断开: " + e.Description); lifetime.Cancel(); };
        rpc.StartListening();
        Log("已连接 Agent，等待调用");

        try { await rpc.Completion; } catch { }
        lifetime.Cancel();
        Log("退出");
        return 0;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < args.Length; i += 2)
            if (args[i].StartsWith("--")) d[args[i][2..]] = args[i + 1];
        return d;
    }

    private static void Log(string m) => Console.WriteLine($"[PluginHost {DateTime.Now:HH:mm:ss}] {m}");
}
