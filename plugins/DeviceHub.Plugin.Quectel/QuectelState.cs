namespace DeviceHub.Plugin.Quectel;

/// <summary>推给 UI 的完整状态（camelCase JSON，与 UI 侧 QuectelStateDto 对应）。null = 尚未读到。</summary>
public sealed class QuectelState
{
    public bool Authorized { get; set; }
    public bool Connected { get; set; }
    /// <summary>最近一次操作的结果或错误。</summary>
    public string Status { get; set; } = "";
    public bool Busy { get; set; }

    public DriverInfo Driver { get; set; } = new();
    public PcLinkInfo PcLink { get; set; } = new();
    public ModuleInfo Module { get; set; } = new();
    public SimInfo Sim { get; set; } = new();
    public NetworkInfo Network { get; set; } = new();
    public SignalInfo Signal { get; set; } = new();
    public ServingCell? Serving { get; set; }
    public List<NeighbourCell> Neighbours { get; set; } = new();
    public BandInfo Bands { get; set; } = new();
    public LockInfo Locks { get; set; } = new();
    public GpsInfo Gps { get; set; } = new();
    public SmsInfo Sms { get; set; } = new();
    public EsimInfo Esim { get; set; } = new();
    public List<int> Temperatures { get; set; } = new();
    /// <summary>AT 控制台最近的收发记录（"&gt; " 发出，"&lt; " 收到）。</summary>
    public List<string> Console { get; set; } = new();
    public DateTimeOffset? LastPollAt { get; set; }
}

/// <summary>Windows 上模块各 USB 接口的驱动情况（核心按接口报告：COM 口 / 网卡 / 缺驱动）。</summary>
public sealed class DriverInfo
{
    /// <summary>AT 口有驱动、出现了 COM 口：插件能工作的前提。</summary>
    public bool AtPortReady { get; set; } = true;
    public string AtPortName { get; set; } = "";
    /// <summary>缺驱动的接口，如 "接口 2（AT 口）：未安装驱动"。</summary>
    public List<string> Missing { get; set; } = new();
}

/// <summary>"电脑能不能通过模块上网"的诊断：结论 + 依据。</summary>
public sealed class PcLinkInfo
{
    /// <summary>ok / warning / error。</summary>
    public string Severity { get; set; } = "warning";
    /// <summary>一句话结论，如"已连接，电脑可通过模块上网"。</summary>
    public string Summary { get; set; } = "";
    /// <summary>怎么解决（没问题时为空）。</summary>
    public string Advice { get; set; } = "";
    /// <summary>Windows 网卡：名称、驱动状态、连接状态、IP。</summary>
    public string Adapter { get; set; } = "";
    public string AdapterState { get; set; } = "";
    public string AdapterIp { get; set; } = "";
    /// <summary>模块侧：数据连接（PDP）拿到的 IP。</summary>
    public string ModuleIp { get; set; } = "";
    /// <summary>建议切到的 USB 网卡模式（-1 = 无建议）。</summary>
    public int SuggestUsbNet { get; set; } = -1;
}

public sealed class ModuleInfo
{
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Imei { get; set; } = "";
    /// <summary>USB 网卡模式：0 RmNet/QMI，1 ECM，2 MBIM，3 RNDIS。</summary>
    public int? UsbNet { get; set; }
    public string UsbConfig { get; set; } = "";
    public string AtPort { get; set; } = "";
    /// <summary>当前生效的运营商配置（MBN）。</summary>
    public string ActiveMbn { get; set; } = "";
}

public sealed class SimInfo
{
    /// <summary>+CPIN 状态，如 READY、SIM PIN、NOT INSERTED。</summary>
    public string State { get; set; } = "";
    public string Imsi { get; set; } = "";
    public string Iccid { get; set; } = "";
    public string Smsc { get; set; } = "";
    public string Number { get; set; } = "";
}

public sealed class NetworkInfo
{
    public string Operator { get; set; } = "";
    public string Plmn { get; set; } = "";
    /// <summary>接入技术，如 "FDD LTE"、"TDD LTE"、"WCDMA"。</summary>
    public string Act { get; set; } = "";
    public string Band { get; set; } = "";
    public int? Channel { get; set; }
    /// <summary>EPS 注册状态（+CEREG）：0 未注册、1 本地、2 搜索中、3 被拒、4 未知、5 漫游。</summary>
    public int? EpsRegistration { get; set; }
    /// <summary>电路域注册状态（+CREG），含义同上。</summary>
    public int? CsRegistration { get; set; }
    public string Ip { get; set; } = "";
    /// <summary>+QCFG="nwscanmode"：0 自动、1 仅 GSM、2 仅 WCDMA、3 仅 LTE。</summary>
    public int? ScanMode { get; set; }
    public string ScanSequence { get; set; } = "";
    /// <summary>+QCFG="roamservice"：1 禁止漫游、2 允许、255 自动。</summary>
    public int? RoamService { get; set; }
    public bool? ImsEnabled { get; set; }
    /// <summary>+CFUN：1 全功能、4 飞行模式、0 最小功能。</summary>
    public int? Cfun { get; set; }
}

