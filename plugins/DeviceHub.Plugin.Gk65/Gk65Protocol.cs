namespace DeviceHub.Plugin.Gk65;

/// <summary>
/// GK65（2.4G 接收器 VID_1A2C PID_7FFF）配置通道编码器。
///
/// 通道：mi_02，UsagePage 0xFF01 / Usage 0x01，无 report id；Output/Input 均为 65 字节
/// （首字节 report id = 0x00 + 64 字节负载）。设备不回包，也没有已知的读命令——
/// 官方软件同样不回读（固件版本显示 v0.0），状态只保存在本地。
///
/// 帧格式（2026-10-26 在 ShinetekTools 的 WriteFile 上断点实抓）：
///   [0]    0x00          report id
///   [1..4] BB AA 99 88   固定帧头
///   [5]    命令           0xAA = 面板灯效
///   [6..]  参数
///   其余补 0，无校验字节。
///
/// 已核实（Verified=true）：面板灯效 AA &lt;模式&gt; &lt;亮度 0-5&gt; &lt;速度 0-4&gt;。
/// 实抓样本：Steady 00 05 00 / Breathing 02 05 04 / Neon_stream 04 05 04 /
/// Streamer 05 05 04、05 05 02 / 亮度 00 04 00。
/// 其余命令（侧灯/恢复出厂）尚未抓到，保持 Verified=false，插件不会下发。
/// 改键与宏不是设备功能（官方软件在电脑端实现），不在此列。
/// </summary>
public static class Gk65Protocol
{
    public const byte ReportId = 0x00;
    public const int FrameLen = 65;
    public static readonly byte[] Header = { 0xBB, 0xAA, 0x99, 0x88 };

    public const byte CmdPanelLighting = 0xAA;

    public sealed record Frame(byte[] Data, bool Verified, string Note);

    private static byte[] New(params byte[] body)
    {
        var f = new byte[FrameLen];
        f[0] = ReportId;
        Header.CopyTo(f, 1);
        Array.Copy(body, 0, f, 1 + Header.Length, body.Length);
        return f;
    }

    /// <summary>
    /// 主灯。模式码即官方下拉框的 data 值（0..19；1 = Gaming Special Key 在官方界面被注释掉，未核实）。
    /// 方向与 RGB 在官方抓包里未出现对应字节，不编码。
    /// </summary>
    public static Frame PanelLighting(LightSettings p)
    {
        int mode = Math.Clamp(p.Mode, 0, 19);
        var f = New(CmdPanelLighting, (byte)mode, (byte)Math.Clamp(p.Brightness, 0, 5), (byte)Math.Clamp(p.Speed, 0, 4));
        return mode == 1 ? new(f, false, "主灯效(模式 1 未核实)") : new(f, true, "主灯效");
    }

    // —— 以下命令尚未从官方软件抓到，字节布局未知；Verified=false 的帧不会被下发 ——

    public static Frame SwitchProfile(int profile) => new(New(), false, "切换配置(未抓到协议)");

    public static Frame SetDpi(int dpi) => new(New(), false, "设置DPI(键盘无此功能)");

    public static Frame SideLighting(SideSettings s) => new(New(), false, "侧灯(未抓到协议)");

    public static Frame RestoreFactory() => new(New(), false, "恢复出厂(未抓到协议)");

    // 改键与宏没有设备协议：官方软件只写本地 record.xml，在电脑端转换按键（2026-10-26 实测），故不实现。
}
