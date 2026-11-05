// 直连串口驱动 Quectel 插件的 ModuleSession（绕过 Agent），用来在真模块上核对解析结果。
//   QuectelProbe COM5                 只读：读一遍信息与状态，打印状态 JSON
//   QuectelProbe COM5 --lock-test     锁定当前服务小区 → 读回 → 解锁 → 读回（会短暂改变模块设置）
//   QuectelProbe COM5 --at "AT+CSQ"   经 AT 控制台执行一条命令
using System.IO.Ports;
using System.Threading.Channels;
using DeviceHub.Contracts;
using DeviceHub.Plugin.Quectel;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Contains("--diag")) { DiagSelfTest.Run(); return; }
var com =args.FirstOrDefault(a => a.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) ?? "COM5";
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

/// <summary>
/// "驱动 / 电脑联网"诊断的离线用例：用合成的通道列表和状态覆盖真机上不方便复现的情况（卸驱动、换模式）。
/// </summary>
static class DiagSelfTest
{
    static HidChannelInfo Ch(int mi, string kind, string? detail = null) =>
        new() { Id = $"mi{mi:x2}-{kind}", Kind = kind, Interface = mi, Detail = detail };

    static QuectelState Ok(int usbNet) => new()
    {
        Module = { UsbNet = usbNet },
        Sim = { State = "READY" },
        Network = { Cfun = 1, EpsRegistration = 5, Ip = "10.0.0.2" },
    };

    public static void Run()
    {
        var full = new[] { Ch(0, "serial", "COM3"), Ch(1, "serial", "COM4"), Ch(2, "serial", "COM5"), Ch(4, "net", "{00000000-0000-0000-0000-000000000001}") };
        var noDriver = new[] { Ch(0, "nodriver", "未安装驱动"), Ch(1, "nodriver", "未安装驱动"), Ch(2, "nodriver", "未安装驱动"), Ch(3, "nodriver", "未安装驱动"), Ch(4, "nodriver", "未安装驱动") };
        var serialOnly = new[] { Ch(0, "serial", "COM3"), Ch(1, "serial", "COM4"), Ch(2, "serial", "COM5"), Ch(4, "nodriver", "未安装驱动") };
        var noNet = new[] { Ch(0, "serial", "COM3"), Ch(1, "serial", "COM4"), Ch(2, "serial", "COM5") };
        var up = new AdapterSnapshot("Remote NDIS Compatible Device", true, "已连接", "192.168.225.20", 100_000_000);
        var down = up with { Up = false, Status = "未连接", Ipv4 = "" };
        var qmiDown = new AdapterSnapshot("Quectel Wireless Ethernet Adapter", false, "未连接", "", 0);

        int fail = 0;
        void Case(string name, IReadOnlyList<HidChannelInfo> chs, QuectelState st, AdapterSnapshot? a, string expectSeverity, string expectContains, int expectSuggest = -1)
        {
            var layout = ModuleLayout.From(chs);
            var r = PcLink.Evaluate(layout, st, a);
            bool ok = r.Severity == expectSeverity && r.Summary.Contains(expectContains) && r.SuggestUsbNet == expectSuggest;
            if (!ok) fail++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: at={layout.AtPort ?? "-"} missing={layout.Missing.Count} → [{r.Severity}] {r.Summary} (建议模式 {r.SuggestUsbNet})");
        }

        // 驱动层
        var l0 = ModuleLayout.From(noDriver);
        Console.WriteLine($"{(l0.AtPort is null && l0.Missing.Count == 5 ? "PASS" : "FAIL")} 全部没驱动：AT 口不可用，缺 {l0.Missing.Count} 个：{string.Join("；", l0.Missing)}");
        if (l0.AtPort is not null || l0.Missing.Count != 5) fail++;

        Case("全部没驱动（模式未知）", noDriver, new QuectelState(), null, "error", "网卡接口没有驱动");
        Case("ECM 模式、网卡没驱动", serialOnly, Ok(1), null, "error", "ECM", 3);
        Case("QMI 模式、网卡没驱动", serialOnly, Ok(0), null, "error", "Quectel USB 驱动", 3);
        Case("没有网卡接口", noNet, Ok(1), null, "error", "没有提供 USB 网卡");
        Case("ECM 模式装了 QMI 驱动（本机实测情况）", full, Ok(1), qmiDown, "error", "不匹配", 3);
        Case("RNDIS 正常上网", full, Ok(3), up, "ok", "已连接");
        Case("RNDIS 网卡未连接", full, Ok(3), down, "warning", "网卡未连接");
        Case("QMI 模式等 Windows 拨号", full, Ok(0), qmiDown, "warning", "需要由 Windows 拨号");
        var noSim = Ok(3); noSim.Sim.State = "NOT INSERTED";
        Case("SIM 未插", full, noSim, down, "error", "SIM 卡不可用");
        var noReg = Ok(3); noReg.Network.EpsRegistration = 2;
        Case("未注册", full, noReg, down, "warning", "还没注册");
        var noPdp = Ok(3); noPdp.Network.Ip = "";
        Case("已注册未拨号", full, noPdp, down, "warning", "没有建立数据连接");
        var air = Ok(3); air.Network.Cfun = 4;
        Case("飞行模式", full, air, down, "warning", "飞行模式");
        Case("网卡在系统里找不到", full, Ok(3), null, "warning", "找不到");

        Console.WriteLine(fail == 0 ? "全部通过" : $"{fail} 项失败");
        Environment.ExitCode = fail;
    }
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
