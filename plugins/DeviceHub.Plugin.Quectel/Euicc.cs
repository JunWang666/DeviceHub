using System.Text;

namespace DeviceHub.Plugin.Quectel;

/// <summary>BER-TLV（SGP.22 ES10 用的编码）：多字节标签（BF3E、9F70）、长度 81 xx / 82 xx xx。</summary>
public sealed class Tlv
{
    public int Tag { get; init; }
    public byte[] Value { get; init; } = [];
    public List<Tlv> Children { get; } = new();

    public static List<Tlv> Parse(ReadOnlySpan<byte> data)
    {
        var list = new List<Tlv>();
        int i = 0;
        while (i < data.Length)
        {
            if (data[i] is 0x00 or 0xFF) { i++; continue; } // 填充
            int tag = data[i++];
            bool constructed = (tag & 0x20) != 0;
            if ((tag & 0x1F) == 0x1F)
                do { tag = (tag << 8) | data[i]; } while ((data[i++] & 0x80) != 0);
            int len = data[i++];
            if (len == 0x81) len = data[i++];
            else if (len == 0x82) { len = (data[i] << 8) | data[i + 1]; i += 2; }
            if (i + len > data.Length) throw new FormatException("TLV 长度越界");
            var t = new Tlv { Tag = tag, Value = data.Slice(i, len).ToArray() };
            if (constructed) t.Children.AddRange(Parse(t.Value));
            list.Add(t);
            i += len;
        }
        return list;
    }

    public Tlv? Find(int tag) => Children.FirstOrDefault(c => c.Tag == tag);
    public IEnumerable<Tlv> All(int tag) => Children.Where(c => c.Tag == tag);
    public string Text => Encoding.UTF8.GetString(Value);
    public int Int => Value.Aggregate(0, (a, b) => (a << 8) | b);

    public static byte[] Encode(int tag, params byte[][] parts)
    {
        var value = parts.SelectMany(p => p).ToArray();
        var t = new List<byte>();
        if (tag > 0xFFFF) t.Add((byte)(tag >> 16));
        if (tag > 0xFF) t.Add((byte)(tag >> 8));
        t.Add((byte)tag);
        if (value.Length < 0x80) t.Add((byte)value.Length);
        else if (value.Length < 0x100) { t.Add(0x81); t.Add((byte)value.Length); }
        else { t.Add(0x82); t.Add((byte)(value.Length >> 8)); t.Add((byte)value.Length); }
        t.AddRange(value);
        return t.ToArray();
    }
}

/// <summary>
/// 经模块 AT 口访问 eUICC 的 ISD-R（SGP.22 ES10b/ES10c）：AT+CCHO 打开逻辑通道，AT+CGLA 收发 APDU（STORE DATA），AT+CCHC 关闭。
/// 只实现本机管理用到的指令：读 EID / 卡信息 / 配置列表 / 通知，启用、停用、删除配置，改昵称，删除通知。
/// 下载配置（ES9+ 与 SM-DP+ 通信）不在范围内。
/// </summary>
public sealed class EuiccSession
{
    public const string IsdrAid = "A0000005591010FFFFFFFF8900000100";
    private readonly Func<string, int, Task<AtResponse>> _at;
    private int _channel;

    public EuiccSession(Func<string, int, Task<AtResponse>> at) => _at = at;

    /// <summary>打开到 ISD-R 的逻辑通道；卡没有 ISD-R（不是 eUICC）时返回 false。</summary>
    public async Task<bool> OpenAsync()
    {
        var r = await _at($"AT+CCHO=\"{IsdrAid}\"", 5000);
        if (!r.Ok)
        {
            // 上次异常退出可能留下了打开的通道（每张卡只有 3 个），关掉再试一次。
            if (r.Error?.Contains("not inserted", StringComparison.OrdinalIgnoreCase) == true) throw new InvalidOperationException("没有插 SIM 卡");
            for (int c = 1; c <= 3; c++) await _at($"AT+CCHC={c}", 3000);
            r = await _at($"AT+CCHO=\"{IsdrAid}\"", 5000);
            if (!r.Ok) return false;
        }
        _channel = AtParse.Int(r.Value("+CCHO:")) ?? AtParse.Int(r.Lines.FirstOrDefault(l => l.All(char.IsDigit))) ?? 0;
        if (_channel is < 1 or > 19) throw new InvalidOperationException("模块没有返回逻辑通道号：" + string.Join(" ", r.Lines));
        return true;
    }

    public async Task CloseAsync()
    {
        if (_channel > 0) await _at($"AT+CCHC={_channel}", 3000);
        _channel = 0;
    }

