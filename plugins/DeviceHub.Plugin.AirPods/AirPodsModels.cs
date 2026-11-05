namespace DeviceHub.Plugin.AirPods;

/// <summary>
/// 型号表：Apple 型号 Id（蓝牙 Device ID 的 ProductId，也在接近配对广播 offset 3 以小端出现）→ 名称与能力。
/// Id 来自 AirPodsDesktop（AppleCP.cpp）与 PodBridge（docs/research/model-ids.md）；
/// 降噪/自适应/对话感知按 Apple 公开的机型功能表。
/// </summary>
public sealed record AirPodsModel(ushort Id, string Name, bool HeadWorn = false, bool Anc = false, bool Adaptive = false, bool ConversationAwareness = false)
{
    public bool HasCase => !HeadWorn;

    public static readonly AirPodsModel Unknown = new(0, "AirPods");

    private static readonly AirPodsModel[] All =
    {
        new(0x2002, "AirPods（第 1 代）"),
        new(0x200F, "AirPods（第 2 代）"),
        new(0x2013, "AirPods（第 3 代）"),
        new(0x2019, "AirPods 4"),
        new(0x201B, "AirPods 4（主动降噪款）", Anc: true, Adaptive: true, ConversationAwareness: true),
        // 0x2030：用户真机（AirPods 5，无线充电盒款）的蓝牙 Device ID ProductId，2026-11 实测。
        // AirPods 5 全系降噪；自适应 / 对话感知按 AirPods 4 降噪款推定，未经 AAP 实测。其他充电盒款若 Id 不同需另加。
        new(0x2030, "AirPods 5", Anc: true, Adaptive: true, ConversationAwareness: true),
        new(0x200E, "AirPods Pro", Anc: true),
        new(0x2014, "AirPods Pro 2", Anc: true, Adaptive: true, ConversationAwareness: true),
        new(0x2024, "AirPods Pro 2（USB-C）", Anc: true, Adaptive: true, ConversationAwareness: true),
        new(0x2027, "AirPods Pro 3", Anc: true, Adaptive: true, ConversationAwareness: true),
        new(0x200A, "AirPods Max", HeadWorn: true, Anc: true),
        new(0x201F, "AirPods Max（USB-C）", HeadWorn: true, Anc: true),
        new(0x2012, "Beats Fit Pro", Anc: true),
    };

    public static AirPodsModel Find(ushort id) => All.FirstOrDefault(m => m.Id == id) ?? (id == 0 ? Unknown : Unknown with { Id = id });
}
