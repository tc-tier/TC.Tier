using System.Buffers.Binary;
using System.Net;

namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// RFC 1035 线格式编解码（internal——#498）：查询构造（RD + EDNS0 OPT/DO）与应答解析
/// （压缩指针 0xC0 防环 + A/AAAA/SRV/CNAME RDATA；authority/additional 节跳过——stub 只消费应答节）。
/// <para>★ 解析内环纪律：span 走读、保留记录才物化 string——无 LINQ/装箱/中途 string 分配；
/// 确定性：同应答字节 → 同结果（记录保持应答节顺序）。</para>
/// </summary>
internal static class DnsWire
{
    /// <summary>头部定长（RFC 1035 §4.1.1）。</summary>
    public const int HeaderBytes = 12;

    /// <summary>单名线格式上限（各 label ≤63 由编码守卫）。</summary>
    public const int MaxNameWireBytes = 255;

    /// <summary>查询报文上限（EDNS 声明的是接收容量——查询本身恒小，经典 512 够用）。</summary>
    public const int MaxQueryBytes = 512;

    /// <summary>压缩指针跳转上限（防环——RFC 1025 无环保证，解析器自查）。</summary>
    private const int MaxPointerJumps = 100;

    private const ushort ClassInternet = 1;

    // ═══════════════ 查询构造 ═══════════════

    /// <summary>编码标准查询（RD 置位 + 可选 EDNS0 OPT——载荷协商 + DO 位）。
    /// 头部与 OPT 前缀走 <see cref="DnsMessageHeaderCodec"/>/<see cref="DnsRecordPrefixCodec"/>
    /// 生成物（布局声明一次——零手写偏移）；名字/节游标为结构性推进。</summary>
    /// <returns>写入字节数。</returns>
    /// <exception cref="ArgumentException">名字非法或缓冲区不足。</exception>
    public static int EncodeQuery(Span<byte> buffer, ushort id, string qname, DnsRecordType qtype,
        int ednsPayload, bool dnsSecOk)
    {
        var pos = 0;
        // 头部（生成 codec——ID | flags(RD) | QDCOUNT=1 | AN/NS=0 | AR=EDNS 时 1）
        if (buffer.Length < DnsMessageHeaderCodec.StructSize)
            throw new ArgumentException("缓冲区不足（头部）。", nameof(buffer));
        DnsMessageHeaderCodec.Write(buffer, new DnsMessageHeader
        {
            Id = id,
            Flags = DnsHeaderFlags.RecursionDesired,
            QuestionCount = 1,
            AdditionalCount = (ushort)(ednsPayload > 0 ? 1 : 0),
        });
        pos = DnsMessageHeaderCodec.StructSize;

        // 问题节：QNAME + QTYPE + QCLASS=IN
        pos += EncodeName(buffer[pos..], qname);
        if (buffer.Length < pos + 4) throw new ArgumentException("缓冲区不足（问题节）。", nameof(buffer));
        BinaryPrimitives.WriteUInt16BigEndian(buffer[pos..], (ushort)qtype);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[(pos + 2)..], ClassInternet);
        pos += 4;

        // EDNS0 OPT 伪记录（RFC 6891：NAME=根 | TYPE=41 | CLASS=载荷 | TTL=扩展段(DO 占 bit15) | RDLEN=0）
        if (ednsPayload > 0)
        {
            if (buffer.Length < pos + 1 + DnsRecordPrefixCodec.StructSize)
                throw new ArgumentException("缓冲区不足（EDNS0）。", nameof(buffer));
            buffer[pos] = 0;   // OPT 根名
            DnsRecordPrefixCodec.Write(buffer[(pos + 1)..], new DnsRecordPrefix
            {
                Type = (ushort)DnsRecordType.OPT,
                Class = (ushort)ednsPayload,
                Ttl = dnsSecOk ? 0x8000u : 0u,   // DO = 扩展段最高位
                RdLength = 0,
            });
            pos += 1 + DnsRecordPrefixCodec.StructSize;
        }

