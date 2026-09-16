using System.Buffers.Binary;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Core.Net.Tests.Swarm;

/// <summary>
/// Swarm fetch-block message family contract tests (spec-12 §6.1 × §10 — [WireMessage]-generated
/// wire format: [Tag][fields in declaration order], Opaque16 verbatim, LE scalars, Data blob
/// with explicit bound; decode defenses = truncated / unknown tag / over-bound).
/// </summary>
public class SwarmMessageTests
{
    private static Opaque16 Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(fill);
        return new Opaque16(bytes);
    }

    [Fact]
    public void TagConstants_Unique()
    {
        SwarmMessageCodec.TagGetBlockReq.Should().Be(0x01);
        SwarmMessageCodec.TagGetBlockResp.Should().Be(0x02);
        SwarmMessageCodec.TagSourceAnnounceMsg.Should().Be(0x03);
        SwarmMessageCodec.TagEntropyProbeReq.Should().Be(0x04);
        SwarmMessageCodec.TagEntropyProbeResp.Should().Be(0x05);
    }

    /// <summary>持有者上报（spec-12 §6.1 增量 S1）：[Tag][ManifestId 16B 原序]——最小消息，
    /// 字段原样往返。</summary>
    [Fact]
    public void Encode_SourceAnnounce_ManifestIdVerbatim()
    {
        var encoded = SwarmMessageCodec.Encode(new SourceAnnounceMsg { ManifestId = Id(0x77) });

        encoded.Length.Should().Be(1 + 16, "[Tag][ManifestId 16B verbatim]");
        encoded[0].Should().Be(0x03);
        encoded.Skip(1).Take(16).Should().OnlyContain(b => b == 0x77);
    }

    [Fact]
    public void RoundTrip_SourceAnnounce_PreservesManifestId()
    {
        var announce = new SourceAnnounceMsg { ManifestId = Id(0x55) };
        SwarmMessageCodec.TryDecode(SwarmMessageCodec.Encode(announce), out var decoded).Should().BeTrue();
        decoded.Should().BeOfType<SourceAnnounceMsg>().Which.ManifestId.Should().Be(Id(0x55));
    }

    [Fact]
    public void Encode_GetBlockReq_ManifestIdVerbatimBlockIndexLittleEndian()
    {
        var encoded = SwarmMessageCodec.Encode(new GetBlockReq { ManifestId = Id(0xAB), BlockIndex = 0x1122334455667788 });

        encoded.Length.Should().Be(1 + 16 + 8, "[Tag][ManifestId 16B verbatim][BlockIndex 8B LE]");
        encoded[0].Should().Be(0x01);
        encoded.Skip(1).Take(16).Should().OnlyContain(b => b == 0xAB);
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(17)).Should().Be(0x1122334455667788);
    }

    [Fact]
    public void Encode_GetBlockResp_HasBlockPrefixThenBlob()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var encoded = SwarmMessageCodec.Encode(new GetBlockResp
        {
            ManifestId = Id(0xCD), BlockIndex = 5, HasBlock = true, Data = data,
        });

        encoded.Length.Should().Be(1 + 16 + 8 + 1 + 4 + 3, "[Tag][ManifestId][BlockIndex][HasBlock 1B][Len 4B][Data]");
        encoded[0].Should().Be(0x02);
        encoded[25].Should().Be(1, "has-block flag");
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(26)).Should().Be(3);
        encoded[30..].Should().Equal(data);
    }

    [Fact]
    public void RoundTrip_BothMessages_PreserveFields()
    {
        var req = new GetBlockReq { ManifestId = Id(0x11), BlockIndex = 42 };
        SwarmMessageCodec.TryDecode(SwarmMessageCodec.Encode(req), out var reqDecoded).Should().BeTrue();
        reqDecoded.Should().BeOfType<GetBlockReq>().Which.ManifestId.Should().Be(Id(0x11));
        reqDecoded.Should().BeOfType<GetBlockReq>().Which.BlockIndex.Should().Be(42);

        var resp = new GetBlockResp { ManifestId = Id(0x22), BlockIndex = 7, HasBlock = false, Data = [] };
        SwarmMessageCodec.TryDecode(SwarmMessageCodec.Encode(resp), out var respDecoded).Should().BeTrue();
        respDecoded.Should().BeOfType<GetBlockResp>().Which.Should().Match<GetBlockResp>(
            m => m.ManifestId == Id(0x22) && m.BlockIndex == 7 && !m.HasBlock && m.Data.Length == 0);
    }

    [Fact]
    public void TryDecode_EmptyUnknownTagTruncatedOverBound_ReturnsFalse()
    {
        SwarmMessageCodec.TryDecode([], out _).Should().BeFalse();
        SwarmMessageCodec.TryDecode(new byte[] { 0x00 }, out _).Should().BeFalse();
        SwarmMessageCodec.TryDecode(new byte[] { 0x03 }, out _).Should().BeFalse("tag 0x03 = SourceAnnounceMsg——缺 ManifestId 截断");

        var req = SwarmMessageCodec.Encode(new GetBlockReq { ManifestId = Id(0x33), BlockIndex = 1 });
        SwarmMessageCodec.TryDecode(req.AsSpan(..^1), out _).Should().BeFalse("BlockIndex truncated");

        Span<byte> overBound = stackalloc byte[1 + 16 + 8 + 1 + 4];
        overBound[0] = SwarmMessageCodec.TagGetBlockResp;
        overBound[25] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(overBound[26..], SwarmMessage.MaxBlockBytes + 1);
        SwarmMessageCodec.TryDecode(overBound, out _).Should().BeFalse("Data length over bound");
    }
}
