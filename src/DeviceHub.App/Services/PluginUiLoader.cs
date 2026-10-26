using System.Reflection;
using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;

namespace DeviceHub.App.Services;

/// <summary>
/// 运行时动态加载插件 UI 程序集，取得 IPluginPage 并构建页面。
/// 方案 B 的关键风险点：进程内加载外部 XAML（x:Bind + PRI 资源）。
/// </summary>
public static class PluginUiLoader
{
    private static readonly Dictionary<string, Assembly> _loaded = new();

    public static FrameworkElement LoadPage(string uiAssemblyPath, string pageTypeName, IPluginUiBridge bridge)
    {
        if (!_loaded.TryGetValue(uiAssemblyPath, out var asm))
        {
            asm = Assembly.LoadFrom(uiAssemblyPath); // 重启式更新：加载进默认上下文即可
            _loaded[uiAssemblyPath] = asm;
        }

        var type = asm.GetType(pageTypeName)
                   ?? throw new InvalidOperationException($"找不到 UI 入口类型 {pageTypeName}");
        var entry = (IPluginPage)(Activator.CreateInstance(type)
                   ?? throw new InvalidOperationException("无法实例化 UI 入口"));
        return entry.CreatePage(bridge);
    }
}
