using System.Buffers.Binary;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.P2P;

namespace TC.Tier.Core.Net.Tests.P2P;

/// <summary>
/// HyParView message family contract tests (spec-06 §3 semantics × spec-12 §6/§10 —
/// [WireMessage]-generated wire format locked byte-exact: tag dispatch, field order,
/// little-endian scalars, NodeId verbatim 16B; decode defenses = truncated / unknown tag /
/// Count over bound; trailing extension bytes tolerated for forward compatibility).
/// </summary>
public class HyParViewMessageTests
{
    private static NodeId Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[NodeId.Size];
        bytes.Fill(fill);
        return new NodeId(bytes);
    }

    private static byte[] Bytes(params byte[] raw) => raw;

    [Fact]
    public void TagConstants_FamilyUnique_MatchSpec06TypeCodes()
    {
        HyParViewMessageCodec.TagJoinMsg.Should().Be(0x01);
        HyParViewMessageCodec.TagNeighborMsg.Should().Be(0x02);
        HyParViewMessageCodec.TagDisconnectMsg.Should().Be(0x03);
        HyParViewMessageCodec.TagFailMsg.Should().Be(0x04);
        HyParViewMessageCodec.TagShuffleMsg.Should().Be(0x05);
        HyParViewMessageCodec.TagHeartbeatMsg.Should().Be(0x06);
    }

    [Fact]
    public void Encode_EmptyMessages_TagByteOnly()
    {
        HyParViewMessageCodec.Encode(new HeartbeatMsg()).Should().Equal(Bytes(0x06));
        HyParViewMessageCodec.Encode(new DisconnectMsg()).Should().Equal(Bytes(0x03));
    }

    [Fact]
    public void Encode_Join_OriginVerbatimTtlLittleEndian()
    {
        var encoded = HyParViewMessageCodec.Encode(new JoinMsg { Origin = Id(0xAB), Ttl = 0x11223344 });

        encoded.Length.Should().Be(1 + NodeId.Size + 4, "[Tag][Origin 16B verbatim][Ttl 4B LE]");
        encoded[0].Should().Be(0x01);
        encoded.Skip(1).Take(NodeId.Size).Should().OnlyContain(b => b == 0xAB, "NodeId bytes verbatim");
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(1 + NodeId.Size)).Should().Be(0x11223344);
    }

    [Fact]
    public void Encode_Neighbor_PriorityThenCountedView()
    {
        var view = new[] { Id(0x01), Id(0x02), Id(0x03) };
        var encoded = HyParViewMessageCodec.Encode(new NeighborMsg { Priority = true, View = view });

        encoded.Length.Should().Be(1 + 1 + 4 + 3 * NodeId.Size,
            "[Tag][Priority 1B][Count 4B][NodeId × Count]");
        encoded[0].Should().Be(0x02);
        encoded[1].Should().Be(1, "priority flag");
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(2)).Should().Be(3);
        encoded[6..(6 + NodeId.Size)].Should().OnlyContain(b => b == 0x01, "first id verbatim");
    }

    [Fact]
    public void Encode_Shuffle_CountedSample()
    {
        var encoded = HyParViewMessageCodec.Encode(new ShuffleMsg { Sample = [Id(0x0F)] });

        encoded.Length.Should().Be(1 + 4 + NodeId.Size, "[Tag][Count 4B][NodeId × Count]");
        encoded[0].Should().Be(0x05);
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(1)).Should().Be(1);
        encoded[5..].Should().OnlyContain(b => b == 0x0F);
    }

    [Fact]
    public void Encode_Fail_FailedNodeVerbatim()
    {
        var encoded = HyParViewMessageCodec.Encode(new FailMsg { Failed = Id(0xCD) });

        encoded.Length.Should().Be(1 + NodeId.Size, "[Tag][Failed 16B verbatim]");
        encoded[0].Should().Be(0x04);
        encoded.Skip(1).Should().OnlyContain(b => b == 0xCD);
    }

    [Fact]
    public void RoundTrip_AllSixMessages_PreservesFieldsAndDispatch()
    {
        var join = RoundTrip(new JoinMsg { Origin = Id(0x11), Ttl = 4 });
        join.Should().BeOfType<JoinMsg>().Which.Should().Match<JoinMsg>(
            m => m.Origin == Id(0x11) && m.Ttl == 4);

        var neighbor = RoundTrip(new NeighborMsg { Priority = false, View = [Id(0x21), Id(0x22)] });
        // 数组成员逐项结构比较（record 相等性对数组按引用——不用于此处契约断言）
        neighbor.Should().BeOfType<NeighborMsg>().Which.View.Should().Equal(Id(0x21), Id(0x22));
        neighbor.Should().BeOfType<NeighborMsg>().Which.Priority.Should().BeFalse();

        RoundTrip(new NeighborMsg { Priority = true, View = []})
            .Should().BeOfType<NeighborMsg>().Which.View.Should().BeEmpty();

        RoundTrip(new DisconnectMsg()).Should().BeOfType<DisconnectMsg>();

        RoundTrip(new FailMsg { Failed = Id(0x41) })
            .Should().BeOfType<FailMsg>().Which.Failed.Should().Be(Id(0x41));

        RoundTrip(new ShuffleMsg { Sample = [Id(0x51), Id(0x52), Id(0x53)] })
            .Should().BeOfType<ShuffleMsg>().Which.Sample.Should().Equal(Id(0x51), Id(0x52), Id(0x53));

        RoundTrip(new HeartbeatMsg()).Should().BeOfType<HeartbeatMsg>();
    }

    private static HyParViewMessage RoundTrip(HyParViewMessage message)
    {
        var ok = HyParViewMessageCodec.TryDecode(HyParViewMessageCodec.Encode(message), out var decoded);
        ok.Should().BeTrue();
        return decoded!;
    }

    [Fact]
    public void TryDecode_EmptyOrUnknownTag_ReturnsFalse()
    {
        HyParViewMessageCodec.TryDecode([], out _).Should().BeFalse("empty payload");
        HyParViewMessageCodec.TryDecode(Bytes(0x00), out _).Should().BeFalse("tag 0x00 unassigned");
        HyParViewMessageCodec.TryDecode(Bytes(0x07), out _).Should().BeFalse("tag 0x07 unassigned");
        HyParViewMessageCodec.TryDecode(Bytes(0xFF), out _).Should().BeFalse("tag 0xFF reserved");
    }

    [Fact]
    public void TryDecode_TruncatedPayload_ReturnsFalse()
    {
        var join = HyParViewMessageCodec.Encode(new JoinMsg { Origin = Id(0x33), Ttl = 2 });
        HyParViewMessageCodec.TryDecode(join.AsSpan(..^1), out _).Should().BeFalse("Ttl truncated");

        var neighbor = HyParViewMessageCodec.Encode(
            new NeighborMsg { Priority = false, View = [Id(0x34)] });
        HyParViewMessageCodec.TryDecode(neighbor.AsSpan(..^NodeId.Size), out _).Should().BeFalse("list items truncated");
    }

    [Fact]
    public void TryDecode_CountOverBoundOrNegative_ReturnsFalse()
    {
        Span<byte> payload = stackalloc byte[1 + 1 + 4];
        payload[0] = HyParViewMessageCodec.TagNeighborMsg;
        payload[1] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(payload[2..], HyParViewMessage.MaxListCount + 1);
        HyParViewMessageCodec.TryDecode(payload, out _).Should().BeFalse("count over MaxListCount");

        BinaryPrimitives.WriteInt32LittleEndian(payload[2..], -1);
        HyParViewMessageCodec.TryDecode(payload, out _).Should().BeFalse("negative count");
    }

    [Fact]
    public void TryDecode_TrailingExtensionBytes_ToleratedForForwardCompatibility()
    {
        var join = HyParViewMessageCodec.Encode(new JoinMsg { Origin = Id(0x55), Ttl = 6 });
        var extended = join.Concat(Bytes(0xDE, 0xAD, 0xBE)).ToArray();

        HyParViewMessageCodec.TryDecode(extended, out var decoded).Should().BeTrue(
            "known tag decodes its known prefix — future-added fields are extension bytes");
        decoded.Should().Be(new JoinMsg { Origin = Id(0x55), Ttl = 6 });
    }
}
