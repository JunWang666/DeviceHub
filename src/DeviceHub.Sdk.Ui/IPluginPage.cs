using Microsoft.UI.Xaml;

namespace DeviceHub.Sdk.Ui;

/// <summary>
/// 插件 UI 入口。实现类所在程序集由 App 进程在运行时动态加载（方案 B：UI 进程内）。
/// </summary>
public interface IPluginPage
{
    /// <summary>构建并返回页面内容，插入到 App 的宿主容器中。</summary>
    FrameworkElement CreatePage(IPluginUiBridge bridge);
}

/// <summary>
/// 插件 UI 与后端之间的桥。由 App 实现：转发到 Agent（App → Agent → PluginHost → 插件后端）。
/// </summary>
public interface IPluginUiBridge
{
    string PluginId { get; }
    string DeviceKey { get; }

    /// <summary>读取后端当前状态 JSON（首次绑定）。</summary>
    Task<string> GetStateAsync();

    /// <summary>把 UI 操作发给后端，返回最新状态 JSON。</summary>
    Task<string> InvokeAsync(string method, string argsJson);

    /// <summary>后端主动推送的状态变更（deviceKey 一致时触发）。</summary>
    event Action<string> StatePushed;
}
