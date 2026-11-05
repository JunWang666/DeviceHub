using System.Net.NetworkInformation;
using System.Net.Sockets;
using DeviceHub.Contracts;

namespace DeviceHub.Plugin.Quectel;

/// <summary>
/// 模块在 Windows 上的 USB 接口组成，取自核心给的通道列表（serial / net / nodriver）。
/// EG25-G：接口 0 DM、1 NMEA、2 AT、3 Modem、4 网卡。
/// </summary>
public sealed record ModuleLayout(string? AtPort, string? NetGuid, string? NetMissing, bool HasNetInterface, IReadOnlyList<string> Missing)
{
    public const int AtInterface = 2;
    public const int NetInterface = 4;

    public static ModuleLayout From(IReadOnlyList<HidChannelInfo> channels)
    {
        var at = channels.FirstOrDefault(c => c.Kind == ChannelKinds.Serial && c.Interface == AtInterface);
        var net = channels.FirstOrDefault(c => c.Kind == ChannelKinds.Net);
        var netMissing = channels.FirstOrDefault(c => c.Kind == ChannelKinds.NoDriver && c.Interface == NetInterface);
        var missing = channels.Where(c => c.Kind == ChannelKinds.NoDriver)
            .Select(c => $"接口 {c.Interface}（{InterfaceName(c.Interface)}）：{c.Detail ?? "未安装驱动"}").ToList();
        return new ModuleLayout(at?.Detail ?? (at is null ? null : "AT 口"), net?.Detail, netMissing?.Detail,
            net is not null || netMissing is not null, missing);
    }

    public static string InterfaceName(int i) => i switch
    {
        0 => "诊断口", 1 => "GPS 口", 2 => "AT 口", 3 => "Modem", 4 => "网卡", _ => "接口",
    };
}

/// <summary>Windows 网卡的一次快照。</summary>
public sealed record AdapterSnapshot(string Name, bool Up, string Status, string Ipv4, long SpeedBps);

/// <summary>
/// "电脑能不能通过模块上网"的诊断。按链路从里到外检查：网卡接口有没有驱动 → 驱动和模块的 USB 网卡模式是否匹配 →
/// SIM / 注册 / 模块数据连接 → Windows 网卡是否连上、有没有拿到 IP。给出第一个断开的环节和解决办法。
/// </summary>
public static class PcLink
{
    /// <summary>按 NetCfgInstanceId 找 Windows 网卡；找不到或查询失败返回 null。</summary>
    public static AdapterSnapshot? Lookup(string? netGuid)
    {
        if (string.IsNullOrEmpty(netGuid)) return null;
        try
        {
            var n = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(i => string.Equals(i.Id, netGuid, StringComparison.OrdinalIgnoreCase));
            if (n is null) return null;
            var ip = n.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                ?.Address.ToString() ?? "";
            var status = n.OperationalStatus switch
            {
                OperationalStatus.Up => "已连接", OperationalStatus.Down => "未连接", OperationalStatus.Dormant => "休眠",
                OperationalStatus.NotPresent => "不存在", _ => n.OperationalStatus.ToString(),
            };
            return new AdapterSnapshot(n.Description, n.OperationalStatus == OperationalStatus.Up, status, ip, n.Speed);
        }
        catch { return null; }
    }

