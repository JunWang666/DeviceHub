using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace DeviceHub.Plugin.Gk65;

public enum StateSource { Saved, OfficialDriver, Default }

/// <summary>灯效名称（来自驱动配置提取的 gk65-data.json，嵌入资源）。</summary>
internal static class Gk65Names
{
    private static readonly Lazy<Dictionary<int, string>> Modes = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("gk65-data.json");
        if (s is null) return new();
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.GetProperty("ledModes").EnumerateArray()
            .ToDictionary(m => m.GetProperty("i").GetInt32(), m => m.GetProperty("zh").GetString() ?? "");
    });

    public static string LedMode(int mode) => Modes.Value.TryGetValue(mode, out var n) ? n : $"模式 {mode}";
}

/// <summary>
/// 首次接管时从官方驱动导入灯效设置。
/// 键盘不支持读取当前设置，官方软件也是靠本地 ledeffect.xml 记住状态：
/// &lt;effect sel="下拉框序号"&gt; + 每个灯效一条 &lt;item speed brightness deriction/&gt;。
/// 下拉框序号 → 模式码：官方界面注释掉了模式 1，所以序号 0 为模式 0，其余为序号 + 1。
/// </summary>
internal static class OfficialDriverImport
{
    private static readonly string[] Candidates =
    {
        @"C:\Program Files (x86)\700M\ledeffect.xml",
        @"C:\Program Files\700M\ledeffect.xml",
    };

    public static Gk65DeviceState? TryLoad()
    {
        try
        {
            var file = Candidates.FirstOrDefault(File.Exists);
            if (file is null) return null;
            var root = XDocument.Load(file).Root;
            if (root is null) return null;
            int sel = (int?)root.Attribute("sel") ?? 0;
            var item = root.Elements("item").ElementAtOrDefault(sel);
            if (item is null) return null;

            var st = new Gk65DeviceState { Source = StateSource.OfficialDriver };
            var p = st.Current.Panel;
            p.Mode = sel == 0 ? 0 : sel + 1;
            p.Brightness = Math.Clamp((int?)item.Attribute("brightness") ?? 5, 0, 5);
            p.Speed = Math.Clamp((int?)item.Attribute("speed") ?? 0, 0, 4);
            p.Direction = (int?)item.Attribute("deriction") ?? 0; // 官方拼写
            return st;
        }
        catch { return null; }
    }
}
