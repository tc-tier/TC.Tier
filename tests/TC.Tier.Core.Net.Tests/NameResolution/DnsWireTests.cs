using System.Buffers.Binary;
using System.Net;
using System.Text;
using FluentAssertions;
using TC.Tier.Core.Net.NameResolution;
using Xunit;

namespace TC.Tier.Core.Net.Tests.NameResolution;

/// <summary>
/// DNS wire 编解码单元 fixture（#498 验收判据 1/7）——查询构造字节级 golden、压缩指针
/// （含防环）、SRV/A/CNAME RDATA、EDNS0 OPT+DO、解析确定性（同应答逐字节等价）。
/// </summary>
public sealed class DnsWireTests
{
    /// <summary>参考查询：a.example.com A + EDNS0(4096, DO)——字节级 golden。</summary>
    [Fact]
    public void EncodeQuery_GoldenBytes_WithEdns0AndDo()
    {
        var buffer = new byte[DnsWire.MaxQueryBytes];
        var len = DnsWire.EncodeQuery(buffer, id: 0x1A2B, "a.example.com", DnsRecordType.A,
            ednsPayload: 4096, dnsSecOk: true);

        var expected = new byte[]
        {
            0x1A, 0x2B,             // ID
            0x01, 0x00,             // flags: RD
            0x00, 0x01,             // QDCOUNT
            0x00, 0x00, 0x00, 0x00, // AN/NS/AR
            0x00, 0x01,             // ARCOUNT=1（EDNS0）
            0x01, (byte)'a', 0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            0x03, (byte)'c', (byte)'o', (byte)'m', 0x00,   // a.example.com
            0x00, 0x01, 0x00, 0x01, // QTYPE=A QCLASS=IN
            0x00,                   // OPT: 根名
            0x00, 0x29,             // TYPE=OPT
            0x10, 0x00,             // CLASS=4096（UDP 载荷）
            0x00, 0x00, 0x80, 0x00, // TTL 扩展段：DO=bit15 置位
            0x00, 0x00,             // RDLEN=0
        };
        len.Should().Be(expected.Length);
        buffer.AsSpan(0, len).ToArray().Should().BeEquivalentTo(expected, "查询字节级 golden（含 EDNS0+DO）");
    }

