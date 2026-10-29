using DeviceHub.Contracts;

namespace DeviceHub.App.Services;

/// <summary>页顶提示条/详情里的一条诊断。</summary>
public sealed class UsbIssueRow
{
    public required UsbRow Row { get; init; }
    public required UsbIssue Issue { get; init; }
    public string Title => $"{Row.Title}：{Issue.Title}";
    public string Message => Issue.Detail;
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity => Issue.Level switch
    {
        UsbIssueLevel.Error => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
        UsbIssueLevel.Warning => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
        _ => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
    };
}

/// <summary>USB 页详情里的一行。</summary>
public sealed record UsbDetail(string Name, string Value);

/// <summary>USB 拓扑树的一个节点：控制器、端口上的设备（集线器继续展开）或空端口。</summary>
public sealed class UsbRow
{
    public string Glyph { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    /// <summary>右侧的速率标签，如"480 Mbps"。</summary>
    public string Badge { get; init; } = "";
    public bool Dim { get; init; }
    public bool IsExpanded { get; init; } = true;
    public List<UsbRow> Children { get; } = new();
    public List<UsbDetail> Details { get; } = new();
    public List<UsbIssue> Issues { get; init; } = new();
    /// <summary>"控制器 › 集线器 › 端口 3"，供详情显示位置。</summary>
    public string Path { get; init; } = "";

    public bool HasIssue => Issues.Any(i => i.Level != UsbIssueLevel.Info);
    public double TitleOpacity => Dim ? 0.55 : 1;
    public string IssueGlyph => Issues.Any(i => i.Level == UsbIssueLevel.Error) ? "" : HasIssue ? "" : "";

    /// <summary>TreeView 用它作为节点的无障碍名称。</summary>
    public override string ToString() => string.IsNullOrEmpty(Badge) ? Title : $"{Title}，{Badge}";

    /// <summary>复制到剪贴板的纯文本。</summary>
    public string ToText()
    {
        var lines = new List<string> { Title };
        lines.AddRange(Issues.Select(i => $"[{UsbFmt.Level(i.Level)}] {i.Title}：{i.Detail}"));
        lines.AddRange(Details.Select(d => $"{d.Name}：{d.Value}"));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>把 Agent 给的拓扑转成树节点；问题汇总给页顶的提示条。</summary>
public static class UsbTreeBuilder
{
    public static List<UsbRow> Build(UsbTopology topo, bool showEmptyPorts, List<(UsbRow Row, UsbIssue Issue)> issues)
    {
        var roots = new List<UsbRow>();
        foreach (var c in topo.Controllers)
        {
            var hub = c.RootHub;
            var row = new UsbRow
            {
                Glyph = "",
                Title = c.Name,
                Subtitle = c.Error ?? (hub is null ? "" : $"{(hub.IsUsb3 ? "USB 3" : "USB 2.0")} · {PhysicalPorts(hub).Count()} 个端口"),
                Path = c.Name,
            };
            row.Details.Add(new("类型", "USB 主控制器"));
            if (hub is not null)
            {
                row.Details.Add(new("根集线器", hub.IsUsb3 ? "USB 3（含 USB 2.0 伴随端口）" : "USB 2.0"));
                row.Details.Add(new("端口", $"{PhysicalPorts(hub).Count()} 个物理端口（系统端口 {hub.Ports.Count} 个）"));
            }
            if (c.InstanceId is { } id) row.Details.Add(new("实例 Id", id));
            if (c.Error is { } err) row.Details.Add(new("错误", err));
            if (hub is not null) AddPorts(row, hub, null, c.Name, showEmptyPorts, issues);
            roots.Add(row);
        }
        return roots;
    }

    /// <summary>界面上显示的端口：去掉已并入 USB 3 端口的 USB 2 伴随口。</summary>
    private static IEnumerable<UsbPortInfo> PhysicalPorts(UsbHubInfo hub) => hub.Ports.Where(p => !p.MergedIntoCompanion);

    private static void AddPorts(UsbRow parent, UsbHubInfo hub, UsbHubInfo? twin, string path, bool showEmpty,
        List<(UsbRow, UsbIssue)> issues)
    {
        var ports = PhysicalPorts(hub).ToList();
        // USB 3 集线器的 USB 2 另一半上没能并入的设备（伴随信息缺失时）
        if (twin is not null) ports.AddRange(PhysicalPorts(twin).Where(p => p.Device is not null));

        foreach (var p in ports)
        {
            var portPath = $"{path} › 端口 {p.Number}";
            if (p.Device is { } d)
            {
                var row = DeviceRow(d, p, portPath);
                foreach (var i in d.Issues) issues.Add((row, i));
                if (d.Hub is { } child) AddPorts(row, child, d.Usb2Twin, portPath, showEmpty, issues);
                parent.Children.Add(row);
            }
            else if (p.Status != UsbConnectionStatus.NoDevice)
            {
                var issue = new UsbIssue(UsbIssueLevel.Error, UsbFmt.Status(p.Status), "端口上有设备，但系统没能识别它。");
                var row = new UsbRow { Glyph = "", Title = $"端口 {p.Number}：{UsbFmt.Status(p.Status)}", Subtitle = PortSummary(p), Path = portPath, Issues = { issue } };
                AddPortDetails(row, p, portPath);
                issues.Add((row, issue));
                parent.Children.Add(row);
            }
            else if (showEmpty && p.IsUserConnectable)
            {
                var row = new UsbRow { Glyph = "", Title = $"端口 {p.Number}（空）", Subtitle = PortSummary(p), Dim = true, Path = portPath };
                AddPortDetails(row, p, portPath);
                parent.Children.Add(row);
            }
        }
    }

    private static UsbRow DeviceRow(UsbDeviceInfo d, UsbPortInfo p, string path)
    {
        var row = new UsbRow
        {
            Glyph = Glyph(d),
            Title = d.Name,
            Subtitle = $"端口 {p.Number} · {PortSummary(p)}",
            Badge = UsbFmt.Mbps(d.SpeedMbps),
            Issues = d.Issues,
            Path = path,
        };
        var x = row.Details;
        x.Add(new("当前速率", UsbFmt.Speed(d)));
        if (d.CapableMbps > d.SpeedMbps) x.Add(new("设备最高速率", UsbFmt.Mbps(d.CapableMbps)));
        x.Add(new("连接", $"端口 {p.Number} · {PortSummary(p)}"));
        x.Add(new("位置", path));
        if (d.Manufacturer is { } m) x.Add(new("厂商", m));
        if (d.Product is { } prod && prod != d.Name) x.Add(new("产品", prod));
        x.Add(new("VID:PID", $"{d.Vid:X4}:{d.Pid:X4}"));
        if (d.Serial is { } s) x.Add(new("序列号", s));
        x.Add(new("USB 版本", $"{UsbFmt.Bcd(d.BcdUsb)}（设备版本 {UsbFmt.Bcd(d.BcdDevice)}）"));
        x.Add(new("供电", $"{(d.SelfPowered ? "自供电" : "总线供电")}{(d.MaxPowerMa > 0 ? $" · 最大 {d.MaxPowerMa} mA" : "")}{(d.RemoteWakeup ? " · 支持远程唤醒" : "")}"));
        if (d.Hub is { } hub)
            x.Add(new("下游端口", $"{PhysicalPorts(hub).Count()} 个（{(hub.IsUsb3 ? "USB 3" : "USB 2.0")}{(hub.IsBusPowered == true ? "，总线供电" : "")}）"));
        if (d.InterfaceClasses.Count > 0) x.Add(new("接口类别", string.Join("、", d.InterfaceClasses)));
        if (d.Functions.Count > 0) x.Add(new("系统功能", string.Join("\n", d.Functions)));
        if (d.Capabilities.Count > 0) x.Add(new("设备能力", string.Join("\n", d.Capabilities)));
        if (d.AltModes.Count > 0) x.Add(new("备用模式", string.Join("\n", d.AltModes.Select(a => $"{a.Name}：{UsbFmt.AltMode(a.State)}"))));
        if (d.DriverDescription is { } drv) x.Add(new("驱动", d.ProblemCode == 0 ? drv : $"{drv}（问题代码 {d.ProblemCode}）"));
        if (d.InstanceId is { } id) x.Add(new("实例 Id", id));
        x.Add(new("地址", d.Address.ToString()));
        return row;
    }

    private static void AddPortDetails(UsbRow row, UsbPortInfo p, string path)
    {
        row.Details.Add(new("端口", PortSummary(p)));
        row.Details.Add(new("位置", path));
        if (p.CompanionPort is { } cp) row.Details.Add(new("伴随端口", $"端口 {cp}"));
        if (p.IsDebugCapable) row.Details.Add(new("调试", "支持 USB 调试"));
    }

    private static string PortSummary(UsbPortInfo p)
    {
        var parts = new List<string> { p.SupportsUsb3 ? "USB 3 接口" : "USB 2.0 接口" };
        if (p.IsTypeC) parts.Add("Type-C");
        if (!p.IsUserConnectable) parts.Add("内部");
        return string.Join(" · ", parts);
    }

    private static string Glyph(UsbDeviceInfo d)
    {
        if (d.IsHub) return "";
        var c = d.InterfaceClasses;
        if (c.Contains("HID 键盘")) return "";
        if (c.Contains("HID 鼠标")) return "";
        if (c.Contains("视频")) return "";
        if (c.Contains("音频")) return "";
        if (c.Any(x => x.StartsWith("大容量存储"))) return "";
        if (c.Contains("蓝牙")) return "";
        if (c.Contains("打印机")) return "";
        if (c.Contains("图像（PTP）")) return "";
        return "";
    }
}

public static class UsbFmt
{
    public static string Mbps(int mbps) => mbps switch
    {
        >= 1000 => $"{mbps / 1000.0:0.#} Gbps",
        1 => "1.5 Mbps",
        > 0 => $"{mbps} Mbps",
        _ => "",
    };

    public static string Speed(UsbDeviceInfo d)
    {
        var name = d.Speed switch
        {
            UsbSpeed.Low => "USB 1.1 低速",
            UsbSpeed.Full => "USB 1.1 全速",
            UsbSpeed.High => "USB 2.0 高速",
            UsbSpeed.Super => "USB 3.2 Gen 1",
            UsbSpeed.SuperPlus => d.Lanes == 2 ? "USB 3.2 Gen 1x2" : "USB 3.2 Gen 2",
            UsbSpeed.SuperPlus20 => "USB 3.2 Gen 2x2",
            _ => "未知",
        };
        return d.SpeedMbps > 0 ? $"{Mbps(d.SpeedMbps)}（{name}）" : name;
    }

    /// <summary>BCD 版本号：0x0210 → "2.1"，0x0102 → "1.02"。</summary>
    public static string Bcd(ushort v)
    {
        var s = $"{v >> 8:X}.{(v >> 4) & 0xF:X}";
        return (v & 0xF) == 0 ? s : s + (v & 0xF).ToString("X");
    }

    public static string Status(UsbConnectionStatus s) => s switch
    {
        UsbConnectionStatus.FailedEnumeration => "无法识别",
        UsbConnectionStatus.GeneralFailure => "设备故障",
        UsbConnectionStatus.Overcurrent => "过流保护",
        UsbConnectionStatus.NotEnoughPower => "供电不足",
        UsbConnectionStatus.NotEnoughBandwidth => "带宽不足",
        UsbConnectionStatus.HubNestedTooDeeply => "集线器层级过深",
        UsbConnectionStatus.Enumerating => "正在识别",
        UsbConnectionStatus.Reset => "正在重置",
        _ => s.ToString(),
    };

    public static string AltMode(UsbAltModeState s) => s switch
    {
        UsbAltModeState.Active => "已启用",
        UsbAltModeState.Failed => "协商失败",
        UsbAltModeState.NotAttempted => "未启用",
        _ => "未知",
    };

    public static string Level(UsbIssueLevel l) => l switch
    {
        UsbIssueLevel.Error => "错误",
        UsbIssueLevel.Warning => "注意",
        _ => "提示",
    };
}
