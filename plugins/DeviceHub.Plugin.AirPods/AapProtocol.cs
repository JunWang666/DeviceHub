namespace DeviceHub.Plugin.AirPods;

/// <summary>聆听模式（AAP 控制命令 0x0D 的取值）。</summary>
public enum ListeningMode : byte { Off = 1, NoiseCancellation = 2, Transparency = 3, Adaptive = 4 }

/// <summary>
/// AAP（Apple Accessory Protocol，L2CAP PSM 0x1001）帧。来源：ntpods docs/control_commands.md 与
/// windows/daemon/src/aap.rs、PodBridge src/PodBridge.Core/Protocol/AapProtocol.cs，两者独立实现、字节一致。
/// 握手之后的数据帧都以 <c>04 00 04 00</c> 开头，接 2 字节小端操作码。
/// </summary>
public static class AapProtocol
{
    private static readonly byte[] Header = { 0x04, 0x00, 0x04, 0x00 };

    /// <summary>连接后第一帧；不发的话 AirPods 忽略之后所有帧。</summary>
    public static byte[] Handshake() => new byte[] { 0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>解锁按“Apple 设备”放开的功能（不发时设自适应会被改回别的模式）。</summary>
    public static byte[] SetSpecificFeatures() => Frame(0x4D, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);

    /// <summary>订阅推送：电量、入耳、控制命令回显等。</summary>
    public static byte[] RequestNotifications() => Frame(0x0F, 0xFF, 0xFF, 0xFF, 0xFF);

    // —— 控制命令：04 00 04 00 09 00 [id] [v1] [v2] 00 00 ——
    public const byte ControlListeningMode = 0x0D;
    /// <summary>对话感知：1 = 开，2 = 关。</summary>
    public const byte ControlConversationAwareness = 0x28;
    /// <summary>自动入耳检测：1 = 开，2 = 关。</summary>
    public const byte ControlEarDetection = 0x0A;

    public static byte[] Control(byte id, byte value, byte value2 = 0) => Frame(0x09, id, value, value2, 0x00, 0x00);
    public static byte[] SetListeningMode(ListeningMode m) => Control(ControlListeningMode, (byte)m);
    public static byte[] SetToggle(byte id, bool on) => Control(id, on ? (byte)1 : (byte)2);

    /// <summary>操作码 0x0009 的回显 / 推送：(设置 id, 值)。</summary>
    public static (byte Id, byte Value)? ParseControl(ReadOnlySpan<byte> f) =>
        IsData(f, 0x09) && f.Length >= 8 ? (f[6], f[7]) : null;

    /// <summary>
    /// 电量推送（操作码 0x04）：<c>04 00 04 00 04 00 [n] { [部件][01][电量][状态][01] }×n</c>。
    /// 部件 0x02 右、0x04 左、0x08 充电盒、0x01 头戴整机；状态 0x01 充电中、0x04 不在位；电量 0xFF 未知。
    /// </summary>
    public static AapBattery? ParseBattery(ReadOnlySpan<byte> f)
    {
        if (!IsData(f, 0x04) || f.Length < 7) return null;
        var b = new AapBattery();
        int count = f[6];
        for (int i = 0; i < count; i++)
        {
            int at = 7 + i * 5;
            if (at + 3 >= f.Length) break;
            byte part = f[at], level = f[at + 2], status = f[at + 3];
            int? v = status == 0x04 || level == 0xFF ? null : Math.Min((int)level, 100);
            bool charging = status is 0x01 or 0x05;
            switch (part)
            {
                case 0x01: b.Headphone = v; b.HeadphoneCharging = charging; break;
                case 0x02: b.Right = v; b.RightCharging = charging; break;
                case 0x04: b.Left = v; b.LeftCharging = charging; break;
                case 0x08: b.Case = v; b.CaseCharging = charging; break;
            }
        }
        return b;
    }

    /// <summary>入耳推送（操作码 0x06）：主耳、副耳各一字节，0 入耳、1 出耳、2 在盒内。</summary>
    public static (byte Primary, byte Secondary)? ParseEarDetection(ReadOnlySpan<byte> f) =>
        IsData(f, 0x06) && f.Length >= 8 ? (f[6], f[7]) : null;

    /// <summary>设备信息推送（操作码 0x1D）：以 NUL 分隔的字符串，依次为 名称、型号、厂商、序列号、固件……</summary>
    public static (string Model, string Firmware)? ParseMetadata(ReadOnlySpan<byte> f)
    {
        if (!IsData(f, 0x1D) || f.Length < 8) return null;
        var strings = new List<string>();
        foreach (var part in f[6..].ToArray().Split0())
            if (part.Length >= 3 && part.All(c => c is >= 0x20 and < 0x7F)) strings.Add(System.Text.Encoding.ASCII.GetString(part));
        var model = strings.ElementAtOrDefault(1) ?? "";
        var fw = strings.ElementAtOrDefault(4) ?? "";
        return model.Length == 0 && fw.Length == 0 ? null : (model, fw);
    }

    private static bool IsData(ReadOnlySpan<byte> f, byte opcode) =>
        f.Length >= 6 && f[..4].SequenceEqual(Header) && f[4] == opcode && f[5] == 0x00;

    private static byte[] Frame(byte opcode, params byte[] body)
    {
        var f = new byte[6 + body.Length];
        Header.CopyTo(f, 0);
        f[4] = opcode;
        body.CopyTo(f, 6);
        return f;
    }

    private static IEnumerable<byte[]> Split0(this byte[] data)
    {
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && data[i] != 0) continue;
            if (i > start) yield return data[start..i];
            start = i + 1;
        }
    }
}

public sealed class AapBattery
{
    public int? Left, Right, Case, Headphone;
    public bool LeftCharging, RightCharging, CaseCharging, HeadphoneCharging;
}
