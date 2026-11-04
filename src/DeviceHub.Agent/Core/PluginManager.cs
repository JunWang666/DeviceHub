using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceHub.Agent.Core;

public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string SdkVersion { get; set; } = "1.0";
    public string BackendAssembly { get; set; } = "";
    public string BackendType { get; set; } = "";
    public string? UiAssembly { get; set; }
    public string? UiPageType { get; set; }
    /// <summary>实现 IPluginWidgetProvider 的类型；省略时用 <see cref="UiPageType"/>。</summary>
    public string? UiWidgetType { get; set; }
    public List<DeviceMatch> Devices { get; set; } = new();
    public List<WidgetManifest> Widgets { get; set; } = new();
    public List<string> Permissions { get; set; } = new();

    /// <summary>清单里的小组件声明 → 发给界面的 <see cref="WidgetInfo"/>；无效项（缺 id/标题、id 重复）丢弃。</summary>
    public IReadOnlyList<WidgetInfo> WidgetInfos() => Widgets
        .Where(w => w.Id.Length > 0 && w.Title.Length > 0)
        .GroupBy(w => w.Id).Select(g => g.First())
        .Select(w =>
        {
            var sizes = w.Sizes.Select(ParseSize).OfType<WidgetSize>().Distinct().ToList();
            if (sizes.Count == 0) sizes.Add(WidgetSize.Small);
            var def = ParseSize(w.DefaultSize) is { } d && sizes.Contains(d) ? d : sizes[0];
            return new WidgetInfo
            {
                Id = w.Id, Title = w.Title, Description = w.Description,
                Glyph = Glyphs.IsValid(w.Glyph) ? w.Glyph : null,
                Sizes = sizes, DefaultSize = def,
                Targets = w.Targets is "physical" or "child" ? w.Targets : "any",
            };
        }).ToList();

    private static WidgetSize? ParseSize(string? s) => s?.ToLowerInvariant() switch
    {
        "small" => WidgetSize.Small, "wide" => WidgetSize.Wide, "large" => WidgetSize.Large, _ => null,
    };
}

/// <summary>plugin.json 里的 widgets[] 项。sizes / defaultSize 取 small、wide、large。</summary>
public sealed class WidgetManifest
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Glyph { get; set; }
    public List<string> Sizes { get; set; } = new();
    public string? DefaultSize { get; set; }
    public string? Targets { get; set; }
}

/// <summary>
/// 插件在自己的 plugin.json 里声明适配的设备：总线 + VID/PID，以及它要用的 HID 通道。
/// 物理设备必须具备全部声明的通道才算匹配；插件 I/O 只能落到声明过的通道上（最小权限）。
/// </summary>
public sealed class DeviceMatch
{
    public string Bus { get; set; } = "";
    /// <summary>匹配成功时给设备显示的友好名称（替代 "2.4G Wireless Device" 这类产品串）。</summary>
    public string? Name { get; set; }
    /// <summary>0 = 任意。</summary>
    [JsonConverter(typeof(HexUShortConverter))] public ushort Vid { get; set; }
    /// <summary>空 = 任意。可写单个值或数组（同一型号常有多个 PID，如有线/接收器）。</summary>
    [JsonConverter(typeof(HexUShortListConverter))] public List<ushort> Pid { get; set; } = new();
    /// <summary>需要的通道，第一个为默认通道。HID 设备未声明通道时插件无法做任何 I/O。</summary>
    public List<ChannelMatch> Channels { get; set; } = new();
}

/// <summary>
/// 描述插件要用的一个通道并给它起个角色名供插件引用：HID 集合按接口号与 Usage 匹配；
/// 蓝牙设备按种类（continuity / aap）匹配。
/// </summary>
public sealed class ChannelMatch
{
    public string Role { get; set; } = "";
    /// <summary>通道种类，见 <see cref="ChannelKinds"/>；默认 hid。</summary>
    public string Kind { get; set; } = ChannelKinds.Hid;
    /// <summary>可选通道：设备没有它也算匹配（如 aap 只在装了驱动时才有），插件访问时返回 AccessDenied。</summary>
    public bool Optional { get; set; }
    /// <summary>aap 通道允许连接的 L2CAP PSM；核心只连插件声明的 PSM。</summary>
    [JsonConverter(typeof(HexUShortConverter))] public ushort Psm { get; set; } = 0x1001;
    /// <summary>-1 = 任意；否则匹配 USB 接口号（mi_XX）。</summary>
    public int Interface { get; set; } = -1;
    /// <summary>0 = 任意。</summary>
    [JsonConverter(typeof(HexUShortConverter))] public ushort UsagePage { get; set; }
    /// <summary>0 = 任意。</summary>
    [JsonConverter(typeof(HexUShortConverter))] public ushort Usage { get; set; }

