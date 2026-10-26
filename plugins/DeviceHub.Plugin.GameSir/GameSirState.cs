namespace DeviceHub.Plugin.GameSir;

/// <summary>一侧扳机或摇杆的死区（%）。</summary>
public sealed class DeadzoneState
{
    public int Begin { get; set; }
    public int End { get; set; } = 100;
}

/// <summary>推给 UI 的完整状态（camelCase JSON，与 UI 侧 GameSirStateDto 对应）。</summary>
public sealed class GameSirState
{
    public bool Authorized { get; set; }
    public bool Connected { get; set; }
    public string Status { get; set; } = "";

    public string ModelName { get; set; } = "GameSir Nova 2 Lite";
    public bool Wireless { get; set; } = true;

    public int? Battery { get; set; }
    public string Firmware { get; set; } = "";
    public string Mac { get; set; } = "";

    /// <summary>0–4 档；null = 未读到。</summary>
    public int? VibrationLeft { get; set; }
    public int? VibrationRight { get; set; }

    public int? SleepMinutes { get; set; }
    public bool? AutoSleep { get; set; }

    public DeadzoneState L2 { get; set; } = new();
    public DeadzoneState R2 { get; set; } = new();
    public DeadzoneState LeftStick { get; set; } = new();
    public DeadzoneState RightStick { get; set; } = new();

    /// <summary>写入是否可用（对应写帧已从官方软件抓到核实）。</summary>
    public bool CanWriteVibration { get; set; }
    public bool CanWriteSleep { get; set; }

    public DateTimeOffset? LastReadAt { get; set; }
}
