using System.Buffers.Binary;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Primitives;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

/// <summary>
/// 帧编解码契约测试（spec-11 §1——16B 帧头字节级布局锁死 + CRC 拒绝语义）。
/// </summary>
public class FrameCodecTests
{
    private static readonly byte[] Payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42];

    private static byte[] EncodeFrame(byte kind, byte channel, byte protocol, byte[] payload)
    {
        var frame = new byte[FrameCodec.HeaderSize + payload.Length];
        FrameCodec.Encode(kind, channel, protocol, payload, frame);
        return frame;
    }

    // ══ 常量与线格式布局（字节级锁死——线格式即标准）══

    [Fact]
    public void 常量_与spec12一致()
    {
        FrameCodec.HeaderSize.Should().Be(16);
        FrameCodec.CurrentHeaderVersion.Should().Be(1);
        FrameCodec.MaxPayloadLength.Should().Be(16u * 1024 * 1024);
        ChannelIds.Datagram.Should().Be(0x00);
        ChannelIds.Management.Should().Be(0xFF);
        ChannelIds.IsStream(0x01).Should().BeTrue();
        ChannelIds.IsStream(0xFE).Should().BeTrue();
        ChannelIds.IsStream(0x00).Should().BeFalse();
        ChannelIds.IsStream(0xFF).Should().BeFalse();
        ProtocolIds.Management.Should().Be(0x00);
        ProtocolIds.Raft.Should().Be(0x01);
        ProtocolIds.HyParView.Should().Be(0x02);
        ProtocolIds.SwarmSync.Should().Be(0x03);
        ProtocolIds.SnapshotStream.Should().Be(0x04);
    }

    [Fact]
    public void 编码_字节级布局_小端与偏移锁死()
    {
        var frame = EncodeFrame(FrameKind.Datagram, ChannelIds.Datagram, ProtocolIds.Raft, Payload);

        // [0..4) PayloadLen 小端
        BinaryPrimitives.ReadUInt32LittleEndian(frame).Should().Be((uint)Payload.Length);
        // [4..8) Kind / Channel / Protocol / Version
        frame[4].Should().Be(FrameKind.Datagram);
        frame[5].Should().Be(ChannelIds.Datagram);
        frame[6].Should().Be(ProtocolIds.Raft);
        frame[7].Should().Be(1);
        // [8..12) HeaderCrc = CRC32C(前 8B) 小端
        BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8))
            .Should().Be(UnifiedCrc.ComputeCrc32C(frame.AsSpan(0, 8)));
        // [12..16) PayloadCrc = CRC32C(载荷) 小端
        BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12))
            .Should().Be(UnifiedCrc.ComputeCrc32C(Payload));
        // 载荷原样
        frame.AsSpan(FrameCodec.HeaderSize, Payload.Length).ToArray().Should().Equal(Payload);
        // 整帧长度 = 头 + 载荷
        frame.Length.Should().Be(FrameCodec.HeaderSize + Payload.Length);
    }

    // ══ 往返（单帧/数据报形态）══

    [Fact]
    public void 往返_TryDecode_字段与载荷一致()
    {
        var frame = EncodeFrame(FrameKind.HandshakeInit, ChannelIds.Management, ProtocolIds.Management, Payload);

        var ok = FrameCodec.TryDecode(frame, out var header, out var payload);

        ok.Should().BeTrue();
        header.PayloadLength.Should().Be((uint)Payload.Length);
        header.Kind.Should().Be(FrameKind.HandshakeInit);
        header.ChannelId.Should().Be(ChannelIds.Management);
        header.ProtocolId.Should().Be(ProtocolIds.Management);
        header.Version.Should().Be(1);
        payload.ToArray().Should().Equal(Payload);
    }

    [Fact]
    public void 往返_空载荷_合法()
    {
        var frame = EncodeFrame(FrameKind.HandshakeFinal, ChannelIds.Management, ProtocolIds.Management, []);

        var ok = FrameCodec.TryDecode(frame, out var header, out var payload);

        ok.Should().BeTrue();
        header.PayloadLength.Should().Be(0);
        payload.Length.Should().Be(0);
    }

    [Fact]
    public void 往返_未知种类与协议_编解码层透传不拒绝()
    {
        // spec-11 §1.4：未知 Kind = 丢弃+计数（传输层策略）——编解码层必须放行
        var frame = EncodeFrame(0x55, 0x33, 0x44, Payload);

        FrameCodec.TryDecode(frame, out var header, out var unknownPayload).Should().BeTrue();
        unknownPayload.ToArray().Should().Equal(Payload);
        header.Kind.Should().Be(0x55);
        header.ProtocolId.Should().Be(0x44);
    }

    // ══ 两段式（流介质形态）══

    [Fact]
    public void 两段式_读头验头_切载荷验载()
    {
        var frame = EncodeFrame(FrameKind.Datagram, ChannelIds.Datagram, ProtocolIds.HyParView, Payload);

        FrameCodec.TryReadHeader(frame, out var header).Should().BeTrue();
        var payload = frame.AsSpan(FrameCodec.HeaderSize, (int)header.PayloadLength);
        FrameCodec.VerifyPayload(header, payload).Should().BeTrue();
    }

    // ══ 拒绝语义 ══

    [Fact]
    public void 拒绝_源不足16B_读头失败()
    {
        FrameCodec.TryReadHeader(new byte[15], out _).Should().BeFalse();
        FrameCodec.TryReadHeader([], out _).Should().BeFalse();
    }

    [Fact]
    public void 拒绝_头CRC被篡改_读头失败()
    {
        var frame = EncodeFrame(FrameKind.Datagram, ChannelIds.Datagram, ProtocolIds.Raft, Payload);

        frame[2] ^= 0xFF;   // 破坏头 CRC 覆盖区（[0..8)）

        FrameCodec.TryReadHeader(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void 拒绝_版本非1_读头失败()
    {
        // 手工构造 version=2 且 CRC 自洽的帧头——版本字段是协商产物，本 codec 只讲 v1
        var frame = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0);
        frame[7] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), UnifiedCrc.ComputeCrc32C(frame.AsSpan(0, 8)));

        FrameCodec.TryReadHeader(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void 拒绝_载荷长度超上限_读头失败且编码抛()
    {
        // 线上形态：声明长度 > 16MB（CRC 自洽）——读头即拒（防恶意长度）
        var frame = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, FrameCodec.MaxPayloadLength + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), UnifiedCrc.ComputeCrc32C(frame.AsSpan(0, 8)));

        FrameCodec.TryReadHeader(frame, out _).Should().BeFalse();
        var act = () => FrameCodec.GetFrameLength(FrameCodec.MaxPayloadLength + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 拒绝_载荷CRC被篡改_验载失败()
    {
        var frame = EncodeFrame(FrameKind.Datagram, ChannelIds.Datagram, ProtocolIds.Raft, Payload);
        FrameCodec.TryReadHeader(frame, out var header).Should().BeTrue();
        var payload = frame.AsSpan(FrameCodec.HeaderSize, (int)header.PayloadLength);

        payload[0] ^= 0xFF;   // 破坏载荷首字节（头 CRC 不受影响）

        FrameCodec.VerifyPayload(header, payload).Should().BeFalse();
        FrameCodec.TryDecode(frame, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void 拒绝_载荷长度与声明不符_验载失败()
    {
        var frame = EncodeFrame(FrameKind.Datagram, ChannelIds.Datagram, ProtocolIds.Raft, Payload);
        FrameCodec.TryReadHeader(frame, out var header).Should().BeTrue();

        FrameCodec.VerifyPayload(header, Payload.AsSpan(0, 3)).Should().BeFalse();
    }

    [Fact]
    public void 拒绝_整帧字节数不足声明_解码失败()
    {
        var frame = EncodeFrame(FrameKind.Datagram, ChannelIds.Datagram, ProtocolIds.Raft, Payload);

        FrameCodec.TryDecode(frame.AsSpan(0, frame.Length - 1), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void 编码_目标缓冲不足_抛()
    {
        var small = new byte[FrameCodec.HeaderSize + Payload.Length - 1];
        var act = () => FrameCodec.Encode(FrameKind.Datagram, 0, 1, Payload, small);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 编码_载荷超上限_抛()
    {
        // 上限 +1 的参数校验发生在分配之前（长度检查先行）
        var act = () => FrameCodec.GetFrameLength(FrameCodec.MaxPayloadLength + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ══ 缓冲复用编码 ══

    [Fact]
    public void 缓冲复用编码_与一次性编码等价_且缓冲增长复用()
    {
        byte[]? buffer = null;
        var view = FrameCodec.Encode(FrameKind.Datagram, ChannelIds.Datagram, ProtocolIds.Raft, Payload, ref buffer);

        view.ToArray().Should().Equal(EncodeFrame(FrameKind.Datagram, ChannelIds.Datagram, ProtocolIds.Raft, Payload));
        buffer.Should().NotBeNull();

        // 小载荷复用同一缓冲（不缩容）
        byte[] one = [0x01];
        var small = FrameCodec.Encode(FrameKind.Keepalive, ChannelIds.Management, ProtocolIds.Management, one, ref buffer);
        small.Length.Should().Be(FrameCodec.HeaderSize + 1);
        small.ToArray().Should().Equal(EncodeFrame(FrameKind.Keepalive, ChannelIds.Management, ProtocolIds.Management, one));
    }

    // ══ 16MB 边界 ══

    [Fact]
    public void 往返_载荷恰为上限()
    {
        var payload = new byte[FrameCodec.MaxPayloadLength];
        payload[0] = 0xAB;
        payload[^1] = 0xCD;
        var frame = new byte[FrameCodec.HeaderSize + payload.Length];
        FrameCodec.Encode(FrameKind.StreamData, 0x07, ProtocolIds.SnapshotStream, payload, frame);

        FrameCodec.TryDecode(frame, out var header, out var decoded).Should().BeTrue();
        header.PayloadLength.Should().Be(FrameCodec.MaxPayloadLength);
        decoded.Length.Should().Be(payload.Length);
        decoded[0].Should().Be(0xAB);
        decoded[^1].Should().Be(0xCD);
    }
}