    public bool Matches(HidChannelInfo c) =>
        string.Equals(Kind, c.Kind, StringComparison.OrdinalIgnoreCase) &&
        (Interface < 0 || Interface == c.Interface) &&
        (UsagePage == 0 || UsagePage == c.UsagePage) &&
        (Usage == 0 || Usage == c.Usage);
}

/// <summary>每插件的版本指针。</summary>
public sealed class PluginState
{
    public string Active { get; set; } = "";
    public string? Pending { get; set; }
    public string? Previous { get; set; }
    public bool Disabled { get; set; }
}

/// <summary>
/// 插件仓库管理：扫描、版本指针、暂存更新与回滚。
/// 目录布局：plugins/{id}/{version}/（含 manifest.json），plugins/{id}/state.json。
/// </summary>
public sealed class PluginManager
{
    private readonly ILogger<PluginManager> _log;
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };
    public string Root { get; }

    public PluginManager(ILogger<PluginManager> log, string dataDir)
    {
        _log = log;
        Root = Path.Combine(dataDir, "plugins");
        Directory.CreateDirectory(Root);
    }

    public IEnumerable<string> PluginIds() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith('.'))!
            : Enumerable.Empty<string>();

    public PluginState LoadState(string pluginId)
    {
        var f = Path.Combine(Root, pluginId, "state.json");
        if (File.Exists(f))
            try { return JsonSerializer.Deserialize<PluginState>(File.ReadAllText(f), CaseInsensitive) ?? Infer(pluginId); }
            catch { }
        return Infer(pluginId);
    }

    private PluginState Infer(string pluginId)
    {
        var versions = VersionDirs(pluginId);
        var st = new PluginState { Active = versions.LastOrDefault() ?? "" };
        if (st.Active.Length > 0) SaveState(pluginId, st);
        return st;
    }

    public void SaveState(string pluginId, PluginState st) =>
        File.WriteAllText(Path.Combine(Root, pluginId, "state.json"), JsonSerializer.Serialize(st));

    private List<string> VersionDirs(string pluginId)
    {
        var dir = Path.Combine(Root, pluginId);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetDirectories(dir).Select(Path.GetFileName)!
            .Where(n => n != null).OrderBy(n => n, StringComparer.Ordinal).ToList()!;
    }

    public string ActiveVersionDir(string pluginId)
    {
        var st = LoadState(pluginId);
        return Path.Combine(Root, pluginId, st.Active);
    }

    public PluginManifest? ReadManifest(string versionDir)
    {
        var f = Path.Combine(versionDir, "manifest.json");
        if (!File.Exists(f)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(f), CaseInsensitive);
        }
        catch (Exception ex) { _log.LogWarning(ex, "解析 manifest 失败 {Dir}", versionDir); return null; }
    }

    public PluginManifest? ActiveManifest(string pluginId) => ReadManifest(ActiveVersionDir(pluginId));

    /// <summary>设备匹配：返回可接管此设备的插件 Id（第一个）。</summary>
    public string? MatchPlugin(DeviceInfo device) => MatchPlugins(device).FirstOrDefault();

    /// <summary>设备匹配：返回所有候选插件 Id（供"建议插件"列表）。</summary>
    public IReadOnlyList<string> MatchPlugins(DeviceInfo device) =>
        PluginIds().Where(id => MatchDevice(id, device) is not null).ToList();

    /// <summary>插件 manifest 中第一条匹配此设备的声明；插件被禁用或不适配时为 null。</summary>
    public DeviceMatch? MatchDevice(string pluginId, DeviceInfo device)
    {
        var m = ActiveManifest(pluginId);
        // 子设备不按 manifest 匹配：它只属于发布它的插件，也没有可声明的通道。
        if (m is null || device.IsChild || LoadState(pluginId).Disabled) return null;
        return m.Devices.FirstOrDefault(d =>
            string.Equals(d.Bus, device.Bus.ToString(), StringComparison.OrdinalIgnoreCase) &&
            (d.Vid == 0 || d.Vid == device.Vid) &&
            (d.Pid.Count == 0 || d.Pid.Contains(device.Pid)) &&
            d.Channels.Where(c => !c.Optional).All(c => device.Channels.Any(c.Matches)));
    }

    /// <summary>
    /// 设备不在线时，按设备键里的总线与 VID/PID（"hid:1a2c:7fff:…"、"bt:004c:…"）在 manifest 里找声明名，
    /// 给没记下名字的旧授权记录兜底。
    /// </summary>
    public string? DeclaredName(string pluginId, string deviceKey)
    {
        var m = ActiveManifest(pluginId);
        if (m is null) return null;
        var parts = deviceKey.Split(':');
        var bus = parts[0] switch { "hid" => "Hid", "bt" => "Bluetooth", _ => null };
        const System.Globalization.NumberStyles hex = System.Globalization.NumberStyles.HexNumber;
        if (bus is null || parts.Length < 3 || !ushort.TryParse(parts[1], hex, null, out var vid)
            || !ushort.TryParse(parts[2], hex, null, out var pid)) return null;
        return m.Devices.FirstOrDefault(d =>
            string.Equals(d.Bus, bus, StringComparison.OrdinalIgnoreCase) &&
            (d.Vid == 0 || d.Vid == vid) && (d.Pid.Count == 0 || d.Pid.Contains(pid)))?.Name;
    }

    /// <summary>
    /// 把插件请求的通道角色（null = 默认，即声明的第一个）解析为设备上的实际通道。
    /// 未声明的角色返回 null —— 插件碰不到自己没声明的通道。
    /// </summary>
    public HidChannelInfo? ResolveChannel(string pluginId, DeviceInfo device, string? role) =>
        ResolveChannelSpec(pluginId, device, role).Channel;

    /// <summary>同 <see cref="ResolveChannel"/>，并返回插件的声明（aap 通道要用其中的 PSM）。</summary>
    public (HidChannelInfo? Channel, ChannelMatch? Spec) ResolveChannelSpec(string pluginId, DeviceInfo device, string? role)
    {
        var match = MatchDevice(pluginId, device);
        var spec = role is null
            ? match?.Channels.FirstOrDefault()
            : match?.Channels.FirstOrDefault(c => string.Equals(c.Role, role, StringComparison.OrdinalIgnoreCase));
        return spec is null ? (null, null) : (device.Channels.FirstOrDefault(spec.Matches), spec);
    }

    /// <summary>安装结果。Previous 为安装前的 active 版本（新插件为 null），可用 <see cref="Rollback"/> 回到它。</summary>
    public sealed record InstallResult(string Id, string Version, string? Previous);

    /// <summary>
    /// 安装插件包（.dhplugin = zip：manifest.json + backend/ + ui/），立即设为 active。
    /// 原来的 active 版本记为 previous 供回滚，再旧的版本目录清掉。调用方负责先停、后启插件进程。
    /// 重装同一版本会覆盖该版本目录（App 若已加载它的界面，文件被占用时会失败，需先重启界面）。
    /// </summary>
    public InstallResult InstallPackage(string packagePath)
    {
        var tmp = Path.Combine(Root, ".incoming", Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(packagePath, tmp);
            var manifest = ReadManifest(tmp) ?? throw new InvalidOperationException("包内缺少 manifest.json");
            if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Version)
                || manifest.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || manifest.Version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("manifest 的 id/version 无效");

            var target = Path.Combine(Root, manifest.Id, manifest.Version);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(tmp, target);

            var st = File.Exists(Path.Combine(Root, manifest.Id, "state.json")) ? LoadState(manifest.Id) : new PluginState();
            var old = !string.IsNullOrEmpty(st.Active) && Directory.Exists(Path.Combine(Root, manifest.Id, st.Active)) ? st.Active : null;
            if (old is not null && old != manifest.Version) st.Previous = old;
            st.Active = manifest.Version;
            st.Pending = null;
            st.Disabled = false;
            SaveState(manifest.Id, st);
            CleanupVersions(manifest.Id);
            _log.LogInformation("已安装插件包 {Plugin} v{Ver}（原 {Old}）", manifest.Id, manifest.Version, old ?? "无");
            return new InstallResult(manifest.Id, manifest.Version, old == manifest.Version ? st.Previous : old);
        }
        finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>回到上一个版本（active 与 previous 互换）。没有可回滚的版本时返回 null。调用方负责重启插件进程。</summary>
    public string? Rollback(string pluginId)
    {
        var st = LoadState(pluginId);
        if (string.IsNullOrEmpty(st.Previous) || !Directory.Exists(Path.Combine(Root, pluginId, st.Previous))) return null;
        (st.Active, st.Previous) = (st.Previous, st.Active);
        st.Pending = null;
        SaveState(pluginId, st);
        _log.LogInformation("已回滚 {Plugin} → v{Ver}", pluginId, st.Active);
        return st.Active;
    }

    /// <summary>只留 active / previous / pending 三个版本目录，其余删掉（被占用的跳过，下次再删）。</summary>
    private void CleanupVersions(string pluginId)
    {
        var st = LoadState(pluginId);
        foreach (var v in VersionDirs(pluginId).Where(v => v != st.Active && v != st.Previous && v != st.Pending))
            try { Directory.Delete(Path.Combine(Root, pluginId, v), true); }
            catch (Exception ex) { _log.LogDebug(ex, "旧版本目录暂时删不掉 {Plugin} v{Ver}", pluginId, v); }
    }

    /// <summary>
    /// 安装随程序附带的插件（{exe 目录}\plugins\*.dhplugin）：未安装或附带版本更新时才安装。
    /// 在 Agent 启动、插件启动之前调用。
    /// </summary>
    public void InstallBundled(string bundleDir)
    {
        if (!Directory.Exists(bundleDir)) return;
        foreach (var pkg in Directory.GetFiles(bundleDir, "*.dhplugin"))
        {
            try
            {
                using var zip = ZipFile.OpenRead(pkg);
                var entry = zip.GetEntry("manifest.json") ?? throw new InvalidOperationException("缺少 manifest.json");
                using var s = entry.Open();
                var m = JsonSerializer.Deserialize<PluginManifest>(s, CaseInsensitive) ?? throw new InvalidOperationException("manifest 无效");
                var st = File.Exists(Path.Combine(Root, m.Id, "state.json")) ? LoadState(m.Id) : null;
                if (st is not null && Directory.Exists(Path.Combine(Root, m.Id, st.Active))
                    && CompareVersion(st.Active, m.Version) >= 0) continue;
                if (st?.Previous == m.Version) continue; // 用户刚从这个版本回滚，别每次启动又装回去
                InstallPackage(pkg); // Agent 正在启动、插件还没起来，直接切到附带的新版本
            }
            catch (Exception ex) { _log.LogWarning(ex, "安装附带插件失败 {Pkg}", pkg); }
        }
    }

    private static int CompareVersion(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);

    /// <summary>暂存一个新版本（目录或 .dhplugin zip），等待重启生效。</summary>
    public bool StageUpdate(string pluginId, string packagePath)
    {
        try
        {
            var tmp = Path.Combine(Root, pluginId, ".staging");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);

            if (Directory.Exists(packagePath)) CopyDir(packagePath, tmp);
            else ZipFile.ExtractToDirectory(packagePath, tmp);

            var manifest = ReadManifest(tmp) ?? throw new InvalidOperationException("包内缺少 manifest.json");
            if (manifest.Id != pluginId) throw new InvalidOperationException($"包 Id 不匹配：{manifest.Id}");

            var target = Path.Combine(Root, pluginId, manifest.Version);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(tmp, target);

            var st = LoadState(pluginId);
            st.Pending = manifest.Version;
            SaveState(pluginId, st);
            _log.LogInformation("已暂存 {Plugin} v{Ver}，待重启生效", pluginId, manifest.Version);
            return true;
        }
        catch (Exception ex) { _log.LogError(ex, "暂存更新失败 {Plugin}", pluginId); return false; }
    }

    /// <summary>启动时调用：把 pending 提升为 active（旧版留作回滚点）。</summary>
    public bool ApplyPendingIfAny(string pluginId)
    {
        var st = LoadState(pluginId);
        if (string.IsNullOrEmpty(st.Pending)) return false;
        st.Previous = st.Active;
        st.Active = st.Pending;
        st.Pending = null;
        SaveState(pluginId, st);
        _log.LogInformation("已应用更新 {Plugin} → v{Ver}", pluginId, st.Active);
        return true;
    }

    /// <summary>回滚到上一个版本并禁用（安全模式下使用）。</summary>
    public void Rollback(string pluginId, bool disable)
    {
        var st = LoadState(pluginId);
        if (!string.IsNullOrEmpty(st.Previous)) { st.Active = st.Previous!; st.Previous = null; }
        st.Disabled = disable;
        SaveState(pluginId, st);
        _log.LogWarning("已回滚 {Plugin} → v{Ver} (disabled={D})", pluginId, st.Active, disable);
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}

/// <summary>支持 "0x1a2c" 十六进制或十进制的 ushort 转换。</summary>
public sealed class HexUShortConverter : JsonConverter<ushort>
{
    public override ushort Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.Number) return r.GetUInt16();
        var s = r.GetString() ?? "0";
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt16(s[2..], 16) : ushort.Parse(s);
    }
    public override void Write(Utf8JsonWriter w, ushort v, JsonSerializerOptions o) => w.WriteNumberValue(v);
}

/// <summary>接受单个值或数组，元素同 <see cref="HexUShortConverter"/>。</summary>
public sealed class HexUShortListConverter : JsonConverter<List<ushort>>
{
    private static readonly HexUShortConverter One = new();

    public override List<ushort> Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray) return new() { One.Read(ref r, typeof(ushort), o) };
        var list = new List<ushort>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) list.Add(One.Read(ref r, typeof(ushort), o));
        return list;
    }

    public override void Write(Utf8JsonWriter w, List<ushort> v, JsonSerializerOptions o)
    {
        w.WriteStartArray();
        foreach (var x in v) w.WriteNumberValue(x);
        w.WriteEndArray();
    }
}