    /// <summary>无 EDNS 查询：ARCOUNT=0。</summary>
    [Fact]
    public void EncodeQuery_WithoutEdns_ArCountZero()
    {
        var buffer = new byte[DnsWire.MaxQueryBytes];
        var len = DnsWire.EncodeQuery(buffer, 1, "x.y", DnsRecordType.SRV, ednsPayload: 0, dnsSecOk: false);
        BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(10)).Should().Be(0, "ARCOUNT=0");
        var msg = DnsWire.Decode(buffer.AsSpan(0, len));   // 查询形态亦可结构解码
        msg.Should().NotBeNull();
    }

    /// <summary>压缩指针解码：应答属主名 = 指针指回问题节名（0xC00C）；SRV target 内嵌压缩同样解出。</summary>
    [Fact]
    public void Decode_CompressionPointer_ResolvesToQuestionName()
    {
        // 问题节：srv.example.com（自偏移 12 起）
        var message = BuildQuery(name: "srv.example.com", type: DnsRecordType.SRV);
        // 应答：属主=指针 0xC00C，SRV rdata = p/w/port + 指针 0xC00C（target 压缩回问题名）
        var rdata = new byte[] { 0x00, 0x0A, 0x00, 0x14, 0x1F, 0x90, 0xC0, 0x0C };
        var response = ResponseBuilder(message, id: 0x0BEE, flags: 0x8180, answers: new[]
        {
            (type: DnsRecordType.SRV, ttl: 60u, rdata),
        });

        var msg = DnsWire.Decode(response);
        msg.Id.Should().Be(0x0BEE);
        msg.Srv.Should().NotBeNull();
        msg.Srv![0].Should().Be(new DnsSrvRecord("srv.example.com", 10, 20, 8080), "指针回问题名 + 压缩 target");
        msg.MinTtl.Should().Be(60);
    }

    /// <summary>压缩指针环 → 拒收（IOException——防环守卫）。</summary>
    [Fact]
    public void Decode_PointerCycle_Rejected()
    {
        // 头 12B + QDCOUNT=1（问题节承载环字节——节计数为 0 时 Decode 不进节循环）
        var buffer = new byte[16];
        buffer[4] = 0; buffer[5] = 1;   // QDCOUNT=1
        buffer[12] = 0xC0; buffer[13] = 0x0E;   // 指向 14
        buffer[14] = 0xC0; buffer[15] = 0x0C;   // 指向 12——环
        var act = () => DnsWire.Decode(buffer);
        act.Should().Throw<IOException>().Which.Message.Should().Contain("环");
    }

    /// <summary>SRV 多记录 + weight：保持应答节顺序（确定性）。</summary>
    [Fact]
    public void Decode_SrvMultiRecord_PreservesOrderAndWeight()
    {
        var query = BuildQuery("app.prod.svc", DnsRecordType.SRV);
        var rdata1 = new byte[] { 0x00, 0x01, 0x00, 0x64, 0x1F, 0x40, 0x03, (byte)'a', (byte)'b', (byte)'c', 0x00 };
        var rdata2 = new byte[] { 0x00, 0x01, 0x00, 0x32, 0x1F, 0x41, 0x03, (byte)'x', (byte)'y', (byte)'z', 0x00 };
        var response = ResponseBuilder(query, 7, 0x8180, new[]
        {
            (DnsRecordType.SRV, 120u, rdata1),
            (DnsRecordType.SRV, 120u, rdata2),
        });

        var msg = DnsWire.Decode(response);
        msg.Srv.Should().HaveCount(2);
        msg.Srv![0].Weight.Should().Be(100);
        msg.Srv![0].Target.Should().Be("abc");
        msg.Srv![1].Weight.Should().Be(50);
        msg.Srv![1].Target.Should().Be("xyz");
        msg.MinTtl.Should().Be(120);
    }

    /// <summary>解析确定性：同应答解析两次 → 结果逐字节等价。</summary>
    [Fact]
    public void Decode_Deterministic_SameAnswerSameResult()
    {
        var query = BuildQuery("h.test", DnsRecordType.A);
        var response = ResponseBuilder(query, 9, 0x8180, new[]
        {
            (DnsRecordType.A, 30u, new byte[] { 192, 168, 1, 7 }),
        });

        var first = DnsWire.Decode(response);
        var second = DnsWire.Decode(response);
        first.Addresses![0].Should().Be(second.Addresses![0]);
        first.MinTtl.Should().Be(second.MinTtl);
        first.Addresses![0].Should().Be(IPAddress.Parse("192.168.1.7"));
    }

    /// <summary>AAAA + CNAME 混合应答解码。</summary>
    [Fact]
    public void Decode_AaaaAndCname()
    {
        var query = BuildQuery("v6.test", DnsRecordType.AAAA);
        var cnameRdata = new byte[] { 0x04, (byte)'r', (byte)'e', (byte)'a', (byte)'l', 0x00 };   // "real"=4 字符
        var aaaaRdata = new byte[] { 0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
        var response = ResponseBuilder(query, 3, 0x8180, new[]
        {
            (DnsRecordType.CNAME, 15u, cnameRdata),
            (DnsRecordType.AAAA, 45u, aaaaRdata),
        });

        var msg = DnsWire.Decode(response);
        msg.Cnames.Should().Equal("real");
        msg.Addresses.Should().ContainSingle().Which.Should().Be(IPAddress.Parse("2001:db8::1"));
        msg.MinTtl.Should().Be(15, "聚合 MinTtl 跨保留记录取最小");
    }

    // ═══════════════ fixture 工具 ═══════════════

    /// <summary>构造标准查询字节（header + 问题节）。</summary>
    internal static byte[] BuildQuery(string name, DnsRecordType type)
    {
        var buffer = new byte[DnsWire.MaxQueryBytes];
        var len = DnsWire.EncodeQuery(buffer, 1, name, type, ednsPayload: 0, dnsSecOk: false);
        return buffer[..len];
    }

    /// <summary>由查询构造应答：回显头部+问题节，追加应答记录（属主名 = 压缩指针 0xC00C 指回问题名）。</summary>
    internal static byte[] ResponseBuilder(byte[] query, ushort id, ushort flags,
        (DnsRecordType Type, uint Ttl, byte[] Rdata)[] answers)
    {
        // 问题节 = 查询的 [12, 头 + 名 + 4)
        var qdEnd = 12;
        while (query[qdEnd] != 0) qdEnd += 1 + query[qdEnd];
        qdEnd += 5;   // 根 0 + QTYPE(2) + QCLASS(2)

        using var ms = new MemoryStream();
        ms.Write(query, 0, qdEnd);   // 头部（ID/flags 稍后覆写）+ 问题节
        var buffer = ms.GetBuffer();

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6), (ushort)answers.Length);

        foreach (var (type, ttl, rdata) in answers)
        {
            ms.WriteByte(0xC0); ms.WriteByte(0x0C);   // 属主名：压缩指针 → 问题名
            WriteU16(ms, (ushort)type);
            WriteU16(ms, 1);                          // IN
            WriteU32(ms, ttl);
            WriteU16(ms, (ushort)rdata.Length);
            ms.Write(rdata);
        }
        return ms.ToArray();
    }

    private static void WriteU16(MemoryStream ms, ushort v)
    {
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }

    private static void WriteU32(MemoryStream ms, uint v)
    {
        ms.WriteByte((byte)(v >> 24));
        ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }
}