    /// <summary>Quectel 自家的 NDIS 网卡驱动（RmNet / QMI），只能在模块 usbnet=0 时工作。</summary>
    private static bool IsQmiDriver(string name) =>
        name.Contains("Quectel", StringComparison.OrdinalIgnoreCase) || name.Contains("Baiwang", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Wireless Ethernet Adapter", StringComparison.OrdinalIgnoreCase);

    public static PcLinkInfo Evaluate(ModuleLayout layout, QuectelState st, AdapterSnapshot? adapter)
    {
        int? mode = st.Module.UsbNet;
        var modeName = AtParse.UsbNetName(mode);
        var r = new PcLinkInfo
        {
            ModuleIp = st.Network.Ip,
            Adapter = adapter?.Name ?? (layout.NetMissing is not null ? "（没有驱动）" : layout.HasNetInterface ? "（未找到）" : "（无网卡接口）"),
            AdapterState = adapter?.Status ?? (layout.NetMissing ?? ""),
            AdapterIp = adapter?.Ipv4 ?? "",
        };

        // 1. 模块根本没有网卡接口（USB 配置里关掉了）
        if (!layout.HasNetInterface)
            return Bad(r, "error", "模块没有提供 USB 网卡",
                "模块的 USB 配置里网卡接口被关闭（见「设置 → 模块信息 → USB 配置」第 7 项）。电脑无法通过它上网。");

        // 2. 网卡接口没有驱动
        if (layout.NetMissing is not null)
            return mode switch
            {
                1 => Bad(r, "error", "网卡没有驱动：模块是 ECM 模式，Windows 没有自带 ECM 驱动",
                    "把「设置 → USB 网卡模式」改成 RNDIS（Windows 自带驱动），重启模块后即可上网。在大疆设备上使用前记得改回 ECM。", 3),
                0 => Bad(r, "error", "网卡没有驱动：QMI 模式需要 Quectel USB 驱动",
                    "安装 Quectel Windows USB Driver（NDIS 版）；或把「USB 网卡模式」改成 RNDIS，用 Windows 自带驱动。", 3),
                2 => Bad(r, "error", "网卡没有驱动：MBIM 驱动没有加载",
                    "Windows 自带 MBIM 驱动，正常应自动安装。可尝试重新插拔模块，或改用 RNDIS 模式。", 3),
                3 => Bad(r, "error", "网卡没有驱动：RNDIS 驱动没有加载",
                    "Windows 自带 RNDIS 驱动，正常应自动安装。可在设备管理器里为该接口手动选择「远程 NDIS 兼容设备」。"),
                _ => Bad(r, "error", "网卡接口没有驱动", "先读取模块的 USB 网卡模式，再按模式安装对应驱动。"),
            };

        if (adapter is null)
            return Bad(r, "warning", "找不到模块的 Windows 网卡", "网卡驱动已装但系统里没有对应的网络连接，可尝试重新插拔模块。");

        // 3. 驱动与模块模式不匹配（实测：大疆模块默认 ECM，装 Quectel NDIS 驱动后网卡一直"未连接"）
        if (mode is 1 or 2 or 3 && IsQmiDriver(adapter.Name))
            return Bad(r, "error", $"驱动与模块模式不匹配：网卡装的是 Quectel QMI 驱动，模块却是 {modeName} 模式",
                "Quectel 网卡驱动只在 QMI（RmNet）模式下工作。把「USB 网卡模式」改成 RmNet / QMI 并在 Windows「手机网络」里连接；" +
                "或者改成 RNDIS，改用 Windows 自带驱动。", mode == 1 ? 3 : 0);

        // 4. 模块侧：SIM、注册、数据连接
        if (st.Network.Cfun == 4) return Bad(r, "warning", "模块处于飞行模式", "在「设置」里关闭飞行模式。");
        if (st.Sim.State is { Length: > 0 } sim && sim != "READY")
            return Bad(r, "error", $"SIM 卡不可用（{sim}）", "检查 SIM 卡是否插好、是否需要 PIN 码。");
        if (st.Network.EpsRegistration is not (1 or 5))
            return Bad(r, "warning", "模块还没注册到移动网络（" + AtParse.RegText(st.Network.EpsRegistration) + "）",
                "检查天线和信号；如果锁定了频段或小区，试试「恢复全部频段」「解除锁定」。");
        if (st.Network.Ip.Length == 0 && mode is 1 or 3)
            return Bad(r, "warning", "模块已注册，但没有建立数据连接",
                "ECM / RNDIS 模式由模块自己拨号：检查 SIM 卡是否有流量、APN 是否正确（「高级」里 AT+CGDCONT? 查看）。");

        // 5. Windows 网卡
        if (!adapter.Up)
            return mode switch
            {
                0 or 2 => Bad(r, "warning", "网卡未连接：QMI / MBIM 模式需要由 Windows 拨号",
                    "打开 Windows「设置 → 网络和 Internet → 手机网络」并连接。"),
                _ => Bad(r, "warning", "网卡未连接", "模块已拨号但网卡没连上：试试重启模块，或在 Windows 里禁用再启用这个网卡。"),
            };
        if (adapter.Ipv4.Length == 0)
            return Bad(r, "warning", "网卡已连接，但没有拿到 IP 地址", "等待几秒；仍没有时重启模块。");

        r.Severity = "ok";
        r.Summary = $"已连接，电脑可通过模块上网（{adapter.Ipv4}）";
        return r;
    }

    private static PcLinkInfo Bad(PcLinkInfo r, string severity, string summary, string advice, int suggest = -1)
    {
        r.Severity = severity;
        r.Summary = summary;
        r.Advice = advice;
        r.SuggestUsbNet = suggest;
        return r;
    }
}
