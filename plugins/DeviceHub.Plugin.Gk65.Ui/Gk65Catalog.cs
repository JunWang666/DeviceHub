using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceHub.Plugin.Gk65.Ui;

public sealed class LedMode { public int I { get; set; } public string En { get; set; } = ""; public string Zh { get; set; } = ""; }

/// <summary>物理键位：HID 使用码 + 在键盘图上的矩形（官方 positions.xml 的坐标，原点移到 Esc）。Id 0 为 Fn（固件处理，系统收不到）。</summary>
public sealed class KeyPos { public int Id { get; set; } public string Label { get; set; } = ""; public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; } }
public sealed class Gk65Layout { public List<KeyPos> Keys { get; set; } = new(); }

/// <summary>GK65 功能目录，从驱动配置文件生成的 gk65-data.json（嵌入资源）加载。</summary>
public sealed class Gk65Catalog
{
    public List<string> Profiles { get; set; } = new();
    public List<string> Areas { get; set; } = new();
    public List<LedMode> LedModes { get; set; } = new();

    /// <summary>物理键位布局（gk65-layout.json）。</summary>
    [JsonIgnore] public Gk65Layout Layout { get; private set; } = new();

    private static Gk65Catalog? _instance;
    public static Gk65Catalog Instance => _instance ??= Load();

    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static Gk65Catalog Load()
    {
        var catalog = Read<Gk65Catalog>("gk65-data.json");
        catalog.Layout = Read<Gk65Layout>("gk65-layout.json");
        return catalog;
    }

    private static T Read<T>(string resource) where T : new()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"缺少嵌入资源 {resource}");
        using var r = new StreamReader(s);
        return JsonSerializer.Deserialize<T>(r.ReadToEnd(), Opt) ?? new();
    }
}
