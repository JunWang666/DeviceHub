using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using DeviceHub.Agent.Native;
using DeviceHub.Agent.Rpc;
using DeviceHub.Contracts;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace DeviceHub.Agent.Core;

/// <summary>
/// 进程管理：按需启动/监控每个插件的 PluginHost 进程，Job Object 兜底回收，
/// 心跳检测 + 指数退避重启 + 熔断。
/// </summary>
public sealed class PluginSupervisor : IDisposable
{
    private readonly ILogger<PluginSupervisor> _log;
    private readonly ILoggerFactory _lf;
    private readonly PluginManager _plugins;
    private readonly DeviceBroker _broker;
    private readonly PermissionService _perms;
    private readonly StorageService _storage;
    private readonly AppHub _appHub;
    private readonly string _hostExe;
    private readonly ConcurrentDictionary<string, HostEntry> _hosts = new();

    private const int MaxRestarts = 5;

    public PluginSupervisor(ILogger<PluginSupervisor> log, ILoggerFactory lf, PluginManager plugins,
        DeviceBroker broker, PermissionService perms, StorageService storage, AppHub appHub, string hostExe)
    {
        _log = log; _lf = lf; _plugins = plugins; _broker = broker;
        _perms = perms; _storage = storage; _appHub = appHub; _hostExe = hostExe;
    }

    private sealed class HostEntry
    {
        public required string PluginId;
        public Process? Process;
        public JobObject? Job;
        public JsonRpc? Rpc;
        public IHostApi? Api;
        public PluginRunState State = PluginRunState.Stopped;
        public int RestartCount;
        public string? LastError;
        public bool ShuttingDown;
        public readonly HashSet<string> AttachedDevices = new();
    }

    public IReadOnlyList<PluginInfo> List()
    {
        var result = new List<PluginInfo>();
        foreach (var id in _plugins.PluginIds())
        {
            var m = _plugins.ActiveManifest(id);
            _hosts.TryGetValue(id, out var e);
            long ws = 0;
            try { if (e?.Process is { HasExited: false } p) { p.Refresh(); ws = p.WorkingSet64; } } catch { }
            result.Add(new PluginInfo
            {
                Id = id,
                DisplayName = m?.DisplayName ?? id,
                Version = m?.Version ?? "?",
                State = e?.State ?? (_plugins.LoadState(id).Disabled ? PluginRunState.Disabled : PluginRunState.Stopped),
                RestartCount = e?.RestartCount ?? 0,
                WorkingSetBytes = ws,
                LastError = e?.LastError,
                UiAssemblyPath = AbsUi(id, m),
                UiPageTypeName = m?.UiPageType,
            });
        }
        return result;
    }

