namespace DeviceHub.Plugin.GameSir.Ui;

// 与后端 GameSirState 对应的 UI 侧 DTO（camelCase）。
public sealed class DeadzoneDto { public int Begin { get; set; } public int End { get; set; } = 100; }

public sealed class GameSirStateDto
{
    public bool Authorized { get; set; }
    public bool Connected { get; set; }
    public string Status { get; set; } = "";
    public string ModelName { get; set; } = "GameSir";
    public bool Wireless { get; set; }
    public int? Battery { get; set; }
    public string Firmware { get; set; } = "";
    public string Mac { get; set; } = "";
    public int? VibrationLeft { get; set; }
    public int? VibrationRight { get; set; }
    public int? SleepMinutes { get; set; }
    public bool? AutoSleep { get; set; }
    public DeadzoneDto L2 { get; set; } = new();
    public DeadzoneDto R2 { get; set; } = new();
    public DeadzoneDto LeftStick { get; set; } = new();
    public DeadzoneDto RightStick { get; set; } = new();
    public bool CanWriteVibration { get; set; }
    public bool CanWriteSleep { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
}
