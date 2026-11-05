using DeviceHub.Contracts;

namespace DeviceHub.Plugin.Quectel;

/// <summary>
/// 一台模块的会话：持有 AT 引擎与状态，实现全部查询与管理操作。和插件宿主无关（宿主只提供 AT 口与推送），
/// 所以测试工具也能直连串口驱动它。
/// <para>
/// 只读查询随时可做；会改变模块设置的操作（锁频段、锁小区、网卡模式、重启…）由 UI 先征得用户确认再调用。
/// ATE0 / AT+CMEE=2 / 短信相关的 CMGF、CSCS、CSMP 都是易失设置（重启恢复），短信操作完会还原原值。
/// </para>
/// </summary>
public sealed class ModuleSession : IAsyncDisposable
{
    private readonly AtClient _at;
    private readonly Func<Task> _push;
    private readonly Func<string, string?, Task> _store;   // (key, value) 保存
    private readonly Func<string, Task<string?>> _load;
    private readonly CancellationTokenSource _stop = new();
    private Task? _poller;
    private int _userOps;

    public QuectelState State { get; }
    /// <summary>值得提醒用户的事件（新短信、来电、SIM 拔出），参数为 (种类, 文本)。</summary>
    public event Action<OsdKind, string>? Notice;

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private const int ConsoleMax = 300;

    public ModuleSession(IAtTransport io, QuectelState state, Func<Task> push,
        Func<string, Task<string?>> load, Func<string, string?, Task> store)
    {
        State = state;
        _push = push;
        _load = load;
        _store = store;
        _at = new AtClient(io);
        _at.Urc += OnUrc;
        _at.Faulted += (s, m) =>
        {
            State.Status = AtClient.Describe(DeviceIoResult.Fail(s, m));
            if (s is DeviceIoStatus.DeviceGone) State.Connected = false;
            if (s is DeviceIoStatus.AccessDenied or DeviceIoStatus.AccessRevoked) State.Authorized = false;
            _ = _push();
        };
    }

