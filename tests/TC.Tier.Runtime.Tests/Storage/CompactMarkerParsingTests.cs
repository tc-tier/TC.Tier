using System.Buffers.Binary;
using TC.Tier.Runtime.Storage.Compact;

namespace TC.Tier.Runtime.Tests.Storage;

public class CompactMarkerParsingTests
{
    [Fact]
    public void TryParseCommitMarker_ValidMarker_DecodesBody()
    {
        var bytes = CreateMarker(new CompactMarkerHeader(CompactType.Full, 2, 0), [17, 18]);

        var parsed = DefaultCompactor.TryParseCommitMarker(
            bytes, out var header, out var segmentIds, out var dispositions);

        parsed.Should().BeTrue();
        header.CompactType.Should().Be(CompactType.Full);
        segmentIds.Should().Equal(17, 18);
        dispositions.Should().BeEmpty();
    }

    [Fact]
    public void TryParseCommitMarker_NegativeCount_ReturnsFalse()
    {
        var bytes = CreateMarker(new CompactMarkerHeader(CompactType.Full, -1, 0), []);

        var parsed = DefaultCompactor.TryParseCommitMarker(
            bytes, out _, out var segmentIds, out var dispositions);

        parsed.Should().BeFalse();
        segmentIds.Should().BeEmpty();
        dispositions.Should().BeEmpty();
    }

    private static byte[] CreateMarker(CompactMarkerHeader header, int[] segmentIds)
    {
        var bytes = new byte[CompactMarkerHeaderCodec.StructSize + segmentIds.Length * sizeof(int)];
        for (int i = 0; i < segmentIds.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(CompactMarkerHeaderCodec.StructSize + i * sizeof(int)),
                segmentIds[i]);

        CompactMarkerHeaderCodec.Write(bytes, in header);
        header.Crc = UnifiedCrc.ComputeCrc32(bytes);
        CompactMarkerHeaderCodec.Write(bytes, in header);
        return bytes;
    }
}