        return pos;
    }

    /// <summary>编码域名为 wire labels（尾点容忍；空 label/超长/非 ASCII 拒绝）。</summary>
    /// <returns>写入字节数。</returns>
    public static int EncodeName(Span<byte> buffer, string name)
    {
        var start = 0;
        var pos = 0;
        var end = name.Length;
        if (end > 0 && name[end - 1] == '.') end--;   // 尾点 = 绝对名标记，wire 不再需要

        while (start < end)
        {
            var dot = name.IndexOf('.', start, end - start);
            if (dot < 0) dot = end;
            var labelLen = dot - start;
            if (labelLen == 0) throw new ArgumentException($"域名为空 label：'{name}'。", nameof(name));
            if (labelLen > 63) throw new ArgumentException($"域名 label 超 63 字节：'{name}'。", nameof(name));
            if (buffer.Length < pos + 1 + labelLen) throw new ArgumentException("缓冲区不足（名字）。", nameof(buffer));
            buffer[pos] = (byte)labelLen;
            for (var i = 0; i < labelLen; i++)
            {
                var c = name[start + i];
                if (c > 127) throw new ArgumentException($"域名含非 ASCII 字符：'{name}'。", nameof(name));
                buffer[pos + 1 + i] = (byte)c;
            }
            pos += 1 + labelLen;
            start = dot + 1;
        }

        if (pos == 0) throw new ArgumentException("域名为空。", nameof(name));
        if (pos + 1 > buffer.Length) throw new ArgumentException("缓冲区不足（名字根）。", nameof(buffer));
        if (pos > MaxNameWireBytes - 1) throw new ArgumentException($"域名超线格式上限：'{name}'。", nameof(name));
        buffer[pos] = 0;   // 根 label 终结
        return pos + 1;
    }

    // ═══════════════ 应答解析 ═══════════════

    /// <summary>解析后的应答（仅保留应答节 A/AAAA/SRV/CNAME——其余类型与 authority/additional 节跳过）。</summary>
    public sealed class Message
    {
        /// <summary>事务 ID（调用方与请求 ID 对账）。</summary>
        public ushort Id { get; internal set; }

        /// <summary>TC 截断位（true = 应答不完整，须 TCP 重试）。</summary>
        public bool Truncated { get; internal set; }

        /// <summary>AD 位（递归层验证结论——security-aware 外显）。</summary>
        public bool AuthenticatedData { get; internal set; }

        /// <summary>CD 位（递归层未验证——原样外显）。</summary>
        public bool CheckingDisabled { get; internal set; }

        /// <summary>应答码。</summary>
        public DnsResponseCode ResponseCode { get; internal set; }

        /// <summary>SRV 记录集（应答节序；null = 无）。</summary>
        public List<DnsSrvRecord>? Srv { get; internal set; }

        /// <summary>地址集（A/AAAA 应答节序；null = 无）。</summary>
        public List<IPAddress>? Addresses { get; internal set; }

        /// <summary>CNAME 链（应答节序；null = 无）。</summary>
        public List<string>? Cnames { get; internal set; }

        /// <summary>保留记录 TTL 最小值（无保留记录时为 uint.MaxValue——调用方勿用）。</summary>
        public uint MinTtl { get; internal set; } = uint.MaxValue;
    }

    /// <summary>解析应答报文（结构解析——语义对账 [QR/ID/RCODE] 归调用方）。
    /// 头部与记录前缀走生成 codec 读（零手写偏移）；名字/节游标为结构性推进。</summary>
    /// <exception cref="IOException">报文畸形（长度/指针环/保留位）。</exception>
    public static Message Decode(ReadOnlySpan<byte> message)
    {
        if (message.Length < DnsMessageHeaderCodec.StructSize) throw new IOException("DNS 应答过短（不足头部）。");
        var header = DnsMessageHeaderCodec.Read(message);

        var result = new Message
        {
            Id = header.Id,
            Truncated = (header.Flags & DnsHeaderFlags.Truncated) != 0,
            AuthenticatedData = (header.Flags & DnsHeaderFlags.AuthenticatedData) != 0,
            CheckingDisabled = (header.Flags & DnsHeaderFlags.CheckingDisabled) != 0,
            ResponseCode = (DnsResponseCode)(header.Flags & DnsHeaderFlags.ResponseCodeMask),
        };

        var pos = DnsMessageHeaderCodec.StructSize;

        // 问题节跳过（名 + QTYPE + QCLASS）
        for (var i = 0; i < header.QuestionCount; i++)
        {
            pos = ReadName(message, pos, Span<char>.Empty, out _);
            pos = Advance(message, pos, 4);
        }

        // 应答节——保留 A/AAAA/SRV/CNAME
        for (var i = 0; i < header.AnswerCount; i++)
        {
            pos = ReadName(message, pos, Span<char>.Empty, out _);
            pos = Advance(message, pos, DnsRecordPrefixCodec.StructSize);
            var prefix = DnsRecordPrefixCodec.Read(message[(pos - DnsRecordPrefixCodec.StructSize)..]);
            if (pos + prefix.RdLength > message.Length) throw new IOException("DNS 应答畸形（RDATA 越界）。");
            DecodeRdata(result, prefix.Type, prefix.Ttl, message.Slice(pos, prefix.RdLength), message, pos);
            pos += prefix.RdLength;
        }

        // authority + additional 节跳过（stub 只消费应答节——v1 边界；OPT 伪记录同在此跳过）
        for (var i = 0; i < header.AuthorityCount + header.AdditionalCount; i++)
        {
            pos = ReadName(message, pos, Span<char>.Empty, out _);
            pos = Advance(message, pos, 8);   // TYPE + CLASS + TTL
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
            pos = Advance(message, pos, 2 + rdLength);
        }

        return result;
    }

    /// <summary>读名字（压缩指针 0xC0 跟随——跳转上限 + 单调回指守卫防环；dest 空 = 仅推进不物化）。</summary>
    /// <returns>原始位置上名字之后的偏移（首个指针处 +2，或名尾）。</returns>
    /// <exception cref="IOException">指针环/越界/保留位/名超长。</exception>
    public static int ReadName(ReadOnlySpan<byte> message, int offset, Span<char> dest, out int written)
    {
        var next = -1;
        var pos = offset;
        var jumps = 0;
        written = 0;
        var materialize = dest.Length > 0;
        while (true)
        {
            if ((uint)pos >= (uint)message.Length) throw new IOException("DNS 应答畸形（名字越界）。");
            var b = message[pos];
            if ((b & 0xC0) == 0xC0)
            {
                if (pos + 1 >= message.Length) throw new IOException("DNS 应答畸形（指针截断）。");
                var target = ((b & 0x3F) << 8) | message[pos + 1];
                if (next < 0) next = pos + 2;
                if (target >= pos) throw new IOException("DNS 应答畸形（指针非回指——环）。");
                if (++jumps > MaxPointerJumps) throw new IOException("DNS 应答畸形（指针跳转超限）。");
                pos = target;
                continue;
            }
            if ((b & 0xC0) != 0) throw new IOException("DNS 应答畸形（保留 label 位型）。");
            if (b == 0) { pos++; break; }

            var labelLen = b;
            if (pos + 1 + labelLen > message.Length) throw new IOException("DNS 应答畸形（label 越界）。");
            if (materialize)
            {
                if (written > 0)
                {
                    if (written >= dest.Length) throw new IOException("DNS 名超解析上限。");
                    dest[written++] = '.';
                }
                if (written + labelLen > dest.Length) throw new IOException("DNS 名超解析上限。");
                for (var i = 0; i < labelLen; i++)
                {
                    var c = message[pos + 1 + i];
                    if (c > 127) throw new IOException("DNS 名含非 ASCII 字节。");
                    dest[written++] = (char)c;
                }
            }
            pos += 1 + labelLen;
        }
        if (next < 0) next = pos;
        return next;
    }

    // ═══════════════ 内部 ═══════════════

    /// <summary>应答节单条 RDATA 解码（保留四型；其余/长度违约跳过——容错不中断整报文）。
    /// <paramref name="rdataOffset"/> = RDATA 在整报文中的全局偏移（压缩指针以整报文为坐标系）。</summary>
    private static void DecodeRdata(Message result, ushort type, uint ttl, ReadOnlySpan<byte> rdata,
        ReadOnlySpan<byte> message, int rdataOffset)
    {
        switch ((DnsRecordType)type)
        {
            case DnsRecordType.A when rdata.Length == 4:
                (result.Addresses ??= []).Add(new IPAddress(rdata));
                result.MinTtl = Math.Min(result.MinTtl, ttl);
                break;
            case DnsRecordType.AAAA when rdata.Length == 16:
                (result.Addresses ??= []).Add(new IPAddress(rdata));
                result.MinTtl = Math.Min(result.MinTtl, ttl);
                break;
            case DnsRecordType.SRV when rdata.Length >= 7:
            {
                var priority = BinaryPrimitives.ReadUInt16BigEndian(rdata);
                var weight = BinaryPrimitives.ReadUInt16BigEndian(rdata[2..]);
                var port = BinaryPrimitives.ReadUInt16BigEndian(rdata[4..]);
                var target = ReadNameString(message, rdataOffset + 6);
                if (target is not null)
                {
                    (result.Srv ??= []).Add(new DnsSrvRecord(target, priority, weight, port));
                    result.MinTtl = Math.Min(result.MinTtl, ttl);
                }
                break;
            }
            case DnsRecordType.CNAME:
            {
                var target = ReadNameString(message, rdataOffset);
                if (target is not null)
                {
                    (result.Cnames ??= []).Add(target);
                    result.MinTtl = Math.Min(result.MinTtl, ttl);
                }
                break;
            }
        }
    }

    /// <summary>从整报文全局偏移读名并物化（畸形 = null——记录跳过，容错不中断）。</summary>
    private static string? ReadNameString(ReadOnlySpan<byte> message, int offset)
    {
        Span<char> name = stackalloc char[MaxNameWireBytes];
        try
        {
            ReadName(message, offset, name, out var written);
            return new string(name[..written]);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>推进并越界校验。</summary>
    private static int Advance(ReadOnlySpan<byte> message, int pos, int count)
    {
        if (pos + count > message.Length) throw new IOException("DNS 应答畸形（节越界）。");
        return pos + count;
    }
}
