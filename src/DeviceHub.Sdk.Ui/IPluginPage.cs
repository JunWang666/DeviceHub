using DeviceHub.Contracts;
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
/// 主页小组件提供者（可选）。在 plugin.json 的 widgets[] 里声明小组件（id、标题、尺寸、适用设备），
/// 再让 uiWidgetType（省略时为 uiPageType 的入口类型）实现本接口。
/// <para>
/// 核心负责卡片外框：圆角背景、设备名与图标、编辑/移除/调整大小，点击标题进入设备页。
/// 插件只返回卡片内容，内容区域大小随 <paramref name="size"/> 变化（小 ≈ 144×96，宽 ≈ 328×96，大 ≈ 328×256 有效像素）；
/// 用户改尺寸时核心会重新调用一次。bridge 绑定到用户为该小组件选择的设备，和设备页用的是同一套状态与方法。
/// 设备未连接时也会创建小组件，请显示离线状态而不是"未授权"。
/// </para>
/// </summary>
public interface IPluginWidgetProvider
{
    FrameworkElement CreateWidget(string widgetId, WidgetSize size, IPluginUiBridge bridge);
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
