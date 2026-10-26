using System.Text.Json;

namespace DeviceHub.Agent.Core;

/// <summary>按 pluginId 隔离的数据存储。插件不能直接碰文件系统，只能经此 API。</summary>
public sealed class StorageService
{
    private readonly string _root;
    public StorageService(string dataDir) => _root = Path.Combine(dataDir, "plugindata");

    private string FileFor(string pluginId)
    {
        var dir = Path.Combine(_root, Sanitize(pluginId));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "store.json");
    }

    public async Task<string?> GetAsync(string pluginId, string key)
    {
        var map = await LoadAsync(pluginId);
        return map.TryGetValue(key, out var v) ? v : null;
    }

    public async Task SetAsync(string pluginId, string key, string valueJson)
    {
        var map = await LoadAsync(pluginId);
        map[key] = valueJson;
        await File.WriteAllTextAsync(FileFor(pluginId), JsonSerializer.Serialize(map));
    }

    private async Task<Dictionary<string, string>> LoadAsync(string pluginId)
    {
        var f = FileFor(pluginId);
        if (!File.Exists(f)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(f)) ?? new(); }
        catch { return new(); }
    }

    private static string Sanitize(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars()));
}
