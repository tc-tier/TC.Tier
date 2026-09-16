using System.Buffers.Binary;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Primitives;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

/// <summary>
/// 握手帧编解码与协商规则契约测试（spec-11 §1.4/§2.2/§5——字节级布局锁死 + 纯规则）。
/// </summary>
public class HandshakeCodecTests
{
    private const ulong TestNonce = 0x0102030405060708UL;
    private const uint ClusterTag = 0x54435431U;   // "TCT1"

    // ══ Init ══

    [Fact]
    public void Init_写入字节级布局锁死()
    {
        var id = NodeId.Parse("000102030405060708090a0b0c0d0e0f");
        var payload = new byte[HandshakeCodec.InitPayloadSize];

        HandshakeCodec.WriteInit(payload, id, 1, 1, HandshakeFeatures.Keepalive | HandshakeFeatures.UdpEndpoint, ClusterTag, TestNonce, HandshakeSecurity.Plaintext);

        payload[0].Should().Be(0x00);                       // NodeId 原序首字节（hex32 = 00 01 02 … 0f）
        payload[15].Should().Be(0x0f);                      // 原序尾字节——字节原序直拷（spec-12 §2）
        payload[16].Should().Be(1);                          // MinVersion
        payload[17].Should().Be(1);                          // MaxVersion
        payload[18].Should().Be(0x03);                       // Features = bit0|bit1
        BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(19)).Should().Be(ClusterTag);   // 集群归属
        BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(23)).Should().Be(TestNonce);
        payload[31].Should().Be(HandshakeSecurity.Plaintext);
    }

    [Fact]
    public void Init_往返_字段一致()
    {
        var id = NodeId.NewRandom();
        var payload = new byte[HandshakeCodec.InitPayloadSize];

        HandshakeCodec.WriteInit(payload, id, 1, 3, HandshakeFeatures.Security, ClusterTag, TestNonce, HandshakeSecurity.KeyPair);
        var init = HandshakeCodec.ReadInit(payload);

        init.NodeId.Should().Be(id);
        init.MinVersion.Should().Be(1);
        init.MaxVersion.Should().Be(3);
        init.Features.Should().Be(HandshakeFeatures.Security);
        init.Nonce.Should().Be(TestNonce);
        init.Security.Should().Be(HandshakeSecurity.KeyPair);
    }

    [Fact]
    public void Init_载荷长度不符_读抛格式异常()
    {
        var act = () => HandshakeCodec.ReadInit(new byte[HandshakeCodec.InitPayloadSize - 1]);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Init_保留特性位置位_写抛参数异常()
    {
        var payload = new byte[HandshakeCodec.InitPayloadSize];
        // 保留区现为 bit4-7（二期-H2：bit3 已收编为 Trace 特性位——0x08 通告合法）
        var act = () => HandshakeCodec.WriteInit(payload, NodeId.NewRandom(), 1, 1, 0xF0, ClusterTag, TestNonce, HandshakeSecurity.Plaintext);
        act.Should().Throw<ArgumentException>();
        var ok = () => HandshakeCodec.WriteInit(new byte[HandshakeCodec.InitPayloadSize], NodeId.NewRandom(), 1, 1, HandshakeFeatures.Trace, ClusterTag, TestNonce, HandshakeSecurity.Plaintext);
        ok.Should().NotThrow("Trace（bit3）已在 v1 位清单内");
    }

    [Fact]
    public void Init_版本区间倒挂_写抛参数异常_读抛格式异常()
    {
        var payload = new byte[HandshakeCodec.InitPayloadSize];
        var actWrite = () => HandshakeCodec.WriteInit(payload, NodeId.NewRandom(), 3, 1, 0, ClusterTag, TestNonce, HandshakeSecurity.Plaintext);
        actWrite.Should().Throw<ArgumentException>();

        payload[16] = 3;
        payload[17] = 1;
        var actRead = () => HandshakeCodec.ReadInit(payload);
        actRead.Should().Throw<FormatException>();
    }

    [Fact]
    public void Init_未定义安全形态_写抛_读抛()
    {
        var payload = new byte[HandshakeCodec.InitPayloadSize];
        var actWrite = () => HandshakeCodec.WriteInit(payload, NodeId.NewRandom(), 1, 1, 0, ClusterTag, TestNonce, 0x7F);
        actWrite.Should().Throw<FormatException>();

        payload[31] = 0x7F;   // 绕过写侧校验直接构造——读侧防线
        var actRead = () => HandshakeCodec.ReadInit(payload);
        actRead.Should().Throw<FormatException>();
    }

    // ══ Ack ══

    [Fact]
    public void Ack_写入字节级布局锁死()
    {
        var id = NodeId.Parse("000102030405060708090a0b0c0d0e0f");
        var payload = new byte[HandshakeCodec.AckPayloadSize];

        HandshakeCodec.WriteAck(payload, id, 1, HandshakeFeatures.None, ClusterTag, TestNonce, HandshakeSecurity.Plaintext);

        payload[0].Should().Be(0x00);
        payload[16].Should().Be(1);                          // 选定版本
        payload[17].Should().Be(0);                          // 交集特性位
        BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(18)).Should().Be(ClusterTag);   // 标签回显
        BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(22)).Should().Be(TestNonce);
        payload[30].Should().Be(HandshakeSecurity.Plaintext);
    }

    [Fact]
    public void Ack_往返_字段一致()
    {
        var id = NodeId.NewRandom();
        var payload = new byte[HandshakeCodec.AckPayloadSize];

        HandshakeCodec.WriteAck(payload, id, 2, HandshakeFeatures.Keepalive, ClusterTag, TestNonce, HandshakeSecurity.Plaintext);
        var ack = HandshakeCodec.ReadAck(payload);

        ack.NodeId.Should().Be(id);
        ack.Version.Should().Be(2);
        ack.Features.Should().Be(HandshakeFeatures.Keepalive);
        ack.Nonce.Should().Be(TestNonce);
        ack.Security.Should().Be(HandshakeSecurity.Plaintext);
    }

    [Fact]
    public void Ack_载荷长度不符_读抛格式异常()
    {
        var act = () => HandshakeCodec.ReadAck(new byte[HandshakeCodec.AckPayloadSize + 1]);
        act.Should().Throw<FormatException>();
    }

    // ══ 协商规则（纯函数）══

    [Fact]
    public void 版本协商_取交集最高版()
    {
        HandshakeCodec.TryNegotiateVersion(1, 1, 1, 3, out var v).Should().BeTrue();
        v.Should().Be(1);

        HandshakeCodec.TryNegotiateVersion(1, 3, 2, 5, out v).Should().BeTrue();
        v.Should().Be(3);          // 交集 [2,3] → 最高 3

        HandshakeCodec.TryNegotiateVersion(1, 2, 2, 5, out v).Should().BeTrue();
        v.Should().Be(2);          // 交集 [2,2]
    }

    [Fact]
    public void 版本协商_无交集_拒绝()
    {
        HandshakeCodec.TryNegotiateVersion(1, 1, 2, 3, out _).Should().BeFalse();
        HandshakeCodec.TryNegotiateVersion(3, 5, 1, 2, out _).Should().BeFalse();
    }

    [Fact]
    public void 特性交集_按位与_单侧开启不生效_保留位天然清除()
    {
        HandshakeCodec.IntersectFeatures(0b0011, 0b0101).Should().Be(0b0001);   // 仅 bit0 双方都开
        HandshakeCodec.IntersectFeatures(HandshakeFeatures.Security, HandshakeFeatures.Keepalive)
            .Should().Be(HandshakeFeatures.None);                               // 通告位不改变行为语义——交集为空
        HandshakeCodec.IntersectFeatures(0b1111, 0b1111).Should().Be(0b1111);   // 交集层不做保留位清洗——发侧校验兜底
    }
}
