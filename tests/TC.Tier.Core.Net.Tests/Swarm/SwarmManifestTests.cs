using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Core.Net.Tests.Swarm;

/// <summary>
/// Swarm manifest contract tests (spec-12 §6.1 — block inventory: geometric block
/// localization, per-block CRC32C checksums, last-block truncation, verification).
/// </summary>
public class SwarmManifestTests
{
    private static Opaque16 Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(fill);
        return new Opaque16(bytes);
    }

    private static byte[] Data(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i * 7);
        return data;
    }

    [Fact]
    public void Build_BlockCountAndLocalization_LastBlockTruncated()
    {
        var data = Data(10);
        var manifest = SwarmManifest.Build(data, blockSize: 4, id: Id(0xAA));

        manifest.TotalBytes.Should().Be(10);
        manifest.BlockCount.Should().Be(3, "ceil(10/4)");
        manifest.BlockOffset(0).Should().Be(0);
        manifest.BlockOffset(1).Should().Be(4);
        manifest.BlockLength(0).Should().Be(4);
        manifest.BlockLength(2).Should().Be(2, "last block truncated at TotalBytes");
    }

    [Fact]
    public void Build_VerifyBlock_IntactPassesCorruptedRejected()
    {
        var data = Data(16);
        var manifest = SwarmManifest.Build(data, blockSize: 4, id: Id(0xBB));

        manifest.VerifyBlock(1, data.AsSpan(4, 4)).Should().BeTrue("intact block passes");
        var corrupted = data.AsSpan(4, 4).ToArray();
        corrupted[0] ^= 0xFF;
        manifest.VerifyBlock(1, corrupted).Should().BeFalse("CRC32C mismatch — corrupted block rejected");
        manifest.VerifyBlock(1, data.AsSpan(4, 3)).Should().BeFalse("wrong length rejected");
        manifest.VerifyBlock(99, data.AsSpan(0, 4)).Should().BeFalse("out-of-range index rejected");
    }

    [Fact]
    public void Build_EmptyContent_ZeroBlocks()
    {
        var manifest = SwarmManifest.Build(ReadOnlyMemory<byte>.Empty, blockSize: 4, id: Id(0xCC));
        manifest.TotalBytes.Should().Be(0);
        manifest.BlockCount.Should().Be(0);
        manifest.Checksums.Should().BeEmpty();
    }

    [Fact]
    public void Build_BlockSizeOverWireBound_Throws()
    {
        var act = () => SwarmManifest.Build(Data(8), blockSize: SwarmMessage.MaxBlockBytes + 1, id: Id(0xDD));
        act.Should().Throw<ArgumentOutOfRangeException>("block size above wire defense bound is a malformed request");
    }

    [Fact]
    public void BlockOffset_OutOfRange_Throws()
    {
        var manifest = SwarmManifest.Build(Data(8), blockSize: 4, id: Id(0xEE));
        var act = () => manifest.BlockOffset(2);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
