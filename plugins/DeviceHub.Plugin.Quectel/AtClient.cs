using System.Text;
using DeviceHub.Contracts;

namespace DeviceHub.Plugin.Quectel;

/// <summary>AT 口的字节收发。插件里经核心 Broker 代理（<see cref="BrokerTransport"/>），测试工具里可直连串口。</summary>
public interface IAtTransport
{
    Task<DeviceIoResult> WriteAsync(byte[] data);
    /// <summary>下一段收到的字节；timeoutMs 内没有数据返回 Timeout。</summary>
    Task<DeviceIoResult> ReadAsync(int timeoutMs);
}

/// <summary>一条 AT 命令的结果：中间响应行（不含回显与结果码）+ 是否 OK + 错误文本。</summary>
public sealed record AtResponse(bool Ok, IReadOnlyList<string> Lines, string? Error)
{
    /// <summary>以 prefix（如 "+CSQ:"）开头的行，去掉前缀并修剪。</summary>
    public IEnumerable<string> Values(string prefix) =>
        Lines.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l[prefix.Length..].Trim());
    public string? Value(string prefix) => Values(prefix).FirstOrDefault();
    public static AtResponse Failed(string error) => new(false, [], error);
}

/// <summary>
/// AT 命令引擎。一个后台泵持续读 AT 口、按行切分：有命令在等待时，行归该命令，直到结果码
/// （OK / ERROR / +CME ERROR / +CMS ERROR）；其余行（以及命令期间到来的已知主动上报）交给 <see cref="Urc"/>。
/// 命令串行执行，彼此的响应不会交错。
/// </summary>
public sealed class AtClient : IAsyncDisposable
{
    private readonly IAtTransport _io;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly StringBuilder _rx = new();
    private readonly object _lock = new();
    private Pending? _pending;
    private Task? _pump;

    /// <summary>主动上报（+CMTI、RING、+QIND …），在泵线程上回调。</summary>
    public event Action<string>? Urc;
    /// <summary>通道失效（授权撤销、设备拔出），之后所有命令立即失败。</summary>
    public event Action<DeviceIoStatus, string?>? Faulted;
    /// <summary>每条收发的原始行（"&gt; AT…" / "&lt; …"），供 AT 控制台显示。</summary>
    public event Action<string>? Trace;

    public DeviceIoStatus? Fault { get; private set; }
    public string? FaultMessage { get; private set; }

    public AtClient(IAtTransport io) => _io = io;

    private sealed class Pending
    {
        public required string Command;
        public readonly List<string> Lines = new();
        public readonly TaskCompletionSource<AtResponse> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>等待 "&gt; " 输入提示（AT+CMGS）。</summary>
        public TaskCompletionSource? Prompt;
    }

    public void Start() => _pump ??= Task.Run(PumpAsync);

    /// <summary>发送一条命令并等待结果码。</summary>
    public Task<AtResponse> SendAsync(string command, int timeoutMs = 5000) => RunAsync(command, null, timeoutMs);

    /// <summary>
    /// 两段式命令（AT+CMGS）：发命令、等 "&gt; " 提示，再发 body + Ctrl-Z，等结果码。
    /// </summary>
    public Task<AtResponse> SendWithPromptAsync(string command, string body, int timeoutMs = 60000) => RunAsync(command, body, timeoutMs);