    private byte Cla => (byte)(_channel <= 3 ? 0x80 | _channel : 0xC0 | (_channel - 4));

    /// <summary>发一个 APDU，处理 61xx（GET RESPONSE 续取）与 6Cxx（按正确长度重发），返回数据（不含状态字）。</summary>
    private async Task<(byte[] Data, int Sw)> TransmitAsync(byte[] apdu)
    {
        var data = new List<byte>();
        var cmd = apdu;
        for (int guard = 0; guard < 32; guard++)
        {
            var hex = Convert.ToHexString(cmd);
            var r = await _at($"AT+CGLA={_channel},{hex.Length},\"{hex}\"", 10000);
            if (!r.Ok) throw new InvalidOperationException("AT+CGLA 失败：" + r.Error);
            var resp = AtParse.Csv(r.Value("+CGLA:") ?? "").ElementAtOrDefault(1) ?? "";
            if (resp.Length < 4) throw new InvalidOperationException("卡没有返回状态字");
            var bytes = Convert.FromHexString(resp);
            int sw = (bytes[^2] << 8) | bytes[^1];
            data.AddRange(bytes[..^2]);
            if ((sw & 0xFF00) == 0x6100) { cmd = [Cla, 0xC0, 0x00, 0x00, (byte)(sw & 0xFF)]; continue; }
            if ((sw & 0xFF00) == 0x6C00) { cmd = [.. apdu[..4], (byte)(sw & 0xFF)]; continue; }
            return (data.ToArray(), sw);
        }
        throw new InvalidOperationException("响应续取次数过多");
    }

    /// <summary>ES10 指令走 STORE DATA（INS E2）：超过 120 字节分块，最后一块 P1=0x91。返回解析好的顶层 TLV。</summary>
    private async Task<Tlv> StoreDataAsync(byte[] payload)
    {
        const int Block = 120;
        byte[] last = [];
        int sw = 0;
        for (int off = 0, blk = 0; off < payload.Length; off += Block, blk++)
        {
            var chunk = payload.AsSpan(off, Math.Min(Block, payload.Length - off)).ToArray();
            bool final = off + Block >= payload.Length;
            var apdu = new byte[] { Cla, 0xE2, (byte)(final ? 0x91 : 0x11), (byte)blk, (byte)chunk.Length }.Concat(chunk).ToArray();
            (last, sw) = await TransmitAsync(apdu);
            if (!final && sw != 0x9000) break;
        }
        if (sw != 0x9000) throw new InvalidOperationException($"卡返回错误 {sw:X4}");
        return Tlv.Parse(last).FirstOrDefault() ?? throw new InvalidOperationException("卡返回了空数据");
    }

    // —— 读取 ——

    public async Task<string> GetEidAsync()
    {
        var t = await StoreDataAsync(Tlv.Encode(0xBF3E, Tlv.Encode(0x5C, [0x5A])));
        return Convert.ToHexString(t.Find(0x5A)?.Value ?? []);
    }

    public async Task ReadInfo2Async(EsimInfo info)
    {
        var t = await StoreDataAsync(Tlv.Encode(0xBF22));
        info.ProfileVersion = Ver(t.Find(0x81));
        info.SgpVersion = Ver(t.Find(0x82));
        info.Firmware = Ver(t.Find(0x83));
        // extCardResource（84）：81 已装应用数、82 剩余非易失存储、83 剩余易失存储
        if (t.Find(0x84) is { } res)
        {
            var inner = Tlv.Parse(res.Value);
            info.FreeNvmBytes = inner.FirstOrDefault(x => x.Tag == 0x82)?.Int;
        }
        info.Sas = t.Find(0x0C)?.Text ?? "";
        static string Ver(Tlv? v) => v is { Value.Length: 3 } ? $"{v.Value[0]}.{v.Value[1]}.{v.Value[2]}" : "";
    }

    public async Task<List<EsimProfile>> GetProfilesAsync()
    {
        var t = await StoreDataAsync(Tlv.Encode(0xBF2D));
        var list = new List<EsimProfile>();
        var ok = t.Find(0xA0);
        if (ok is null) throw new InvalidOperationException("读取配置列表失败：" + (t.Find(0x81)?.Int.ToString() ?? "未知错误"));
        foreach (var p in ok.All(0xE3))
        {
            list.Add(new EsimProfile
            {
                Iccid = DecodeIccid(p.Find(0x5A)?.Value ?? []),
                Aid = Convert.ToHexString(p.Find(0x4F)?.Value ?? []),
                Enabled = p.Find(0x9F70)?.Int == 1,
                Nickname = p.Find(0x90)?.Text ?? "",
                ServiceProvider = p.Find(0x91)?.Text ?? "",
                Name = p.Find(0x92)?.Text ?? "",
                Class = p.Find(0x95)?.Int switch { 0 => "test", 1 => "provisioning", 2 => "operational", _ => "" },
            });
        }
        return list;
    }

