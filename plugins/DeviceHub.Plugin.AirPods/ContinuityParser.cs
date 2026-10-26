namespace DeviceHub.Plugin.AirPods;

/// <summary>一条接近配对广播解出的状态。"本耳"指发广播的那只耳机。电量 0–100，null = 未知。</summary>
public sealed record ProximityStatus
{
    public ushort ModelId { get; init; }
    /// <summary>发这条广播的是左耳（否则右耳）。</summary>
    public bool FromLeft { get; init; }
    public int? Left { get; init; }
    public int? Right { get; init; }
    public int? Case { get; init; }
    public bool LeftCharging { get; init; }
    public bool RightCharging { get; init; }
    public bool CaseCharging { get; init; }
    public bool LeftInEar { get; init; }
    public bool RightInEar { get; init; }
    public bool BothInCase { get; init; }
    public bool? LidOpen { get; init; }
}

/// <summary>
/// Apple Continuity 接近配对包（类型 0x07，27 字节，不含公司 Id）解析。布局见 AirPodsDesktop AppleCP.h：
/// <code>
/// 0 类型 0x07 | 1 剩余长度 0x19 | 2 ? | 3-4 型号（小端）
/// 5 状态：bit1/bit3 入耳（见下）、bit2 两耳都在盒内、bit5 本耳是左耳、bit6 本耳在盒内
/// 6 电量：低 4 位本耳、高 4 位另一耳（0–10 → ×10%，其余 = 未知）
/// 7 低 4 位充电盒电量；bit4 本耳充电、bit5 另一耳充电、bit6 充电盒充电
/// 8 bit3 盖子关闭 | 9 颜色 | 11-26 加密段（核心已清零）
/// </code>
/// 广播里没有左右之分，只有“本耳 / 另一耳”，所以要按 bit5 换算成左右。
/// </summary>
public static class ContinuityParser
{
    public const int Length = 27;

    public static ProximityStatus? TryParse(ReadOnlySpan<byte> d)
    {
        if (d.Length != Length || d[0] != 0x07 || d[1] != Length - 2) return null;
        byte status = d[5], battery = d[6], charge = d[7], lid = d[8];
        bool fromLeft = (status & 0x20) != 0;
        int? self = Battery(battery & 0x0F), other = Battery(battery >> 4);
        bool selfCharging = (charge & 0x10) != 0, otherCharging = (charge & 0x20) != 0;
        bool leftCharging = fromLeft ? selfCharging : otherCharging;
        bool rightCharging = fromLeft ? otherCharging : selfCharging;

        // 入耳位不是简单的“本耳/另一耳”：还要与 bit6（本耳在盒内）异或后再选 bit1/bit3。
        // AirPodsDesktop 没做这一步；PodBridge 汇总多个实现后采用了异或（多数派），这里跟随 PodBridge。
        bool xor = fromLeft ^ ((status & 0x40) != 0);
        bool bit1 = (status & 0x02) != 0, bit3 = (status & 0x08) != 0;
        return new ProximityStatus
        {
            ModelId = (ushort)(d[3] | d[4] << 8),
            FromLeft = fromLeft,
            Left = fromLeft ? self : other,
            Right = fromLeft ? other : self,
            Case = Battery(charge & 0x0F),
            LeftCharging = leftCharging,
            RightCharging = rightCharging,
            CaseCharging = (charge & 0x40) != 0,
            // 充电中的耳机有时也会置入耳位，按 AirPodsDesktop 的做法过滤掉。
            LeftInEar = !leftCharging && (xor ? bit3 : bit1),
            RightInEar = !rightCharging && (xor ? bit1 : bit3),
            BothInCase = (status & 0x04) != 0,
            // 盖子位只在耳机在盒内时有意义（PodBridge 的结论），否则未知。
            LidOpen = (status & 0x44) != 0 ? (lid & 0x08) == 0 : null,
        };
    }

    private static int? Battery(int nibble) => nibble <= 10 ? nibble * 10 : null;
}
