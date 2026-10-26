namespace DeviceHub.Plugin.Gk65;

/// <summary>主灯设置（对应驱动 ledeffect.xml 的 item：速度/亮度/方向/区域 + 颜色）。</summary>
public sealed class LightSettings
{
    public int Mode { get; set; }          // 0..19，见 ledModes
    public int Speed { get; set; }         // 0..4
    public int Brightness { get; set; } = 5; // 0..5
    public int Direction { get; set; }     // 0=左 1=右
    public int R { get; set; }
    public int G { get; set; } = 128;
    public int B { get; set; } = 255;
}

/// <summary>侧灯设置（sidemodel.xml）。</summary>
public sealed class SideSettings
{
    public int Mode { get; set; }
    public int Brightness { get; set; } = 5;
}

/// <summary>单套配置（P1/P2/P3 各自独立）。</summary>
public sealed class ProfileSettings
{
    public LightSettings Panel { get; set; } = new();
    public SideSettings Side { get; set; } = new();
}

/// <summary>设备整体状态，序列化后给 UI 绑定。</summary>
public sealed class Gk65DeviceState
{
    public int CurrentProfile { get; set; }
    public int Dpi { get; set; } = 800;
    public ProfileSettings[] Profiles { get; set; } = { new(), new(), new() };
    public bool Authorized { get; set; }
    public string Status { get; set; } = "未接管";
    public string Firmware { get; set; } = "(未知)";
    /// <summary>当前设置从哪来（键盘不支持回读，只能靠记忆）。</summary>
    public StateSource Source { get; set; } = StateSource.Saved;
    /// <summary>最近一次成功写入键盘的时间。</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    public ProfileSettings Current => Profiles[Math.Clamp(CurrentProfile, 0, Profiles.Length - 1)];
}
