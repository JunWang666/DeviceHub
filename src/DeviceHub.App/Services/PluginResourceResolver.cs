using System.Text.Json;
using Microsoft.Windows.ApplicationModel.Resources;

namespace DeviceHub.App.Services;

/// <summary>
/// 给 XAML 框架提供的资源管理器：先查应用自己的资源表，查不到时（ResourceNotFound）
/// 再到插件仓库里找——插件 UI 的 xbf 等文件因此不必复制到 exe 目录旁，打包（MSIX，安装目录只读）时也能用。
/// 通过 <see cref="Microsoft.UI.Xaml.Application.ResourceManagerRequested"/> 接入。
/// </summary>
/// <summary>
/// 读插件 state.json。Agent 用 .NET 默认命名写 "Active"/"Disabled"，部署脚本写小写，所以按属性名不区分大小写读。
/// </summary>
internal static class PluginStateFile
{
    /// <summary>未禁用插件的 active 版本；读不到时为 null。</summary>
    public static string? ActiveVersion(string stateFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(stateFile));
        string? active = null;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name.Equals("disabled", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True) return null;
            if (p.Name.Equals("active", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) active = p.Value.GetString();
        }
        return string.IsNullOrEmpty(active) ? null : active;
    }
}

public static class PluginResourceResolver
{
    private static ResourceManager? _rm;
    /// <summary>UI 程序集名（即资源路径第一段，如 DeviceHub.Plugin.Gk65.Ui）→ 该插件 active 版本的 ui 目录。</summary>
    private static readonly Dictionary<string, string> UiDirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceHub", "logs", "app-resources.log");

    public static IResourceManager Create()
    {
        if (_rm is not null) return _rm;
        ScanStore();
        _rm = new ResourceManager();
        _rm.ResourceNotFound += OnResourceNotFound;
        return _rm;
    }

    private static void ScanStore()
    {
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceHub", "plugins");
        if (!Directory.Exists(store)) return;
        foreach (var pluginDir in Directory.GetDirectories(store))
        {
            try
            {
                var stateFile = Path.Combine(pluginDir, "state.json");
                if (!File.Exists(stateFile)) continue;
                if (PluginStateFile.ActiveVersion(stateFile) is not { } active) continue;
                var ui = Path.Combine(pluginDir, active, "ui");
                if (!Directory.Exists(ui)) continue;
                foreach (var sub in Directory.GetDirectories(ui)) UiDirs[Path.GetFileName(sub)] = ui;
            }
            catch { }
        }
        Log($"scan: {string.Join(", ", UiDirs.Select(kv => kv.Key + " -> " + kv.Value))}");
    }

    private static void OnResourceNotFound(ResourceManager sender, ResourceNotFoundEventArgs args)
    {
        var name = args.Name;
        // XAML 加载 ms-appx:///<Assembly>/<Page>.xaml 时查的是 "Files/<Assembly>/<Page>.xbf"
        var rel = name.StartsWith("Files/", StringComparison.OrdinalIgnoreCase) ? name[6..] : name;
        var slash = rel.IndexOf('/');
        string? hit = null;
        if (slash > 0 && UiDirs.TryGetValue(rel[..slash], out var ui))
        {
            var path = Path.GetFullPath(Path.Combine(ui, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (path.StartsWith(ui, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                args.SetResolvedCandidate(new ResourceCandidate(ResourceCandidateKind.FilePath, path));
                hit = path;
            }
        }
        Log($"not found: {name} -> {hit ?? "(unresolved)"}");
    }

    internal static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
