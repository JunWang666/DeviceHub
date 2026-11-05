using DeviceHub.Contracts;

namespace DeviceHub.Agent.Core;

/// <summary>
/// 根据扫描到的速率、能力与端口信息给出结论，类似 macOS 上 WhatCable 的"这根线/这个口限速了"提示。
/// Windows 不向用户态公开 Type-C 线缆 e-marker 与 PD 协商结果，所以线缆只能从"设备能力 vs 实际速率"间接判断。
/// </summary>
public static class UsbDiagnostics
{
    public static void Analyze(UsbDeviceInfo d, UsbPortInfo port, UsbHubInfo hub, IReadOnlyDictionary<string, UsbHubInfo> hubs)
    {
        var issues = d.Issues;
        switch (port.Status)
        {
            case UsbConnectionStatus.NotEnoughPower:
                issues.Add(new(UsbIssueLevel.Error, "供电不足", "端口给不了设备要求的电流。接到电脑本身的接口，或给集线器接上电源。")); break;
            case UsbConnectionStatus.Overcurrent:
                issues.Add(new(UsbIssueLevel.Error, "过流保护", "设备取电超过端口上限，端口已被关闭。拔掉设备后重新插入；反复出现可能是设备或线缆短路。")); break;
            case UsbConnectionStatus.FailedEnumeration:
            case UsbConnectionStatus.GeneralFailure:
                issues.Add(new(UsbIssueLevel.Error, "设备无法识别", "系统读不到设备描述符。换一根线或一个接口再试；仍不行可能是设备故障。")); break;
            case UsbConnectionStatus.NotEnoughBandwidth:
                issues.Add(new(UsbIssueLevel.Error, "带宽不足", "同一控制器上的设备（多为摄像头、音频）占满了带宽。把它换到另一个控制器的接口上。")); break;
            case UsbConnectionStatus.HubNestedTooDeeply:
                issues.Add(new(UsbIssueLevel.Error, "集线器层级过深", "USB 最多串接 5 级集线器。减少中间的集线器。")); break;
        }

        if (d.ProblemCode != 0)
            issues.Add(new(UsbIssueLevel.Error, $"驱动未正常工作（代码 {d.ProblemCode}）", "在设备管理器里查看此设备，可尝试重新安装驱动。"));

        var target = Fmt(d.CapableMbps);
        if (d.CapableMbps >= 5000 && d.SpeedMbps is > 0 and <= 480)
        {
            if (port.SupportsUsb3)
                issues.Add(new(UsbIssueLevel.Warning, "以 USB 2.0 速度运行",
                    $"设备和接口都支持 USB 3（{target}），但只协商到 480 Mbps。多半是线缆只有 USB 2.0 线芯（很多 Type-C 充电线就是这样），" +
                    "也可能是接口接触不良，或中间有 USB 2.0 的转接头、延长线。换一根数据线试试。"));
            else if (!hub.IsRoot)
                issues.Add(new(UsbIssueLevel.Warning, "受集线器限速",
                    $"设备支持 {target}，但接在 USB 2.0 集线器上，只能跑 480 Mbps。直接接到电脑的 USB 3 接口可以提速。"));
            else
                issues.Add(new(UsbIssueLevel.Warning, "接在 USB 2.0 接口上",
                    $"设备支持 {target}，这个接口只支持 USB 2.0。换到 USB 3 接口（常见为蓝色或标有 SS）可以提速。"));
        }
        else if (d.CapableMbps >= 10000 && d.SpeedMbps == 5000)
        {
            issues.Add(new(UsbIssueLevel.Info, "未达到设备最高速率",
                $"设备支持 {target}，当前 5 Gbps。接口或线缆只支持 USB 3.2 Gen 1（5 Gbps）。"));
        }
        else if (d.CapableMbps >= 480 && d.SpeedMbps == 12)
        {
            issues.Add(new(UsbIssueLevel.Warning, "以 USB 1.1 速度运行",
                "高速设备只协商到全速（12 Mbps），通常是接在了 USB 1.1 集线器或 KVM 切换器上。"));
        }

        foreach (var m in d.AltModes.Where(m => m.State == UsbAltModeState.Failed))
            issues.Add(new(UsbIssueLevel.Warning, $"{m.Name} 模式未能启用",
                "Type-C 设备进不了备用模式。可能是线缆不支持（需要全功能 Type-C 线），或电脑这个接口不支持视频/雷电输出。"));
        if (d.DeviceClass == 0x11 || d.InterfaceClasses.Contains("Billboard") && d.AltModes.Count == 0)
            issues.Add(new(UsbIssueLevel.Info, "Billboard 设备", "这是 Type-C 设备在备用模式未启用时报告状态用的虚拟设备。"));
    }

    public static string Fmt(int mbps) => mbps switch
    {
        >= 1000 => $"{mbps / 1000.0:0.#} Gbps",
        1 => "1.5 Mbps",
        > 0 => $"{mbps} Mbps",
        _ => "未知",
    };
}

/// <summary>USB 类别码、SVID、平台能力的中文名称。</summary>
public static class UsbNames
{
    public static string InterfaceClass(byte cls, byte sub, byte proto) => cls switch
    {
        0x01 => "音频",
        0x02 => "通信（CDC）",
        0x03 => proto switch { 1 when sub == 1 => "HID 键盘", 2 when sub == 1 => "HID 鼠标", _ => "HID" },
        0x05 => "物理设备",
        0x06 => "图像（PTP）",
        0x07 => "打印机",
        0x08 => sub == 0x06 && proto == 0x62 ? "大容量存储（UAS）" : "大容量存储",
        0x09 => "集线器",
        0x0A => "CDC 数据",
        0x0B => "智能卡",
        0x0D => "内容安全",
        0x0E => "视频",
        0x0F => "医疗设备",
        0x10 => "音视频",
        0x11 => "Billboard",
        0x12 => "Type-C 桥接",
        0xDC => "诊断",
        0xE0 => sub == 1 && proto == 1 ? "蓝牙" : "无线控制器",
        0xEF => "杂项",
        0xFE => sub == 1 ? "固件更新（DFU）" : "应用专用",
        0xFF => "厂商自定义",
        _ => $"类别 0x{cls:X2}",
    };

    public static string Svid(ushort svid) => svid switch
    {
        0xFF01 => "DisplayPort",
        0x8087 => "Thunderbolt",
        0x05AC => "Apple",
        _ => $"SVID {svid:X4}",
    };

    private static readonly Guid WebUsb = new("3408b638-09a9-47a0-8bfd-a0768815b665");
    private static readonly Guid MsOs20 = new("d8dd60df-4589-4cc7-9cd2-659d9e648a9f");

    public static string Platform(ReadOnlySpan<byte> cap)
    {
        if (cap.Length < 20) return "平台能力";
        var id = new Guid(cap.Slice(4, 16));
        return id == WebUsb ? "WebUSB" : id == MsOs20 ? "MS OS 2.0 描述符" : $"平台能力 {id}";
    }
}