    public async Task<List<EsimNotification>> ListNotificationsAsync()
    {
        var t = await StoreDataAsync(Tlv.Encode(0xBF28));
        var list = new List<EsimNotification>();
        var ok = t.Find(0xA0);
        if (ok is null) return list; // 81 = 未定义错误；没有通知时也是空 A0
        foreach (var n in ok.All(0xBF2F))
        {
            list.Add(new EsimNotification
            {
                Seq = n.Find(0x80)?.Int ?? -1,
                Operation = OperationName(n.Find(0x81)?.Value ?? []),
                Address = n.Find(0x0C)?.Text ?? "",
                Iccid = DecodeIccid(n.Find(0x5A)?.Value ?? []),
            });
        }
        return list;
    }

    /// <summary>profileManagementOperation 是 BIT STRING：首字节为未用位数，第二字节 bit7..4 = 安装/启用/停用/删除。</summary>
    private static string OperationName(byte[] bits)
    {
        if (bits.Length < 2) return "";
        var b = bits[1];
        var ops = new List<string>();
        if ((b & 0x80) != 0) ops.Add("安装");
        if ((b & 0x40) != 0) ops.Add("启用");
        if ((b & 0x20) != 0) ops.Add("停用");
        if ((b & 0x10) != 0) ops.Add("删除");
        return string.Join("/", ops);
    }

    // —— 操作 ——

    /// <summary>启用配置。refreshFlag=true：卡随后通知模块重新读卡（模块会短暂掉网再用新配置注册）。</summary>
    public async Task<int> EnableAsync(string iccid) =>
        Result(await StoreDataAsync(Tlv.Encode(0xBF31, Tlv.Encode(0xA0, Tlv.Encode(0x5A, EncodeIccid(iccid))), Tlv.Encode(0x81, [0xFF]))));

    public async Task<int> DisableAsync(string iccid) =>
        Result(await StoreDataAsync(Tlv.Encode(0xBF32, Tlv.Encode(0xA0, Tlv.Encode(0x5A, EncodeIccid(iccid))), Tlv.Encode(0x81, [0xFF]))));

    public async Task<int> DeleteAsync(string iccid) =>
        Result(await StoreDataAsync(Tlv.Encode(0xBF33, Tlv.Encode(0x5A, EncodeIccid(iccid)))));

    public async Task<int> SetNicknameAsync(string iccid, string nickname) =>
        Result(await StoreDataAsync(Tlv.Encode(0xBF29, Tlv.Encode(0x5A, EncodeIccid(iccid)), Tlv.Encode(0x90, Encoding.UTF8.GetBytes(nickname)))));

    public async Task<int> RemoveNotificationAsync(int seq) =>
        Result(await StoreDataAsync(Tlv.Encode(0xBF30, Tlv.Encode(0x80, IntBytes(seq)))));

    private static int Result(Tlv t) => t.Find(0x80)?.Int ?? -1;

    /// <summary>ES10 操作结果码的说明（启用 / 停用 / 删除 / 改名 / 删通知共用一部分）。</summary>
    public static string ResultText(int code) => code switch
    {
        0 => "成功",
        1 => "卡上找不到这个配置",
        2 => "配置状态不对（已启用的要先停用才能删除；启用的必须是已停用的配置）",
        3 => "被配置策略禁止",
        4 => "配置类别不允许这样操作",
        5 => "卡正忙（模块正在和卡交互），稍后再试",
        6 => "卡还有未完成的操作",
        127 => "未定义错误",
        _ => $"错误代码 {code}",
    };

    private static byte[] IntBytes(int v)
    {
        if (v < 0x80) return [(byte)v];
        if (v < 0x8000) return [(byte)(v >> 8), (byte)v];
        return [(byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }

    /// <summary>ICCID 是半字节互换的 BCD，F 填充："985802…" → "8985203…"。</summary>
    public static string DecodeIccid(byte[] v)
    {
        var sb = new StringBuilder();
        foreach (var b in v) { sb.Append((b & 0x0F).ToString("X")); sb.Append((b >> 4).ToString("X")); }
        return sb.ToString().TrimEnd('F');
    }

    public static byte[] EncodeIccid(string iccid)
    {
        var s = iccid.Trim().ToUpperInvariant();
        if (s.Length % 2 == 1) s += "F";
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = (byte)((Convert.ToByte(s[2 * i + 1].ToString(), 16) << 4) | Convert.ToByte(s[2 * i].ToString(), 16));
        return b;
    }
}
