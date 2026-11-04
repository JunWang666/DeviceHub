using System.Collections.Concurrent;
using System.Text.Json;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceHub.Agent.Core;

/// <summary>
/// 授权中心：插件访问设备必须先获得用户允许。授权记录由核心持久化，插件无法改写。
/// </summary>
public sealed class PermissionService
{
    private readonly ILogger<PermissionService> _log;
    private readonly string _file;
    private readonly ConcurrentDictionary<string, AccessScope> _grants = new(); // key: pluginId|deviceKey
    private readonly ConcurrentDictionary<string, DateTimeOffset> _grantedAt = new(); // 同 key：接入时间
    private readonly ConcurrentDictionary<string, string> _lastName = new();   // 同 key：上次在线时的显示名（离线时用）
    private readonly ConcurrentDictionary<string, string> _customName = new(); // 同 key：用户起的名字
    private readonly ConcurrentDictionary<string, string> _glyph = new();      // 同 key：上次在线时插件给的图标
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AccessScope>> _pending = new();

    /// <summary>需要弹窗征求用户同意。</summary>
    public event Action<PermissionRequest>? RequestRaised;
    /// <summary>授权被撤销，Broker 应立即关闭句柄。</summary>
    public event Action<string, string>? GrantRevoked; // pluginId, deviceKey

    public PermissionService(ILogger<PermissionService> log, string dataDir)
    {
        _log = log;
        _file = Path.Combine(dataDir, "grants.json");
        Load();
    }

    private static string K(string pluginId, string deviceKey) => $"{pluginId}|{deviceKey}";

    public AccessScope GetGrant(string pluginId, string deviceKey) =>
        _grants.TryGetValue(K(pluginId, deviceKey), out var s) ? s : AccessScope.Denied;

    public IEnumerable<(string PluginId, string DeviceKey, AccessScope Scope, DateTimeOffset? GrantedAt)> AllGrants() =>
        _grants.Select(kv =>
        {
            var p = kv.Key.Split('|', 2);
            return (p[0], p[1], kv.Value, _grantedAt.TryGetValue(kv.Key, out var t) ? t : (DateTimeOffset?)null);
        });

    /// <summary>上次在线时的显示名、用户自定义名（都可能为空）。</summary>
    public (string? LastName, string? CustomName) GetNames(string pluginId, string deviceKey)
    {
        var k = K(pluginId, deviceKey);
        return (_lastName.GetValueOrDefault(k), _customName.GetValueOrDefault(k));
    }

    /// <summary>记住设备在线时的显示名，设备拔掉后授权列表仍能显示它。名字变了才写盘。</summary>
    public void RememberName(string pluginId, string deviceKey, string name)
    {
        var k = K(pluginId, deviceKey);
        if (string.IsNullOrWhiteSpace(name) || !_grants.ContainsKey(k)) return;
        if (_lastName.TryGetValue(k, out var old) && old == name) return;
        _lastName[k] = name;
        Save();
    }

    /// <summary>记住插件给设备的图标（已校验），离线时侧边栏照样显示。变了才写盘。</summary>
    public void RememberGlyph(string pluginId, string deviceKey, string? glyph)
    {
        var k = K(pluginId, deviceKey);
        if (!Glyphs.IsValid(glyph) || !_grants.ContainsKey(k)) return;
        if (_glyph.TryGetValue(k, out var old) && old == glyph) return;
        _glyph[k] = glyph!;
        Save();
    }

    public string? GetGlyph(string pluginId, string deviceKey) => _glyph.GetValueOrDefault(K(pluginId, deviceKey));

    /// <summary>用户自定义设备名；空白表示恢复默认名。</summary>
    public void SetCustomName(string pluginId, string deviceKey, string? name)
    {
        var k = K(pluginId, deviceKey);
        if (!_grants.ContainsKey(k)) return;
        if (string.IsNullOrWhiteSpace(name)) _customName.TryRemove(k, out _);
        else _customName[k] = name.Trim();
        Save();
        _log.LogInformation("设备改名 {Plugin} → {Device}: {Name}", pluginId, deviceKey, name ?? "(默认)");
    }

    /// <summary>申请访问。Always 直接通过；否则弹窗等待用户。</summary>
    public async Task<AccessScope> RequestAsync(PluginInfo plugin, DeviceInfo device, string reason)
    {
        var existing = GetGrant(plugin.Id, device.DeviceKey);
        if (existing == AccessScope.Always) return existing;

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<AccessScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        var req = new PermissionRequest
        {
            RequestId = requestId,
            PluginId = plugin.Id,
            PluginDisplayName = plugin.DisplayName,
            Device = device,
            Reason = reason,
        };
        _log.LogInformation("授权请求 {Plugin} → {Device}: {Reason}", plugin.Id, device.DeviceKey, reason);

        if (RequestRaised is null)
        {
            _pending.TryRemove(requestId, out _);
            return AccessScope.Denied; // 无 UI 可征询
        }
        RequestRaised.Invoke(req);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (cts.Token.Register(() => tcs.TrySetResult(AccessScope.Denied)))
        {
            var scope = await tcs.Task.ConfigureAwait(false);
            _pending.TryRemove(requestId, out _);
            if (scope is AccessScope.Always or AccessScope.Denied)
            {
                _grants[K(plugin.Id, device.DeviceKey)] = scope;
                _grantedAt[K(plugin.Id, device.DeviceKey)] = DateTimeOffset.Now;
                Save();
            }
            else if (scope == AccessScope.Once)
            {
                _grants[K(plugin.Id, device.DeviceKey)] = AccessScope.Once; // 仅本会话内存态
            }
            return scope;
        }
    }

