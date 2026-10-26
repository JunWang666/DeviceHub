namespace DeviceHub.Plugin.AirPods;

/// <summary>一个部件（左耳 / 右耳 / 充电盒）的状态。</summary>
public sealed class PartState
{
    /// <summary>0–100；null = 未知（不在位、没收到广播）。</summary>
    public int? Battery { get; set; }
    public bool Charging { get; set; }
    public bool InEar { get; set; }
}

/// <summary>推给 UI 的完整状态（camelCase JSON，与 UI 侧 AirPodsStateDto 对应）。</summary>
public sealed class AirPodsState
{
    public bool Authorized { get; set; }
    public bool Connected { get; set; }
    public string Status { get; set; } = "";

    public ushort ModelId { get; set; }
    public string ModelName { get; set; } = "AirPods";
    public bool HeadWorn { get; set; }

    public PartState Left { get; set; } = new();
    public PartState Right { get; set; } = new();
    public PartState Case { get; set; } = new();
    public bool BothInCase { get; set; }
    public bool? LidOpen { get; set; }
    /// <summary>电量来源："aap"（1% 精度）、"ble"（10% 精度）、""（还没有）。</summary>
    public string BatterySource { get; set; } = "";
    public DateTimeOffset? LastSeen { get; set; }
    public short? Rssi { get; set; }

    // —— 高级功能（需 AAP 驱动）——
    public bool AapConnected { get; set; }
    public string AapNote { get; set; } = "";
    public bool SupportsAnc { get; set; }
    public bool SupportsAdaptive { get; set; }
    public bool SupportsConversationAwareness { get; set; }
    /// <summary><see cref="AirPods.ListeningMode"/> 的值；null = 未知。</summary>
    public int? ListeningMode { get; set; }
    public bool? ConversationAwareness { get; set; }
    public string Firmware { get; set; } = "";

    public void ApplyModel(AirPodsModel m)
    {
        ModelId = m.Id; ModelName = m.Name; HeadWorn = m.HeadWorn;
        SupportsAnc = m.Anc; SupportsAdaptive = m.Adaptive; SupportsConversationAwareness = m.ConversationAwareness;
    }
}
