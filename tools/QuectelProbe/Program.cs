// 直连串口驱动 Quectel 插件的 ModuleSession（绕过 Agent），用来在真模块上核对解析结果。
//   QuectelProbe COM5                 只读：读一遍信息与状态，打印状态 JSON
//   QuectelProbe COM5 --lock-test     锁定当前服务小区 → 读回 → 解锁 → 读回（会短暂改变模块设置）
//   QuectelProbe COM5 --at "AT+CSQ"   经 AT 控制台执行一条命令
using System.IO.Ports;
using System.Threading.Channels;
using DeviceHub.Contracts;
using DeviceHub.Plugin.Quectel;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var com = args.FirstOrDefault(a => a.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) ?? "COM5";
using var port = new SerialPort(com, 115200) { DtrEnable = true, RtsEnable = true };
port.Open();
var io = new PortTransport(port);
var st = new QuectelState { Authorized = true, Connected = true };
var store = new Dictionary<string, string?>();
await using var s = new ModuleSession(io, st, () => Task.CompletedTask,
    k => Task.FromResult(store.GetValueOrDefault(k)), (k, v) => { store[k] = v; return Task.CompletedTask; });
s.Notice += (k, t) => Console.WriteLine($"[notice] {k}: {t}");

await s.StartAsync();
Print("初始状态");

if (args.Contains("--lock-test") && st.Serving is { Earfcn: { } e, Pci: { } p })
{
    await s.LockCellAsync(e, p);
    Console.WriteLine($"lock: {st.Status}; cells={string.Join(";", st.Locks.Cells.Select(c => $"{c.Earfcn}/{c.Pci}"))}");
    await s.UnlockAsync();
    Console.WriteLine($"unlock: {st.Status}; locked={st.Locks.Locked}");
}
int i = Array.IndexOf(args, "--at");
if (i >= 0 && i + 1 < args.Length)
{
    await s.RunAtAsync(args[i + 1]);
    Console.WriteLine(string.Join("\n", st.Console));
}

void Print(string title)
{
    Console.WriteLine($"===== {title} =====");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(st, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));
}

sealed class PortTransport : IAtTransport
{
    private readonly SerialPort _p;
    private readonly Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>();
    public PortTransport(SerialPort p)
    {
        _p = p;
        _p.DataReceived += (_, _) =>
        {
            var n = _p.BytesToRead;
            if (n <= 0) return;
            var b = new byte[n];
            n = _p.Read(b, 0, n);
            _rx.Writer.TryWrite(b[..n]);
        };
    }
    public Task<DeviceIoResult> WriteAsync(byte[] data) { _p.Write(data, 0, data.Length); return Task.FromResult(DeviceIoResult.Success()); }
    public async Task<DeviceIoResult> ReadAsync(int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return DeviceIoResult.Success(await _rx.Reader.ReadAsync(cts.Token)); }
        catch (OperationCanceledException) { return DeviceIoResult.Fail(DeviceIoStatus.Timeout); }
    }
}
