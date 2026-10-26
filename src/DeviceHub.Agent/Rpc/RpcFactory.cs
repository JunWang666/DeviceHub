using System.IO.Pipes;
using StreamJsonRpc;

namespace DeviceHub.Agent.Rpc;

/// <summary>统一创建基于命名管道的 JSON-RPC 连接。</summary>
public static class RpcFactory
{
    public static JsonRpc Create(PipeStream pipe, object localTarget)
    {
        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(pipe, pipe, formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(localTarget, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
        return rpc;
    }

    public static (JsonRpc Rpc, T Proxy) CreateWithProxy<T>(PipeStream pipe, object localTarget) where T : class
    {
        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(pipe, pipe, formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(localTarget, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
        var proxy = rpc.Attach<T>();
        return (rpc, proxy);
    }
}
