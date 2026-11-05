using System.Globalization;
using System.Numerics;
using System.Text;

namespace DeviceHub.Plugin.Quectel;

/// <summary>
/// Quectel AT 响应解析（格式以 EG25-G 固件 QDC507GLEFM21 实测为准，见各方法注释里的样例）。
/// 全部是纯函数，便于离线测试。
/// </summary>
public static class AtParse
{
    /// <summary>按逗号拆分，引号内的逗号不拆；去掉字段两侧引号与空白。</summary>
    public static List<string> Csv(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        foreach (var ch in s)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ',' && !q) { list.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        list.Add(sb.ToString().Trim());
        return list;
    }

    public static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    public static double? Dbl(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string At(List<string> f, int i) => i < f.Count ? f[i] : "";

    /// <summary>"+CSQ: 27,99" → 27（99 = 未知）。</summary>
    public static int? Csq(string? v) => v is null ? null : Int(Csv(v)[0]) is { } c and not 99 ? c : null;

    /// <summary>"+QCSQ: "LTE",59,-90,130,-10" → RSSI、RSRP、SINR（0–250 → v/5−20 dB）、RSRQ。</summary>
    public static (string Rat, int? Rssi, int? Rsrp, double? Sinr, int? Rsrq) Qcsq(string? v)
    {
        if (v is null) return ("", null, null, null, null);
        var f = Csv(v);
        var rat = At(f, 0);
        // 这版固件的 RSSI 给的是正数（59 表示 -59 dBm）。
        var rssi = Int(At(f, 1)) is { } r ? -Math.Abs(r) : (int?)null;
        if (rat == "LTE")
            return (rat, rssi, Int(At(f, 2)), Int(At(f, 3)) is { } s ? Math.Round(s / 5.0 - 20, 1) : null, Int(At(f, 4)));
        return (rat, rssi, null, null, null);
    }

    /// <summary>读形式 "+CEREG: 0,5"（n,stat[,…]）→ stat。</summary>
    public static int? RegStat(string? v) => v is null ? null : Int(At(Csv(v), 1));

    /// <summary>"+COPS: 0,0,"CHN-CT",7" → 运营商名。</summary>
    public static string Cops(string? v) => v is null ? "" : At(Csv(v), 2);

    /// <summary>"+QNWINFO: "FDD LTE","46011","LTE BAND 1",100"。</summary>
    public static (string Act, string Plmn, string Band, int? Channel) Qnwinfo(string? v)
    {
        if (v is null) return ("", "", "", null);
        var f = Csv(v);
        return (At(f, 0), At(f, 1), At(f, 2), Int(At(f, 3)));
    }

    /// <summary>
    /// LTE 服务小区：
    /// "servingcell","NOCONN","LTE","FDD",460,11,E707486,362,100,1,5,5,9C72,-89,-9,-61,7,38
    /// → state, rat, duplex, MCC, MNC, cellID, PCI, EARFCN, band, UL/DL 带宽码, TAC, RSRP, RSRQ, RSSI, SINR, srxlev。
    /// 非 LTE（"SEARCH" / "LIMSRV" 等）只填状态与制式。
    /// </summary>
    public static ServingCell? Serving(IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            var f = Csv(v);
            if (At(f, 0) != "servingcell") continue;
            var c = new ServingCell { State = At(f, 1), Rat = At(f, 2) };
            if (c.Rat != "LTE" || f.Count < 17) return c;
            c.Duplex = At(f, 3);
            c.Mcc = Int(At(f, 4)) ?? 0;
            c.Mnc = Int(At(f, 5)) ?? 0;
            c.CellId = At(f, 6);
            c.Pci = Int(At(f, 7));
            c.Earfcn = Int(At(f, 8));
            c.Band = Int(At(f, 9));
            c.DlBandwidthMhz = Int(At(f, 11)) is { } bw and >= 0 and <= 5 ? new[] { 1.4, 3, 5, 10, 15, 20 }[bw] : null;
            c.Tac = At(f, 12);
            c.Rsrp = Int(At(f, 13));
            c.Rsrq = Int(At(f, 14));
            c.Rssi = Int(At(f, 15));
            c.Sinr = Int(At(f, 16));
            return c;
        }
        return null;
    }

    /// <summary>
    /// 邻区："neighbourcell intra","LTE",100,362,-9,-90,-61,-20,37,…（earfcn, pci, rsrq, rsrp, rssi, sinr…）；
    /// 异频未测量时字段为 "-"。
    /// </summary>
    public static List<NeighbourCell> Neighbours(IEnumerable<string> values)
    {
        var list = new List<NeighbourCell>();
        foreach (var v in values)
        {
            var f = Csv(v);
            if (!At(f, 0).StartsWith("neighbourcell", StringComparison.Ordinal) || At(f, 1) != "LTE") continue;
            if (Int(At(f, 2)) is not { } earfcn) continue;
            list.Add(new NeighbourCell
            {
                Kind = At(f, 0).EndsWith("inter", StringComparison.Ordinal) ? "inter" : "intra",
                Earfcn = earfcn,
                Band = EarfcnToBand(earfcn),
                Pci = Int(At(f, 3)),
                Rsrq = Int(At(f, 4)),
                Rsrp = Int(At(f, 5)),
                Rssi = Int(At(f, 6)),
            });
        }
        return list;
    }