    /// <summary>开始：关回显、开详细错误，读一遍模块信息与状态，然后定时轮询。</summary>
    public async Task StartAsync()
    {
        _at.Start();
        // 端口可能还在打开（驱动释放上一个持有者要二三十秒）或被别的程序占着：最多等 90 秒，期间把原因显示出来。
        var deadline = DateTime.UtcNow.AddSeconds(90);
        AtResponse hello;
        while (true)
        {
            hello = await _at.SendAsync("ATE0", 3000);
            if (hello.Ok || _at.Fault is not null || DateTime.UtcNow > deadline || _stop.IsCancellationRequested) break;
            State.Status = "等待 AT 口：" + hello.Error;
            await _push();
            try { await Task.Delay(2000, _stop.Token); } catch (OperationCanceledException) { return; }
        }
        if (!hello.Ok) { State.Status = "AT 口无响应：" + hello.Error; await _push(); }
        await _at.SendAsync("AT+CMEE=2");
        await ReadInfoAsync();
        await PollAsync();
        State.Status = _at.Fault is null ? "" : State.Status;
        await _push();
        _poller = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                if (_at.Fault is not null) return;
                if (Volatile.Read(ref _userOps) > 0) continue; // 用户操作进行中，下一轮再刷
                await PollAsync();
                await _push();
            }
        }
        catch (OperationCanceledException) { }
    }

    // —— 读取 ——

    /// <summary>不常变的信息：型号、IMEI、SIM、频段与锁定配置、USB 模式、GPS 与短信存储。</summary>
    public async Task ReadInfoAsync()
    {
        var m = State.Module;
        m.Manufacturer = (await Q("AT+CGMI")).Lines.FirstOrDefault() ?? m.Manufacturer;
        m.Model = (await Q("AT+CGMM")).Lines.FirstOrDefault() ?? m.Model;
        m.Revision = (await Q("AT+QGMR")).Lines.FirstOrDefault() ?? m.Revision;
        m.Imei = (await Q("AT+CGSN")).Lines.FirstOrDefault(l => l.All(char.IsDigit)) ?? m.Imei;
        m.UsbNet = AtParse.Int(AtParse.Csv((await Q("AT+QCFG=\"usbnet\"")).Value("+QCFG:") ?? "").ElementAtOrDefault(1));
        m.UsbConfig = (await Q("AT+QCFG=\"usbcfg\"")).Value("+QCFG:") is { } uc ? string.Join(",", AtParse.Csv(uc).Skip(1)) : m.UsbConfig;
        var mbn = (await Q("AT+QMBNCFG=\"list\"")).Values("+QMBNCFG:").Select(AtParse.Csv)
            .FirstOrDefault(f => f.Count > 4 && f[2] == "1" && f[3] == "1");
        m.ActiveMbn = mbn?[4] ?? m.ActiveMbn;

        await ReadSimAsync();
        await ReadBandsAsync();
        await ReadLocksAsync();
        await ReadNetConfigAsync();

        State.Gps.On = AtParse.Int((await Q("AT+QGPS?")).Value("+QGPS:")) is { } g ? g == 1 : State.Gps.On;
        var (mem, used, total) = AtParse.Cpms((await Q("AT+CPMS?")).Value("+CPMS:"));
        if (mem.Length > 0) { State.Sms.Storage = mem; State.Sms.Used = used; State.Sms.Total = total; }
    }

    private async Task ReadSimAsync()
    {
        var s = State.Sim;
        var cpin = await Q("AT+CPIN?");
        s.State = cpin.Value("+CPIN:") ?? (cpin.Error?.Contains("not inserted", StringComparison.OrdinalIgnoreCase) == true ? "NOT INSERTED" : cpin.Error ?? "");
        if (s.State != "READY") return;
        s.Imsi = (await Q("AT+CIMI")).Lines.FirstOrDefault(l => l.All(char.IsDigit)) ?? s.Imsi;
        s.Iccid = (await Q("AT+QCCID")).Value("+QCCID:") ?? s.Iccid;
        s.Smsc = AtParse.Csv((await Q("AT+CSCA?")).Value("+CSCA:") ?? "").FirstOrDefault() ?? s.Smsc;
        // 有的 SIM 卡在本机号码记录里存的是短信中心号码，那就当作没有。
        var num = AtParse.Csv((await Q("AT+CNUM")).Value("+CNUM:") ?? "").ElementAtOrDefault(1) ?? "";
        s.Number = num == s.Smsc ? "" : num;
    }

    private async Task ReadBandsAsync()
    {
        var b = State.Bands;
        var (gw, lte) = AtParse.BandMasks((await Q("AT+QCFG=\"band\"")).Value("+QCFG:"));
        if (lte.Length == 0) return;
        b.GwMask = gw;
        b.LteMask = lte;
        b.Enabled = AtParse.MaskToBands(lte);
        // 出厂掩码：第一次见到这台模块（按 IMEI）时记下当时的掩码，用于"恢复全部频段"。
        var key = "factoryLte:" + (State.Module.Imei.Length > 0 ? State.Module.Imei : "unknown");
        var factory = await _load(key);
        if (factory is null || AtParse.MaskToBands(factory).Count < b.Enabled.Count)
        {
            factory = lte;
            await _store(key, factory);
        }
        b.FactoryLteMask = factory;
        b.Supported = AtParse.MaskToBands(factory);
    }

    private async Task ReadLocksAsync()
    {
        var l = State.Locks;
        l.Cells = AtParse.Lock4g((await Q("AT+QNWLOCK=\"common/4g\"")).Value("+QNWLOCK:"));
        (l.LteAction, l.LteEarfcn, l.LtePci) = AtParse.LockLte((await Q("AT+QNWLOCK=\"common/lte\"")).Value("+QNWLOCK:"));
    }

    private async Task ReadNetConfigAsync()
    {
        var n = State.Network;
        n.ScanMode = AtParse.Int(AtParse.Csv((await Q("AT+QCFG=\"nwscanmode\"")).Value("+QCFG:") ?? "").ElementAtOrDefault(1)) ?? n.ScanMode;
        n.ScanSequence = AtParse.Csv((await Q("AT+QCFG=\"nwscanseq\"")).Value("+QCFG:") ?? "").ElementAtOrDefault(1) ?? n.ScanSequence;
        n.RoamService = AtParse.Int(AtParse.Csv((await Q("AT+QCFG=\"roamservice\"")).Value("+QCFG:") ?? "").ElementAtOrDefault(1)) ?? n.RoamService;
        n.ImsEnabled = AtParse.Int(AtParse.Csv((await Q("AT+QCFG=\"ims\"")).Value("+QCFG:") ?? "").ElementAtOrDefault(1)) is { } ims ? ims == 1 : n.ImsEnabled;
    }

    /// <summary>常变的状态：信号、注册、服务小区与邻区、IP、温度、GPS 定位。</summary>
    public async Task PollAsync()
    {
        var n = State.Network;
        n.Cfun = AtParse.Int((await Q("AT+CFUN?")).Value("+CFUN:")) ?? n.Cfun;
        var prevSim = State.Sim.State;
        var cpin = await Q("AT+CPIN?");
        State.Sim.State = cpin.Value("+CPIN:") ?? (cpin.Ok ? State.Sim.State : "NOT READY");
        if (State.Sim.State == "READY" && prevSim != "READY") await ReadSimAsync();

        var sig = State.Signal;
        sig.Csq = AtParse.Csq((await Q("AT+CSQ")).Value("+CSQ:"));
        var (rat, rssi, rsrp, sinr, rsrq) = AtParse.Qcsq((await Q("AT+QCSQ")).Value("+QCSQ:"));
        sig.Rssi = rssi ?? (sig.Csq is { } c ? -113 + 2 * c : null);
        sig.Rsrp = rsrp; sig.Rsrq = rsrq; sig.Sinr = sinr;
        sig.Bars = rat == "NOSERVICE" ? 0 : AtParse.Bars(sig.Rsrp, sig.Rssi);

        n.EpsRegistration = AtParse.RegStat((await Q("AT+CEREG?")).Value("+CEREG:"));
        n.CsRegistration = AtParse.RegStat((await Q("AT+CREG?")).Value("+CREG:"));
        n.Operator = AtParse.Cops((await Q("AT+COPS?")).Value("+COPS:"));
        (n.Act, n.Plmn, n.Band, n.Channel) = AtParse.Qnwinfo((await Q("AT+QNWINFO")).Value("+QNWINFO:"));
        if (n.Act == "No Service") n.Act = "";

        State.Serving = AtParse.Serving((await Q("AT+QENG=\"servingcell\"")).Values("+QENG:"));
        State.Neighbours = AtParse.Neighbours((await Q("AT+QENG=\"neighbourcell\"")).Values("+QENG:"));
        n.Ip = AtParse.Ip((await Q("AT+CGPADDR")).Values("+CGPADDR:"));
        State.Temperatures = AtParse.Temps((await Q("AT+QTEMP")).Value("+QTEMP:"));

        if (State.Gps.On == true) await ReadGpsAsync();
        State.LastPollAt = DateTimeOffset.Now;
    }

    private async Task ReadGpsAsync()
    {
        var r = await Q("AT+QGPSLOC=2");
        if (r.Ok && AtParse.GpsLoc(r.Value("+QGPSLOC:")) is { } fix) { fix.Note = ""; State.Gps = fix; return; }
        State.Gps = new GpsInfo
        {
            On = true, Fix = false,
            Note = r.Error?.Contains("516") == true || r.Error?.Contains("Not fixed", StringComparison.OrdinalIgnoreCase) == true
                ? "正在搜星（尚未定位，需要 GNSS 天线和开阔天空）" : r.Error ?? "",
        };
    }

    // —— 管理操作（会改变模块设置，由 UI 确认后调用）——

    /// <summary>只允许指定的 LTE 频段（必须是模块支持的频段的非空子集）。立即生效，掉电保持。</summary>
    public Task SetBandsAsync(IReadOnlyCollection<int> lteBands) => UserOp("设置频段", async () =>
    {
        var bands = lteBands.Distinct().OrderBy(b => b).ToList();
        if (bands.Count == 0) return "至少保留一个频段";
        var bad = bands.Where(b => !State.Bands.Supported.Contains(b)).ToList();
        if (bad.Count > 0) return $"模块不支持 B{string.Join("/B", bad)}";
        if (State.Bands.GwMask.Length == 0) await ReadBandsAsync();
        var r = await W($"AT+QCFG=\"band\",{State.Bands.GwMask},{AtParse.BandsToMask(bands)},1");
        await ReadBandsAsync();
        return r.Ok ? $"已限定为 B{string.Join("/B", bands)}" : "设置频段失败：" + r.Error;
    });

    public Task RestoreBandsAsync() => UserOp("恢复频段", async () =>
    {
        if (State.Bands.FactoryLteMask.Length == 0) await ReadBandsAsync();
        var r = await W($"AT+QCFG=\"band\",{State.Bands.GwMask},{State.Bands.FactoryLteMask},1");
        await ReadBandsAsync();
        return r.Ok ? "已恢复全部频段" : "恢复频段失败：" + r.Error;
    });

    /// <summary>锁定到指定小区（频点 + PCI）。</summary>
    public Task LockCellAsync(int earfcn, int pci) => UserOp("锁定小区", async () =>
    {
        if (earfcn is < 0 or > 262143 || pci is < 0 or > 503) return "频点或 PCI 超出范围";
        var r = await W($"AT+QNWLOCK=\"common/4g\",1,{earfcn},{pci}");
        await ReadLocksAsync();
        return r.Ok ? $"已锁定小区 EARFCN {earfcn} / PCI {pci}" : "锁定小区失败：" + r.Error;
    });

    /// <summary>锁定频点（该频点上的任意小区）。</summary>
    public Task LockFrequencyAsync(int earfcn) => UserOp("锁定频点", async () =>
    {
        if (earfcn is < 0 or > 262143) return "频点超出范围";
        var r = await W($"AT+QNWLOCK=\"common/lte\",1,{earfcn},0");
        await ReadLocksAsync();
        return r.Ok ? $"已锁定频点 EARFCN {earfcn}" : "锁定频点失败：" + r.Error;
    });

    public Task UnlockAsync() => UserOp("解除锁定", async () =>
    {
        var a = await W($"AT+QNWLOCK=\"common/4g\",0");
        var b = await W($"AT+QNWLOCK=\"common/lte\",0");
        await ReadLocksAsync();
        return a.Ok && b.Ok ? "已解除小区 / 频点锁定" : "解除锁定失败：" + (a.Error ?? b.Error);
    });

    /// <summary>0 自动、1 仅 GSM、2 仅 WCDMA、3 仅 LTE。</summary>
    public Task SetScanModeAsync(int mode) => UserOp("设置网络制式", async () =>
    {
        if (mode is < 0 or > 3) return "不支持的模式";
        var r = await W($"AT+QCFG=\"nwscanmode\",{mode},1");
        await ReadNetConfigAsync();
        return r.Ok ? "已设置网络制式" : "设置失败：" + r.Error;
    });

    /// <summary>1 禁止漫游、2 允许漫游、255 自动。</summary>
    public Task SetRoamingAsync(int mode) => UserOp("设置漫游", async () =>
    {
        if (mode is not (1 or 2 or 255)) return "不支持的模式";
        var r = await W($"AT+QCFG=\"roamservice\",{mode},1");
        await ReadNetConfigAsync();
        return r.Ok ? "已设置漫游" : "设置失败：" + r.Error;
    });

    /// <summary>射频开关：开 = AT+CFUN=1，关 = AT+CFUN=4（飞行模式）。</summary>
    public Task SetRadioAsync(bool on) => UserOp(on ? "打开射频" : "飞行模式", async () =>
    {
        var r = await Q(on ? "AT+CFUN=1" : "AT+CFUN=4", 15000);
        State.Network.Cfun = AtParse.Int((await Q("AT+CFUN?")).Value("+CFUN:")) ?? State.Network.Cfun;
        return r.Ok ? (on ? "射频已打开" : "已进入飞行模式") : "操作失败：" + r.Error;
    });

    /// <summary>重启模块（AT+CFUN=1,1）：USB 会断开重连，插件随后重新接管。</summary>
    public Task RebootAsync() => UserOp("重启模块", async () =>
    {
        var r = await Q("AT+CFUN=1,1", 10000);
        return r.Ok ? "模块正在重启，约 20 秒后重新连接" : "重启失败：" + r.Error;
    });

    /// <summary>USB 网卡模式（重启模块后生效）。</summary>
    public Task SetUsbNetAsync(int mode) => UserOp("设置网卡模式", async () =>
    {
        if (mode is < 0 or > 3) return "不支持的模式";
        var r = await W($"AT+QCFG=\"usbnet\",{mode}");
        State.Module.UsbNet = AtParse.Int(AtParse.Csv((await Q("AT+QCFG=\"usbnet\"")).Value("+QCFG:") ?? "").ElementAtOrDefault(1)) ?? State.Module.UsbNet;
        return r.Ok ? $"已设为 {AtParse.UsbNetName(mode)}，重启模块后生效" : "设置失败：" + r.Error;
    });

    public Task SetGpsAsync(bool on) => UserOp(on ? "打开 GPS" : "关闭 GPS", async () =>
    {
        var r = await Q(on ? "AT+QGPS=1" : "AT+QGPSEND");
        // 504 = 会话已在进行（已经开着），505 = 会话未激活（已经关了）。
        bool already = r.Error?.Contains(on ? "504" : "505") == true || r.Error?.Contains("Session is ongoing", StringComparison.OrdinalIgnoreCase) == true;
        State.Gps = new GpsInfo { On = AtParse.Int((await Q("AT+QGPS?")).Value("+QGPS:")) is { } g ? g == 1 : on };
        if (State.Gps.On == true) await ReadGpsAsync();
        return r.Ok || already ? (on ? "GPS 已打开" : "GPS 已关闭") : "操作失败：" + r.Error;
    });

    // —— 短信（文本模式 + UCS2，操作完还原 CMGF / CSCS / CSMP）——

    /// <summary>读取全部短信（并合并插件记下的本机发送记录）。读取会把未读短信标为已读。</summary>
    public Task LoadSmsAsync() => UserOp("读取短信", ReadSmsCoreAsync);

    private async Task<string> ReadSmsCoreAsync()
    {
        string? err = null;
        List<SmsMessage> fromModule = new();
        await WithSmsModeAsync(async () =>
        {
            var r = await Q("AT+CMGL=\"ALL\"", 20000);
            if (!r.Ok) { err = r.Error; return; }
            fromModule = AtParse.Cmgl(r.Lines);
        });
        if (err is null)
        {
            State.Sms.Messages = fromModule.Concat(await LoadSentAsync()).OrderBy(m => m.Time, StringComparer.Ordinal).ToList();
            State.Sms.Loaded = true;
            State.Sms.NewSinceLoad = 0;
        }
        var (mem, used, total) = AtParse.Cpms((await Q("AT+CPMS?")).Value("+CPMS:"));
        if (mem.Length > 0) { State.Sms.Storage = mem; State.Sms.Used = used; State.Sms.Total = total; }
        return err is null ? $"已读取 {fromModule.Count} 条短信" : "读取短信失败：" + err;
    }

    /// <summary>发送一条短信（UCS2 编码，单条最多 70 个字符）。成功后记进插件的发送记录（模块不保存经 AT+CMGS 发出的短信）。</summary>
    public Task SendSmsAsync(string to, string text) => UserOp("发送短信", async () =>
    {
        to = to.Trim().Replace(" ", "").Replace("-", "");
        if (to.Length is 0 or > 20 || !to.TrimStart('+').All(char.IsDigit)) return "号码格式不对";
        if (text.Length is 0 or > 70) return "内容为空或超过 70 个字符（暂不支持长短信）";
        string? err = null;
        await WithSmsModeAsync(async () =>
        {
            var csmp = (await Q("AT+CSMP?")).Value("+CSMP:");
            await Q("AT+CSMP=17,167,0,8");
            var r = await _at.SendWithPromptAsync($"AT+CMGS=\"{AtParse.Ucs2Encode(to)}\"", AtParse.Ucs2Encode(text));
            if (!r.Ok) err = r.Error;
            if (csmp is not null) await Q("AT+CSMP=" + csmp);
        });
        if (err is not null) return "发送失败：" + err;
        var sent = await LoadSentAsync();
        var msg = new SmsMessage
        {
            Index = Math.Min(-1, sent.Select(m => m.Index).DefaultIfEmpty(0).Min() - 1),
            Outgoing = true, Status = "已发送", Peer = to, Text = text,
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        sent.Add(msg);
        await SaveSentAsync(sent);
        State.Sms.Messages.Add(msg);
        return $"已发送到 {to}";
    });

    /// <summary>删除一条短信：正序号删模块存储里的，负序号删插件的发送记录。</summary>
    public Task DeleteSmsAsync(int index) => UserOp("删除短信", async () =>
    {
        if (index < 0)
        {
            var sent = await LoadSentAsync();
            sent.RemoveAll(m => m.Index == index);
            await SaveSentAsync(sent);
            State.Sms.Messages.RemoveAll(m => m.Index == index);
            return "已删除";
        }
        var r = await Q($"AT+CMGD={index}");
        if (r.Ok) State.Sms.Messages.RemoveAll(m => m.Index == index);
        var (mem, used, total) = AtParse.Cpms((await Q("AT+CPMS?")).Value("+CPMS:"));
        if (mem.Length > 0) { State.Sms.Used = used; State.Sms.Total = total; }
        return r.Ok ? "已删除" : "删除失败：" + r.Error;
    });

    // 本机发送记录：按 IMEI 存在插件存储里，只保存在这台电脑上。
    private string SentKey => "sentSms:" + (State.Module.Imei.Length > 0 ? State.Module.Imei : "unknown");
    private static readonly System.Text.Json.JsonSerializerOptions SentJson = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private async Task<List<SmsMessage>> LoadSentAsync()
    {
        var json = await _load(SentKey);
        if (string.IsNullOrEmpty(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<SmsMessage>>(json, SentJson) ?? new(); }
        catch { return new(); }
    }

    private Task SaveSentAsync(List<SmsMessage> sent) =>
        _store(SentKey, System.Text.Json.JsonSerializer.Serialize(sent.TakeLast(500).ToList(), SentJson));

    private async Task WithSmsModeAsync(Func<Task> body)
    {
        var cmgf = AtParse.Int((await Q("AT+CMGF?")).Value("+CMGF:"));
        var cscs = AtParse.Csv((await Q("AT+CSCS?")).Value("+CSCS:") ?? "").FirstOrDefault();
        await Q("AT+CMGF=1");
        await Q("AT+CSCS=\"UCS2\"");
        try { await body(); }
        finally
        {
            if (!string.IsNullOrEmpty(cscs)) await Q($"AT+CSCS=\"{cscs}\"");
            if (cmgf is not null) await Q($"AT+CMGF={cmgf}");
        }
    }

    // —— AT 控制台 ——

    /// <summary>执行一条用户输入的 AT 命令，收发写进控制台记录。</summary>
    public Task RunAtAsync(string command) => UserOp("执行 AT 命令", async () =>
    {
        command = command.Trim();
        if (!command.StartsWith("AT", StringComparison.OrdinalIgnoreCase)) return "命令要以 AT 开头";
        if (command.StartsWith("AT+CMGS", StringComparison.OrdinalIgnoreCase)) return "发短信请用短信面板";
        Log("> " + command);
        var r = await _at.SendAsync(command, 15000);
        foreach (var l in r.Lines) Log(l);
        Log(r.Ok ? "OK" : r.Error ?? "ERROR");
        // 用户可能改了设置：刷新一遍配置类信息。
        await ReadInfoAsync();
        return "";
    });

    public void ClearConsole() => State.Console.Clear();

    private void Log(string line)
    {
        State.Console.Add(line);
        if (State.Console.Count > ConsoleMax) State.Console.RemoveRange(0, State.Console.Count - ConsoleMax);
    }

    // —— 内部 ——

    private Task<AtResponse> Q(string cmd, int timeoutMs = 5000) => _at.SendAsync(cmd, timeoutMs);

    /// <summary>
    /// 设置类命令：模块刚改完网络设置、正在重新驻留时会短暂回 ERROR（实测锁小区后紧接着解锁），隔 1.5 秒重试，最多 4 次。
    /// </summary>
    private async Task<AtResponse> W(string cmd, int timeoutMs = 5000)
    {
        AtResponse r = AtResponse.Failed("");
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) await Task.Delay(1500);
            r = await _at.SendAsync(cmd, timeoutMs);
            if (r.Ok || _at.Fault is not null || r.Error is not "ERROR") break;
        }
        return r;
    }

    private async Task UserOp(string name, Func<Task<string>> op)
    {
        Interlocked.Increment(ref _userOps);
        State.Busy = true;
        await _push();
        try
        {
            var msg = await op();
            State.Status = _at.Fault is not null ? State.Status : msg;
        }
        catch (Exception ex) { State.Status = $"{name}失败：{ex.Message}"; }
        finally
        {
            Interlocked.Decrement(ref _userOps);
            State.Busy = false;
        }
        if (_at.Fault is null) await PollAsync();
        await _push();
    }

    private void OnUrc(string line)
    {
        if (line.StartsWith("+CMTI:", StringComparison.Ordinal))
        {
            State.Sms.NewSinceLoad++;
            if (State.Sms.Used is { } u) State.Sms.Used = u + 1;
            Notice?.Invoke(OsdKind.Info, "收到新短信");
            // 用户已经打开过收件箱：像聊天软件一样自动拉取新短信。不能在泵线程上等命令（会自己等自己），放到线程池。
            if (State.Sms.Loaded) _ = Task.Run(() => UserOp("接收短信", ReadSmsCoreAsync));
        }
        else if (line == "RING") Notice?.Invoke(OsdKind.Info, "来电");
        else if (line.StartsWith("+CPIN: NOT", StringComparison.Ordinal) || line.StartsWith("+QSIMSTAT: 1,0", StringComparison.Ordinal))
            Notice?.Invoke(OsdKind.Warning, "SIM 卡不可用");
        else return;
        _ = _push();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_poller is not null) { try { await _poller; } catch { } }
        await _at.DisposeAsync();
    }
}
