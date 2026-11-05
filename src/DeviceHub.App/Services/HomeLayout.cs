using System.Text.Json;
using DeviceHub.Contracts;

namespace DeviceHub.App.Services;

/// <summary>主页上的一张小组件卡片。PluginId 为 null 表示核心内置小组件（见 <see cref="BuiltinWidgets"/>）。</summary>
public sealed class WidgetPlacement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? PluginId { get; set; }
    public string WidgetId { get; set; } = "";
    public string? DeviceKey { get; set; }
    public WidgetSize Size { get; set; } = WidgetSize.Small;
}

/// <summary>核心自带的小组件（不依赖插件）。</summary>
public static class BuiltinWidgets
{
    public const string Devices = "core.devices";

    public static readonly WidgetInfo DevicesInfo = new()
    {
        Id = Devices, Title = "设备总览", Description = "所有正在管理的设备及其连接状态，点击进入设备页",
        Glyph = "", Sizes = [WidgetSize.Wide, WidgetSize.Large], DefaultSize = WidgetSize.Wide,
    };

    public static WidgetInfo? Find(string id) => id == Devices ? DevicesInfo : null;
}

/// <summary>
/// 主页布局：卡片顺序与尺寸，存在 %LOCALAPPDATA%\DeviceHub\app\home.json（界面自己的数据，不经 Agent）。
/// 还没有布局文件时返回 null，由主页生成默认布局。
/// </summary>
public static class HomeLayoutStore
{
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceHub", "app", "home.json");
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static List<WidgetPlacement>? Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            return JsonSerializer.Deserialize<List<WidgetPlacement>>(System.IO.File.ReadAllText(File), J)?
                .Where(w => w.WidgetId.Length > 0).ToList();
        }
        catch { return null; }
    }

    public static void Save(IEnumerable<WidgetPlacement> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(items.ToList(), J));
        }
        catch { /* 写不了布局只影响下次启动，不打断界面 */ }
    }
}

/// <summary>"添加小组件"对话框里的一项：哪台设备的哪个小组件。</summary>
public sealed class WidgetOption
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Glyph { get; init; }
    public string? PluginId { get; init; }
    public required string WidgetId { get; init; }
    public string? DeviceKey { get; init; }
    public WidgetSize Size { get; init; }
}