public sealed class SignalInfo
{
    public int? Csq { get; set; }
    public int? Rssi { get; set; }
    public int? Rsrp { get; set; }
    public int? Rsrq { get; set; }
    public double? Sinr { get; set; }
    /// <summary>0–4 格（按 RSRP，非 LTE 按 RSSI）。</summary>
    public int Bars { get; set; }
}

public sealed class ServingCell
{
    public string State { get; set; } = "";
    public string Rat { get; set; } = "";
    public string Duplex { get; set; } = "";
    public int Mcc { get; set; }
    public int Mnc { get; set; }
    public string CellId { get; set; } = "";
    public int? Pci { get; set; }
    public int? Earfcn { get; set; }
    public int? Band { get; set; }
    public double? DlBandwidthMhz { get; set; }
    public string Tac { get; set; } = "";
    public int? Rsrp { get; set; }
    public int? Rsrq { get; set; }
    public int? Rssi { get; set; }
    public int? Sinr { get; set; }
}

public sealed class NeighbourCell
{
    /// <summary>"intra"（同频）或 "inter"（异频）。</summary>
    public string Kind { get; set; } = "";
    public int Earfcn { get; set; }
    public int? Band { get; set; }
    public int? Pci { get; set; }
    public int? Rsrp { get; set; }
    public int? Rsrq { get; set; }
    public int? Rssi { get; set; }
}

public sealed class BandInfo
{
    /// <summary>模块支持的 LTE 频段（出厂掩码）。</summary>
    public List<int> Supported { get; set; } = new();
    /// <summary>当前允许的 LTE 频段。</summary>
    public List<int> Enabled { get; set; } = new();
    public string GwMask { get; set; } = "";
    public string LteMask { get; set; } = "";
    public string FactoryLteMask { get; set; } = "";
    public bool Restricted => Supported.Count > 0 && Enabled.Count < Supported.Count;
}

public sealed class LockInfo
{
    /// <summary>+QNWLOCK="common/4g" 锁定的小区（频点 + PCI）。</summary>
    public List<CellRef> Cells { get; set; } = new();
    /// <summary>+QNWLOCK="common/lte" 的锁定：0 未锁、1 锁频点、2 锁小区。</summary>
    public int LteAction { get; set; }
    public int? LteEarfcn { get; set; }
    public int? LtePci { get; set; }
    public bool Locked => Cells.Count > 0 || LteAction != 0;
}

public sealed class CellRef
{
    public int Earfcn { get; set; }
    public int Pci { get; set; }
}

public sealed class GpsInfo
{
    public bool? On { get; set; }
    public bool Fix { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Altitude { get; set; }
    public double? Hdop { get; set; }
    public int? Satellites { get; set; }
    public double? SpeedKmh { get; set; }
    public double? Course { get; set; }
    public string Utc { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class SmsInfo
{
    public int? Used { get; set; }
    public int? Total { get; set; }
    public string Storage { get; set; } = "";
    /// <summary>只有用户点"读取短信"后才加载（读取会把未读标为已读）。</summary>
    public bool Loaded { get; set; }
    public int NewSinceLoad { get; set; }
    public List<SmsMessage> Messages { get; set; } = new();
}

public sealed class SmsMessage
{
    /// <summary>模块存储里的序号；本机发出、只存在插件记录里的为负数。</summary>
    public int Index { get; set; }
    /// <summary>本机发出的（模块的"已发送"存储，或插件记下的发送记录）。</summary>
    public bool Outgoing { get; set; }
    public string Status { get; set; } = "";
    public string Peer { get; set; } = "";
    public string Time { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>eUICC 上的一个配置（SGP.22 ProfileInfo）。</summary>
public sealed class EsimProfile
{
    public string Iccid { get; set; } = "";
    public string Aid { get; set; } = "";
    public bool Enabled { get; set; }
    public string Nickname { get; set; } = "";
    public string ServiceProvider { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>test / provisioning / operational。</summary>
    public string Class { get; set; } = "";
}

/// <summary>eUICC 上待发送给运营商服务器的通知（SGP.22 NotificationMetadata）。</summary>
public sealed class EsimNotification
{
    public int Seq { get; set; }
    /// <summary>安装 / 启用 / 停用 / 删除。</summary>
    public string Operation { get; set; } = "";
    public string Address { get; set; } = "";
    public string Iccid { get; set; } = "";
}

public sealed class EsimInfo
{
    /// <summary>null = 还没检查；false = 当前 SIM 不是 eUICC（没有 ISD-R）。</summary>
    public bool? IsEuicc { get; set; }
    public string Eid { get; set; } = "";
    public string SgpVersion { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string ProfileVersion { get; set; } = "";
    public int? FreeNvmBytes { get; set; }
    public string Sas { get; set; } = "";
    public List<EsimProfile> Profiles { get; set; } = new();
    public List<EsimNotification> Notifications { get; set; } = new();
    public string Error { get; set; } = "";
    public DateTimeOffset? ReadAt { get; set; }
}