    /// <summary>"+CGPADDR: 1,"10.199.195.34"" → 第一个非 0.0.0.0 的 IPv4（及可能的 IPv6）。</summary>
    public static string Ip(IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            var f = Csv(v);
            for (int i = 1; i < f.Count; i++)
            {
                var a = f[i];
                if (a.Length == 0 || a.StartsWith("0.0.0.0", StringComparison.Ordinal) || a.Trim('0', '.').Length == 0) continue;
                // IPv6 会以点分十进制给出 16 段，太长不显示，只取 IPv4。
                if (a.Count(c => c == '.') == 3) return a;
            }
        }
        return "";
    }

    /// <summary>"+QTEMP: 35,36,34" → 各传感器温度（℃）。</summary>
    public static List<int> Temps(string? v) => v is null ? new() : Csv(v).Select(Int).OfType<int>().ToList();

    /// <summary>"+QCFG: "band",0xbff,0x42000001e20b0e18df,0x0" → (GW 掩码, LTE 掩码)。</summary>
    public static (string Gw, string Lte) BandMasks(string? v)
    {
        if (v is null) return ("", "");
        var f = Csv(v);
        return (At(f, 1), At(f, 2));
    }

    /// <summary>LTE 掩码第 n 位 = Band n+1。</summary>
    public static List<int> MaskToBands(string mask)
    {
        var list = new List<int>();
        if (!TryMask(mask, out var m)) return list;
        for (int i = 0; i < 256 && !m.IsZero; i++, m >>= 1)
            if (!m.IsEven) list.Add(i + 1);
        return list;
    }

    public static string BandsToMask(IEnumerable<int> bands)
    {
        var m = BigInteger.Zero;
        foreach (var b in bands.Distinct()) if (b is >= 1 and <= 256) m |= BigInteger.One << (b - 1);
        var hex = m.ToString("x").TrimStart('0');
        return "0x" + (hex.Length == 0 ? "0" : hex);
    }

    private static bool TryMask(string mask, out BigInteger m)
    {
        var s = mask.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        // 前补 0 防止最高位被当成符号位。
        return BigInteger.TryParse("0" + s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out m);
    }

    /// <summary>"+QNWLOCK: "common/4g",1,100,362" → 锁定的小区列表（数量为 0 时为空）。</summary>
    public static List<CellRef> Lock4g(string? v)
    {
        var list = new List<CellRef>();
        if (v is null) return list;
        var f = Csv(v);
        int n = Int(At(f, 1)) ?? 0;
        for (int i = 0; i < n; i++)
            if (Int(At(f, 2 + i * 2)) is { } e && Int(At(f, 3 + i * 2)) is { } p) list.Add(new CellRef { Earfcn = e, Pci = p });
        return list;
    }

    /// <summary>"+QNWLOCK: "common/lte",0,0,0,0" → (action, earfcn, pci)。</summary>
    public static (int Action, int? Earfcn, int? Pci) LockLte(string? v)
    {
        if (v is null) return (0, null, null);
        var f = Csv(v);
        int a = Int(At(f, 1)) ?? 0;
        return (a, a > 0 ? Int(At(f, 2)) : null, a == 2 ? Int(At(f, 3)) : null);
    }

    /// <summary>
    /// "+QGPSLOC: 061951.0,3150.7223N,11711.9293E,0.7,62.2,2,0.00,0.0,0.0,110513,09"（mode 2 为十进制度：
    /// "+QGPSLOC: 061951.0,31.84537,117.19882,0.7,62.2,2,…"）。
    /// </summary>
    public static GpsInfo? GpsLoc(string? v)
    {
        if (v is null) return null;
        var f = Csv(v);
        if (f.Count < 11) return null;
        return new GpsInfo
        {
            On = true, Fix = true,
            Utc = At(f, 9) is { Length: 6 } d && At(f, 0) is { Length: >= 6 } t
                ? $"20{d[4..]}-{d[2..4]}-{d[..2]} {t[..2]}:{t[2..4]}:{t[4..6]} UTC" : "",
            Latitude = Dbl(At(f, 1)), Longitude = Dbl(At(f, 2)),
            Hdop = Dbl(At(f, 3)), Altitude = Dbl(At(f, 4)),
            Course = Dbl(At(f, 6)), SpeedKmh = Dbl(At(f, 7)),
            Satellites = Int(At(f, 10)),
        };
    }

    /// <summary>"+CPMS: "ME",3,23,"ME",3,23,"ME",3,23" → (存储, 已用, 总数)。</summary>
    public static (string Mem, int? Used, int? Total) Cpms(string? v)
    {
        if (v is null) return ("", null, null);
        var f = Csv(v);
        return (At(f, 0), Int(At(f, 1)), Int(At(f, 2)));
    }

    /// <summary>
    /// 文本模式 + UCS2 字符集下的 AT+CMGL="ALL"：
    /// +CMGL: 0,"REC READ","002B0038003500320031","","26/11/05,22:00:00+32" 后跟一行正文（UCS2 十六进制）。
    /// </summary>
    public static List<SmsMessage> Cmgl(IReadOnlyList<string> lines)
    {
        var list = new List<SmsMessage>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("+CMGL:", StringComparison.Ordinal)) continue;
            var f = Csv(lines[i][6..]);
            var body = new StringBuilder();
            while (i + 1 < lines.Count && !lines[i + 1].StartsWith("+CMGL:", StringComparison.Ordinal))
                body.Append(lines[++i]);
            list.Add(new SmsMessage
            {
                Index = Int(At(f, 0)) ?? -1,
                Outgoing = At(f, 1).StartsWith("STO", StringComparison.Ordinal),
                Status = At(f, 1) switch { "REC UNREAD" => "未读", "REC READ" => "已读", "STO UNSENT" => "草稿", "STO SENT" => "已发送", var s => s },
                Peer = Ucs2Decode(At(f, 2)),
                Time = SmsTime(At(f, 4)),
                Text = Ucs2Decode(body.ToString()),
            });
        }
        return list;
    }

    /// <summary>"26/11/05,22:00:00+32"（时区单位 15 分钟）→ "2026-11-05 22:00:00"。</summary>
    private static string SmsTime(string t)
    {
        if (t.Length < 17) return t;
        return $"20{t[..2]}-{t[3..5]}-{t[6..8]} {t[9..17]}";
    }

    /// <summary>会话分组用的号码键：只留数字，取后 11 位（"+8613800000000" 与 "13800000000" 归为同一会话）。</summary>
    public static string PeerKey(string peer)
    {
        var d = new string(peer.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? peer : d.Length > 11 ? d[^11..] : d;
    }

    /// <summary>UCS2 十六进制 → 文本；不是合法 UCS2 时原样返回。</summary>
    public static string Ucs2Decode(string hex)
    {
        if (hex.Length == 0 || hex.Length % 4 != 0 || !hex.All(Uri.IsHexDigit)) return hex;
        var bytes = Convert.FromHexString(hex);
        return Encoding.BigEndianUnicode.GetString(bytes);
    }

    public static string Ucs2Encode(string text) => Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));

    /// <summary>信号格数（0–4）：LTE 按 RSRP，否则按 RSSI。</summary>
    public static int Bars(int? rsrp, int? rssi) => rsrp switch
    {
        >= -85 => 4, >= -95 => 3, >= -105 => 2, >= -115 => 1, not null => 0,
        _ => rssi switch { >= -65 => 4, >= -75 => 3, >= -85 => 2, >= -95 => 1, _ => 0 },
    };

    /// <summary>LTE 下行 EARFCN → 频段（36.101 表 5.7.3-1，只列 EG25-G 支持的频段）。</summary>
    public static int? EarfcnToBand(int earfcn) => earfcn switch
    {
        >= 0 and <= 599 => 1, <= 1199 => 2, <= 1949 => 3, <= 2399 => 4, <= 2649 => 5,
        >= 2750 and <= 3449 => 7, >= 3450 and <= 3799 => 8,
        >= 5010 and <= 5179 => 12, >= 5180 and <= 5279 => 13,
        >= 5850 and <= 5999 => 18, >= 6000 and <= 6149 => 19, >= 6150 and <= 6449 => 20,
        >= 8040 and <= 8689 => 25, >= 8690 and <= 9039 => 26, >= 9210 and <= 9659 => 28,
        >= 36200 and <= 36349 => 34, >= 37750 and <= 38249 => 38, >= 38250 and <= 38649 => 39,
        >= 38650 and <= 39649 => 40, >= 39650 and <= 41589 => 41,
        >= 66436 and <= 67335 => 66, >= 68586 and <= 68935 => 71,
        _ => null,
    };

    public static string RegText(int? stat) => stat switch
    {
        0 => "未注册", 1 => "已注册（本地）", 2 => "搜索中", 3 => "注册被拒", 4 => "未知", 5 => "已注册（漫游）",
        _ => "—",
    };

    public static string UsbNetName(int? mode) => mode switch
    {
        0 => "RmNet / QMI", 1 => "ECM", 2 => "MBIM", 3 => "RNDIS", _ => "—",
    };
}
