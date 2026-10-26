using System.Reflection;
using System.Runtime.Loader;

namespace DeviceHub.PluginHost;

/// <summary>
/// 隔离插件后端的私有依赖；共享契约（Sdk/Contracts/StreamJsonRpc）走默认上下文，
/// 保证跨边界的类型身份一致。
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "DeviceHub.Sdk", "DeviceHub.Contracts", "StreamJsonRpc", "MessagePack",
        "System.Text.Json", "Microsoft.Extensions.Logging.Abstractions",
    };

    public PluginLoadContext(string pluginAssemblyPath) : base(isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name != null && Shared.Contains(name.Name)) return null; // 交给默认上下文
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
