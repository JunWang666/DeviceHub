namespace DeviceHub.Plugin.GameSir;

/// <summary>
/// GameSir Nova 2 Lite（T4N2，2.4G 接收器 VID_3537 PID_1098；有线 PID_100F）配置通道编解码。
///
/// 来源：GameSir Connect 1.16.5 是 Electron 应用，协议在 sapjoyuniapis_t4n2_x86_*.dll 里，
/// 该 DLL 把每一帧 hid_write / hid_read 都记进 %APPDATA%\gamesir_connect\logs\zw-lib.log，
/// 下面的帧全部取自这份日志（2026-10-26 真机），不是猜的。写帧是用一个 x86 小程序调用同一个 DLL 的
/// Set* 接口（先读出、原样写回，再降一档并恢复）让它自己组帧，从日志里取出来的。
///
/// 通道：mi_02，UsagePage 0xFF7A / Usage 0x01，Output 65 字节（report id 0x00 + 64 字节负载），Input 64 字节。
/// 一问一答；但设备会不停推送首字节 0xF4 的状态包，官方 DLL 发命令前先清空、收包时跳过 F4。
///
/// 帧格式：[命令] [子命令] [帧长（含校验）] [参数…] [校验]，校验 = 前面所有字节求和 &amp; 0xFF。
/// 回包把同样的命令/子命令/帧长回显，参数位置换成设备的值（读命令的回包校验不重算，不做校验）。
/// 唯一例外是读设备信息 01 01 00：回包不回显命令。
/// </summary>
public static class GameSirProtocol
{
    public const int OutputLen = 65;
    public const byte StatePush = 0xF4;

    public sealed record Frame(byte[] Data, bool Verified, string Note, Func<byte[], bool> IsReply);

    private static byte[] Out(params byte[] body)
    {
        var f = new byte[OutputLen];
        body.CopyTo(f, 1);
        return f;
    }

    /// <summary>带校验的标准帧：body = 命令、子命令、参数（不含帧长与校验）。</summary>
    private static Frame Std(bool verified, string note, byte cmd, byte sub, params byte[] args)
    {
        var body = new byte[args.Length + 4];
        body[0] = cmd; body[1] = sub; body[2] = (byte)body.Length;
        args.CopyTo(body, 3);
        body[^1] = Sum(body.AsSpan(0, body.Length - 1));
        return new Frame(Out(body), verified, note, r => r.Length > 2 && r[0] == cmd && r[1] == sub);
    }

    public static byte Sum(ReadOnlySpan<byte> b)
    {
        int s = 0;
        foreach (var x in b) s += x;
        return (byte)s;
    }

    // —— 读命令（全部 Verified：官方日志里原样出现过，且回包已对上官方软件显示的值）——

    /// <summary>01 01 00 → MAC[0..5]、固件 [10..11]、电量 [18]。</summary>
    public static Frame ReadDeviceInfo() =>
        new(Out(0x01, 0x01, 0x00), true, "设备信息", r => r.Length >= 20 && r[0] != StatePush);

    /// <summary>25 01 0D … → [4] 左马达、[8] 右马达，取值 0/25/50/75/100（官方 UI 的 0–4 档）。</summary>
    public static Frame ReadVibration() => Std(true, "震动强度", 0x25, 0x01, new byte[9]);

    /// <summary>F5 00 06 → [3] 休眠分钟、[4] 智能休眠开关。</summary>
    public static Frame ReadSleep() => Std(true, "自动休眠", 0xF5, 0x00, 0x00, 0x00);

    /// <summary>07 &lt;参数&gt; 06 → [4] 值。参数：05 L2 起始、07 L2 结束、06 R2 起始、08 R2 结束（单位 %）。</summary>
    public static Frame ReadTriggerParam(byte param) => Std(true, "扳机死区", 0x07, param, 0x00, 0x00);

    public const byte L2Begin = 0x05, R2Begin = 0x06, L2End = 0x07, R2End = 0x08;

    /// <summary>
    /// 摇杆曲线分两步：先 23 &lt;10+摇杆&gt; 06 00 00 选中，再 F0 00 11 &lt;摇杆+1&gt; 读。
    /// 回包 [4] 起始死区、[5] 起始反死区、[6..13] 曲线点（x,y ×4），[14][15] 末端参数。
    /// </summary>
    public static Frame SelectStick(int stick) => Std(true, "选择摇杆", 0x23, (byte)(0x10 + stick), 0x00, 0x00);
    public static Frame ReadStickCurve(int stick)
    {
        var args = new byte[13];
        args[0] = (byte)(stick + 1);
        var f = Std(true, "摇杆曲线", 0xF0, 0x00, args);
        return f with { IsReply = r => r.Length > 3 && r[0] == 0xF0 && r[3] == stick + 1 };
    }

    // —— 写命令（2026-10-26 用官方 DLL 的 Set* 接口实发并从日志核实；设备回显后再读回确认）——

    /// <summary>
    /// 25 01 0D 01 &lt;左&gt; 03 00 00 &lt;右&gt; 03 00 00：[3]=01 表示写；03 是固定值（75 和 50 两档实抓都是 03）。
    /// 实抓：4B/4B → …D0，32/32 → …9E。
    /// </summary>
    public static Frame WriteVibration(int left, int right) =>
        Std(true, "震动强度", 0x25, 0x01, 0x01, LevelToValue(left), 0x03, 0, 0, LevelToValue(right), 0x03, 0, 0);

    /// <summary>F5 01 06 &lt;分钟&gt; &lt;智能休眠&gt;：读帧的子命令 00 换成 01。实抓 F5 01 06 0A 01 07。</summary>
    public static Frame WriteSleep(int minutes, bool auto) =>
        Std(true, "自动休眠", 0xF5, 0x01, (byte)minutes, (byte)(auto ? 1 : 0));

    // —— 解码 ——

    /// <summary>官方 UI 的 0–4 档 ↔ 设备值 0/25/50/75/100（GameSir Connect 的 ge/me 函数）。</summary>
    public static byte LevelToValue(int level) => (byte)(Math.Clamp(level, 0, 4) * 25);
    public static int ValueToLevel(int v) => v >= 100 ? 4 : v >= 75 ? 3 : v >= 50 ? 2 : v >= 25 ? 1 : 0;

    public static (string Mac, string Firmware, int Battery) ParseDeviceInfo(byte[] r) =>
        (Convert.ToHexString(r, 0, 6), $"{r[10]:X2}{r[11]:X2}", r[18]);
}
