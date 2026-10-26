using System.Text.Json.Serialization;

namespace DeviceHub.Plugin.Gk65.Ui;

// 与后端 Gk65DeviceState 对应的 UI 侧 DTO（camelCase）。
public sealed class PanelDto { public int Mode { get; set; } public int Speed { get; set; } public int Brightness { get; set; } = 5; public int Direction { get; set; } public int R { get; set; } public int G { get; set; } = 128; public int B { get; set; } = 255; }
public sealed class SideDto { public int Mode { get; set; } public int Brightness { get; set; } = 5; }
public sealed class ProfileDto { public PanelDto Panel { get; set; } = new(); public SideDto Side { get; set; } = new(); }

public sealed class Gk65StateDto
{
    public int CurrentProfile { get; set; }
    public int Dpi { get; set; } = 800;
    public ProfileDto[] Profiles { get; set; } = { new(), new(), new() };
    public bool Authorized { get; set; }
    public string Status { get; set; } = "";
    public string Firmware { get; set; } = "(未知)";
    /// <summary>0 = DeviceHub 上次保存，1 = 官方驱动导入，2 = 默认值（对应后端 StateSource）。</summary>
    public int Source { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    [JsonIgnore] public ProfileDto Current => Profiles[Math.Clamp(CurrentProfile, 0, Profiles.Length - 1)];
}
