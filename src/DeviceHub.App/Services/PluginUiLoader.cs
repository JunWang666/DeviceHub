using System.Reflection;
using DeviceHub.Contracts;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;

namespace DeviceHub.App.Services;

/// <summary>
/// 运行时动态加载插件 UI 程序集，取得 IPluginPage / IPluginWidgetProvider 并构建页面或小组件。
/// 方案 B 的关键风险点：进程内加载外部 XAML（x:Bind + PRI 资源）。
/// </summary>
public static class PluginUiLoader
{
    private static readonly Dictionary<string, Assembly> _loaded = new();
    private static readonly Dictionary<(string, string), object> _entries = new();

    public static FrameworkElement LoadPage(string uiAssemblyPath, string pageTypeName, IPluginUiBridge bridge)
    {
        var entry = Create(uiAssemblyPath, pageTypeName) as IPluginPage
                    ?? throw new InvalidOperationException($"{pageTypeName} 没有实现 IPluginPage");
        return entry.CreatePage(bridge);
    }

    /// <summary>创建插件小组件的内容；入口类型复用（无状态的提供者，避免每张卡片都反射一次）。</summary>
    public static FrameworkElement LoadWidget(string uiAssemblyPath, string typeName, string widgetId, WidgetSize size, IPluginUiBridge bridge)
    {
        if (!_entries.TryGetValue((uiAssemblyPath, typeName), out var entry))
            _entries[(uiAssemblyPath, typeName)] = entry = Create(uiAssemblyPath, typeName);
        var provider = entry as IPluginWidgetProvider
                       ?? throw new InvalidOperationException($"{typeName} 没有实现 IPluginWidgetProvider");
        return provider.CreateWidget(widgetId, size, bridge);
    }

    private static object Create(string uiAssemblyPath, string typeName)
    {
        if (!_loaded.TryGetValue(uiAssemblyPath, out var asm))
        {
            asm = Assembly.LoadFrom(uiAssemblyPath); // 重启式更新：加载进默认上下文即可
            _loaded[uiAssemblyPath] = asm;
        }
        var type = asm.GetType(typeName) ?? throw new InvalidOperationException($"找不到 UI 入口类型 {typeName}");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法实例化 UI 入口");
    }
}