    /// <summary>启动插件进程并握手。</summary>
    public async Task<bool> StartAsync(string pluginId)
    {
        if (_plugins.LoadState(pluginId).Disabled) { _log.LogWarning("插件已禁用 {Plugin}", pluginId); return false; }
        var entry = _hosts.GetOrAdd(pluginId, id => new HostEntry { PluginId = id });
        lock (entry) { if (entry.State is PluginRunState.Starting or PluginRunState.Running) return true; entry.State = PluginRunState.Starting; }
        _appHub.PluginStateChanged(Info(pluginId, entry));

        try
        {
            var versionDir = _plugins.ActiveVersionDir(pluginId);
            var manifest = _plugins.ReadManifest(versionDir) ?? throw new InvalidOperationException("缺少 manifest");
            var pipeName = "devicehub-host-" + Guid.NewGuid().ToString("N");

            // 随机管道名 + 默认 ACL（仅当前用户）作为握手凭据。生产应再加一次性 token。
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var psi = new ProcessStartInfo(_hostExe)
            {
                UseShellExecute = false,
                WorkingDirectory = versionDir,
            };
            psi.ArgumentList.Add("--pipe"); psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add("--plugin"); psi.ArgumentList.Add(pluginId);
            psi.ArgumentList.Add("--dir"); psi.ArgumentList.Add(versionDir);
            psi.ArgumentList.Add("--asm"); psi.ArgumentList.Add(Path.Combine(versionDir, manifest.BackendAssembly));
            psi.ArgumentList.Add("--type"); psi.ArgumentList.Add(manifest.BackendType);

            var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 PluginHost");
            entry.Process = proc;
            entry.ShuttingDown = false;

            // Job Object：256MB 上限，Agent 退出即回收。
            try { var job = new JobObject(256L * 1024 * 1024); job.Assign(proc.Handle); entry.Job = job; }
            catch (Exception ex) { _log.LogWarning(ex, "Job Object 设置失败（继续）"); }

            using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.WaitForConnectionAsync(acceptCts.Token);

            var hostSvc = new HostToAgentService(pluginId, _broker, _perms, _storage, _appHub, _plugins,
                _lf.CreateLogger($"Host:{pluginId}"));
            var (rpc, api) = RpcFactory.CreateWithProxy<IHostApi>(server, hostSvc);
            entry.Rpc = rpc; entry.Api = api;
            rpc.Disconnected += (_, args) => OnDisconnected(entry, args);
            rpc.StartListening();

            await api.InitializeAsync(pluginId, versionDir);

            lock (entry) { entry.State = PluginRunState.Running; }
            _log.LogInformation("插件已启动 {Plugin} (pid {Pid})", pluginId, proc.Id);
            _appHub.PluginStateChanged(Info(pluginId, entry));

            _ = Task.Run(() => HeartbeatLoop(entry));
            await AttachMatchingDevicesAsync(entry);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "启动插件失败 {Plugin}", pluginId);
            lock (entry) { entry.State = PluginRunState.Crashed; entry.LastError = ex.Message; }
            _appHub.PluginStateChanged(Info(pluginId, entry));
            return false;
        }
    }

    private async Task AttachMatchingDevicesAsync(HostEntry entry)
    {
        if (entry.Api is null) return;
        // 新流程：只接管用户已授权（Always）的设备；未授权设备留在"新设备"页等待授权。
        foreach (var dev in _broker.Enumerate())
        {
            if (!_plugins.MatchPlugins(dev).Contains(entry.PluginId)) continue;
            if (_perms.GetGrant(entry.PluginId, dev.DeviceKey) != AccessScope.Always) continue;
            if (!entry.AttachedDevices.Add(dev.DeviceKey)) continue;
            try { await entry.Api.AttachDeviceAsync(dev); _log.LogInformation("已接管设备 {Dev} → {Plugin}", dev.DeviceKey, entry.PluginId); }
            catch (Exception ex) { _log.LogWarning(ex, "接管设备失败 {Dev}", dev.DeviceKey); }
        }
    }

    /// <summary>设备集合变化后调用：运行中的插件接管新出现的已授权设备，并放开已消失的设备。</summary>
    public async Task SyncDevicesAsync()
    {
        var present = _broker.Enumerate().Select(d => d.DeviceKey).ToHashSet();
        foreach (var e in _hosts.Values.Where(e => e.State == PluginRunState.Running))
        {
            foreach (var gone in e.AttachedDevices.Where(k => !present.Contains(k)).ToList())
            {
                e.AttachedDevices.Remove(gone);
                try { if (e.Api is { } api) await api.DetachDeviceAsync(gone); _log.LogInformation("设备已移除 {Dev} ← {Plugin}", gone, e.PluginId); }
                catch (Exception ex) { _log.LogDebug(ex, "通知设备移除失败 {Dev}", gone); }
            }
            await AttachMatchingDevicesAsync(e);
        }
    }

    /// <summary>授权后由 App 调用：确保插件已启动并接管指定设备。</summary>
    public async Task<bool> AttachDeviceToAsync(string pluginId, string deviceKey)
    {
        await StartAsync(pluginId);
        if (!_hosts.TryGetValue(pluginId, out var e) || e.Api is null) return false;
        var dev = _broker.Find(deviceKey);
        if (dev is null) return false;
        if (e.AttachedDevices.Add(deviceKey))
        {
            try { await e.Api.AttachDeviceAsync(dev); _log.LogInformation("已接管设备 {Dev} → {Plugin}", deviceKey, pluginId); }
            catch (Exception ex) { _log.LogWarning(ex, "接管设备失败 {Dev}", deviceKey); return false; }
        }
        return true;
    }

    public async Task<string> InvokeAsync(string pluginId, string deviceKey, string method, string argsJson)
    {
        if (!_hosts.TryGetValue(pluginId, out var e) || e.Api is null) throw new InvalidOperationException("插件未运行");
        return await e.Api.InvokeAsync(deviceKey, method, argsJson);
    }

    public async Task<string> GetStateAsync(string pluginId, string deviceKey)
    {
        if (!_hosts.TryGetValue(pluginId, out var e) || e.Api is null) throw new InvalidOperationException("插件未运行");
        return await e.Api.GetStateAsync(deviceKey);
    }

    /// <summary>向插件索取设备描述；插件未运行或出错时返回 null（UI 回退到核心信息）。</summary>
    public async Task<DeviceDescription?> DescribeDeviceAsync(string pluginId, string deviceKey)
    {
        if (!_hosts.TryGetValue(pluginId, out var e) || e.Api is null) return null;
        try { return await e.Api.DescribeDeviceAsync(deviceKey).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { _log.LogDebug(ex, "获取设备描述失败 {Plugin}", pluginId); return null; }
    }

    /// <summary>优雅停止（更新前调用）：通知插件刷写状态后结束进程。</summary>
    public async Task StopAsync(string pluginId)
    {
        if (!_hosts.TryGetValue(pluginId, out var e)) return;
        e.ShuttingDown = true;
        try { if (e.Api != null) await e.Api.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        Kill(e);
        e.State = PluginRunState.Stopped;
        _appHub.PluginStateChanged(Info(pluginId, e));
    }

    private async Task HeartbeatLoop(HostEntry e)
    {
        while (!e.ShuttingDown && e.State == PluginRunState.Running && e.Api != null)
        {
            try
            {
                await Task.Delay(5000);
                if (e.ShuttingDown) return;
                await e.Api.GetSchemaVersionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                if (e.ShuttingDown) return;
                _log.LogWarning("心跳失败 {Plugin}: {Msg}", e.PluginId, ex.Message);
                OnDisconnected(e, null);
                return;
            }
        }
    }

    private void OnDisconnected(HostEntry e, JsonRpcDisconnectedEventArgs? args)
    {
        lock (e)
        {
            if (e.ShuttingDown || e.State == PluginRunState.Crashed) return;
            e.State = PluginRunState.Crashed;
            e.LastError = args?.Description ?? "连接断开";
        }
        _log.LogWarning("插件连接断开 {Plugin}: {Err}", e.PluginId, e.LastError);
        Kill(e);
        _ = Task.Run(() => RestartWithBackoff(e));
    }

    private async Task RestartWithBackoff(HostEntry e)
    {
        if (e.RestartCount >= MaxRestarts)
        {
            _log.LogError("插件熔断 {Plugin}（重启 {N} 次）", e.PluginId, e.RestartCount);
            lock (e) { e.State = PluginRunState.Disabled; }
            _appHub.PluginStateChanged(Info(e.PluginId, e));
            return;
        }
        var delay = TimeSpan.FromSeconds(Math.Pow(2, e.RestartCount)); // 1,2,4,8,16s
        e.RestartCount++;
        _log.LogInformation("{Delay}s 后重启 {Plugin}（第 {N} 次）", delay.TotalSeconds, e.PluginId, e.RestartCount);
        await Task.Delay(delay);
        e.AttachedDevices.Clear();
        lock (e) { e.State = PluginRunState.Stopped; }
        await StartAsync(e.PluginId);
    }

    private void Kill(HostEntry e)
    {
        try { e.Rpc?.Dispose(); } catch { }
        try { if (e.Process is { HasExited: false } p) p.Kill(true); } catch { }
        try { e.Job?.Dispose(); } catch { }
        e.Rpc = null; e.Api = null; e.Job = null;
    }

    private PluginInfo Info(string id, HostEntry e)
    {
        var m = _plugins.ActiveManifest(id);
        return new PluginInfo
        {
            Id = id, DisplayName = m?.DisplayName ?? id, Version = m?.Version ?? "?",
            State = e.State, RestartCount = e.RestartCount, LastError = e.LastError,
            UiAssemblyPath = AbsUi(id, m), UiPageTypeName = m?.UiPageType,
        };
    }

    private string? AbsUi(string pluginId, PluginManifest? m) =>
        m?.UiAssembly is { Length: > 0 } rel ? Path.Combine(_plugins.ActiveVersionDir(pluginId), rel) : null;

    public void Dispose()
    {
        foreach (var e in _hosts.Values) { e.ShuttingDown = true; Kill(e); }
    }
}