    /// <summary>直接授予授权（用户在"新设备"页显式点击授权时调用）。</summary>
    public void Grant(string pluginId, string deviceKey, AccessScope scope)
    {
        _grants[K(pluginId, deviceKey)] = scope;
        _grantedAt[K(pluginId, deviceKey)] = DateTimeOffset.Now;
        if (scope == AccessScope.Always) Save();
        _log.LogInformation("授予 {Plugin} → {Device}: {Scope}", pluginId, deviceKey, scope);
    }

    /// <summary>
    /// 子设备继承父设备的 Always 授权：授权记录让子设备也能被改名、记住名字、显示接入时间。
    /// 父设备没有 Always 授权时返回 false（插件不能给未授权的设备发布子设备）。
    /// </summary>
    public bool InheritGrant(string pluginId, string parentKey, string childKey)
    {
        if (GetGrant(pluginId, parentKey) != AccessScope.Always) return false;
        var k = K(pluginId, childKey);
        if (_grants.TryAdd(k, AccessScope.Always))
        {
            _grantedAt[k] = DateTimeOffset.Now;
            Save();
        }
        return true;
    }

    public void Respond(string requestId, AccessScope scope)
    {
        if (_pending.TryGetValue(requestId, out var tcs)) tcs.TrySetResult(scope);
    }

    public void Revoke(string pluginId, string deviceKey)
    {
        if (_grants.TryRemove(K(pluginId, deviceKey), out _))
        {
            _grantedAt.TryRemove(K(pluginId, deviceKey), out _);
            _lastName.TryRemove(K(pluginId, deviceKey), out _);
            _customName.TryRemove(K(pluginId, deviceKey), out _);
            _glyph.TryRemove(K(pluginId, deviceKey), out _);
            Save();
            _log.LogInformation("撤销授权 {Plugin} → {Device}", pluginId, deviceKey);
            GrantRevoked?.Invoke(pluginId, deviceKey);
        }
        // 子设备的授权随父设备一起撤销。
        if (!DeviceKeys.IsChild(deviceKey))
            foreach (var k in _grants.Keys.Where(k => k.StartsWith(K(pluginId, deviceKey) + DeviceKeys.ChildSeparator, StringComparison.Ordinal)).ToList())
                Revoke(pluginId, k.Split('|', 2)[1]);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_file));
            if (map is null) return;
            foreach (var (key, v) in map)
            {
                // 新格式 {"scope":2,"grantedAt":"…"}；旧格式直接是数字 scope，接入时间取文件修改时间作近似。
                var rec = v.ValueKind == JsonValueKind.Number
                    ? new GrantRecord((AccessScope)v.GetInt32(), new DateTimeOffset(File.GetLastWriteTime(_file)))
                    : v.Deserialize<GrantRecord>(Json);
                if (rec is null || rec.Scope != AccessScope.Always) continue; // 只持久化 Always
                _grants[key] = rec.Scope;
                if (rec.GrantedAt is { } t) _grantedAt[key] = t;
                if (!string.IsNullOrWhiteSpace(rec.LastName)) _lastName[key] = rec.LastName;
                if (!string.IsNullOrWhiteSpace(rec.CustomName)) _customName[key] = rec.CustomName;
                if (Glyphs.IsValid(rec.Glyph)) _glyph[key] = rec.Glyph!;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "读取授权失败"); }
    }

    private void Save()
    {
        try
        {
            var persist = _grants.Where(kv => kv.Value == AccessScope.Always)
                                  .ToDictionary(kv => kv.Key,
                                      kv => new GrantRecord(kv.Value, _grantedAt.TryGetValue(kv.Key, out var t) ? t : null,
                                          _lastName.GetValueOrDefault(kv.Key), _customName.GetValueOrDefault(kv.Key), _glyph.GetValueOrDefault(kv.Key)));
            File.WriteAllText(_file, JsonSerializer.Serialize(persist, Json));
        }
        catch (Exception ex) { _log.LogWarning(ex, "保存授权失败"); }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record GrantRecord(AccessScope Scope, DateTimeOffset? GrantedAt, string? LastName = null, string? CustomName = null, string? Glyph = null);
}
