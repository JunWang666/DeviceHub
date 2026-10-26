namespace DeviceHub.Plugin.AirPods.Ui;

// 与后端 AirPodsState 对应的 UI 侧 DTO（camelCase）。
public sealed class PartDto { public int? Battery { get; set; } public bool Charging { get; set; } public bool InEar { get; set; } }

public sealed class AirPodsStateDto
{
    public bool Authorized { get; set; }
    public bool Connected { get; set; }
    public string Status { get; set; } = "";
    public int ModelId { get; set; }
    public string ModelName { get; set; } = "AirPods";
    public bool HeadWorn { get; set; }
    public PartDto Left { get; set; } = new();
    public PartDto Right { get; set; } = new();
    public PartDto Case { get; set; } = new();
    public bool BothInCase { get; set; }
    public bool? LidOpen { get; set; }
    public string BatterySource { get; set; } = "";
    public DateTimeOffset? LastSeen { get; set; }
    public int? Rssi { get; set; }
    public bool AapConnected { get; set; }
    public string AapNote { get; set; } = "";
    public bool SupportsAnc { get; set; }
    public bool SupportsAdaptive { get; set; }
    public bool SupportsConversationAwareness { get; set; }
    /// <summary>1 关闭、2 降噪、3 通透、4 自适应；null 未知。</summary>
    public int? ListeningMode { get; set; }
    public bool? ConversationAwareness { get; set; }
    public string Firmware { get; set; } = "";
}