    private async Task<AtResponse> RunAsync(string command, string? body, int timeoutMs)
    {
        if (Fault is not null) return AtResponse.Failed(FaultMessage ?? Fault.ToString() ?? "");
        await _gate.WaitAsync(_stop.Token);
        try
        {
            if (Fault is not null) return AtResponse.Failed(FaultMessage ?? Fault.ToString() ?? "");
            var p = new Pending { Command = command, Prompt = body is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously) };
            lock (_lock) _pending = p;
            Trace?.Invoke("> " + command);
            var w = await _io.WriteAsync(Encoding.ASCII.GetBytes(command + "\r"));
            if (w.Status != DeviceIoStatus.Ok)
            {
                // Busy（端口正在打开 / 被占用）和一般错误只让这条命令失败；授权撤销、设备拔出才算通道失效。
                if (w.Status is not (DeviceIoStatus.Busy or DeviceIoStatus.Error or DeviceIoStatus.Timeout)) SetFault(w.Status, w.Message);
                return AtResponse.Failed(Describe(w));
            }

            if (p.Prompt is not null)
            {
                var first = await Task.WhenAny(p.Prompt.Task, p.Done.Task, Task.Delay(5000, _stop.Token));
                if (first != p.Prompt.Task)
                {
                    if (first == p.Done.Task) return await p.Done.Task;
                    await _io.WriteAsync([0x1B]); // ESC 取消
                    return AtResponse.Failed("模块没有给出输入提示");
                }
                Trace?.Invoke("> " + body);
                await _io.WriteAsync(Encoding.ASCII.GetBytes(body + "\x1A"));
            }

            var done = await Task.WhenAny(p.Done.Task, Task.Delay(timeoutMs, _stop.Token));
            if (done == p.Done.Task) return await p.Done.Task;
            Trace?.Invoke("< (超时)");
            return AtResponse.Failed("模块无响应（超时）");
        }
        catch (OperationCanceledException) { return AtResponse.Failed("已停止"); }
        finally
        {
            lock (_lock) _pending = null;
            _gate.Release();
        }
    }

    private async Task PumpAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            DeviceIoResult r;
            try { r = await _io.ReadAsync(1000); }
            catch (Exception ex) { r = DeviceIoResult.Fail(DeviceIoStatus.Error, ex.Message); }
            if (r.Status == DeviceIoStatus.Timeout) continue;
            if (r.Status != DeviceIoStatus.Ok)
            {
                if (r.Status is DeviceIoStatus.Busy or DeviceIoStatus.Error)
                {
                    // 端口暂时打不开（被占用）：报告但继续重试，命令会各自失败。
                    FaultMessage = Describe(r);
                    try { await Task.Delay(2000, _stop.Token); } catch { }
                    continue;
                }
                SetFault(r.Status, r.Message);
                return;
            }
            if (r.Data is { Length: > 0 } data) Feed(Encoding.ASCII.GetString(data));
        }
    }

    private void Feed(string text)
    {
        lock (_lock)
        {
            _rx.Append(text);
            // AT+CMGS 的 "> " 提示不带换行。
            if (_pending?.Prompt is { Task.IsCompleted: false } prompt && _rx.ToString().TrimStart('\r', '\n').StartsWith('>'))
            {
                _rx.Clear();
                prompt.TrySetResult();
                return;
            }
            while (true)
            {
                var s = _rx.ToString();
                int nl = s.IndexOfAny(['\r', '\n']);
                if (nl < 0) break;
                var line = s[..nl].Trim();
                int skip = nl + 1;
                while (skip < s.Length && s[skip] is '\r' or '\n') skip++;
                _rx.Remove(0, skip);
                if (line.Length > 0) OnLine(line);
            }
        }
    }

    private static readonly string[] UrcPrefixes =
        ["+CMTI:", "+CMT:", "+CDS:", "+CDSI:", "RING", "+CLIP:", "NO CARRIER", "+QIND:", "+QUSIM:", "+QGPSURC:", "+CPIN:", "RDY", "POWERED DOWN", "+CFUN:", "+QNETDEVSTATUS:", "+CGEV:", "+QSIMSTAT:", "+CREG:", "+CEREG:", "+CGREG:"];

    private void OnLine(string line)
    {
        Trace?.Invoke("< " + line);
        var p = _pending;
        if (p is null || IsUrcFor(line, p.Command)) { Urc?.Invoke(line); return; }
        if (line == p.Command || line.StartsWith("AT", StringComparison.OrdinalIgnoreCase) && line.Equals(p.Command, StringComparison.OrdinalIgnoreCase)) return; // 回显
        if (line == "OK") { p.Done.TrySetResult(new AtResponse(true, p.Lines.ToList(), null)); return; }
        if (line == "ERROR" || line.StartsWith("+CME ERROR:", StringComparison.Ordinal) || line.StartsWith("+CMS ERROR:", StringComparison.Ordinal))
        { p.Done.TrySetResult(new AtResponse(false, p.Lines.ToList(), line)); return; }
        p.Lines.Add(line);
    }

    /// <summary>命令执行期间到来的行是否是主动上报：以已知上报前缀开头，且不是该命令本身的响应（如 AT+CPIN? 的 +CPIN:）。</summary>
    private static bool IsUrcFor(string line, string command)
    {
        foreach (var u in UrcPrefixes)
        {
            if (!line.StartsWith(u, StringComparison.Ordinal)) continue;
            var name = u.TrimEnd(':');
            return !(name.StartsWith('+') && command.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    private void SetFault(DeviceIoStatus s, string? msg)
    {
        if (Fault is not null) return;
        Fault = s;
        FaultMessage = msg;
        lock (_lock) _pending?.Done.TrySetResult(AtResponse.Failed(Describe(DeviceIoResult.Fail(s, msg))));
        Faulted?.Invoke(s, msg);
    }

    public static string Describe(DeviceIoResult r) => r.Status switch
    {
        DeviceIoStatus.AccessDenied or DeviceIoStatus.AccessRevoked => "未授权",
        DeviceIoStatus.DeviceGone => "模块已断开",
        DeviceIoStatus.Busy => r.Message ?? "AT 口正被其他程序占用",
        _ => r.Message ?? r.Status.ToString(),
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_pump is not null) { try { await _pump; } catch { } }
        _stop.Dispose();
    }
}

/// <summary>经核心 Broker 代理的 AT 口。</summary>
public sealed class BrokerTransport(DeviceHub.Sdk.IDeviceChannel device, string deviceKey, string channel) : IAtTransport
{
    public Task<DeviceIoResult> WriteAsync(byte[] data) => device.WriteOutputAsync(deviceKey, data, channel);
    public Task<DeviceIoResult> ReadAsync(int timeoutMs) => device.ReadInputAsync(deviceKey, timeoutMs, channel);
}
