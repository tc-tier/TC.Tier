using System.Buffers;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Snapshot swarm block source contract (spec-12 §6.1 — ISwarmBlockSource holder side:
/// geometric block slicing, manifest id routing, out-of-range defense).
/// </summary>
public class SnapshotSwarmBlockSourceTests
{
    private static SnapshotSwarmContent BuildContent(Opaque16 id, int blockSize, params (long, long, byte, byte[])[] entries)
    {
        var writer = new ArrayBufferWriter<byte>();
        foreach (var (index, term, kind, payload) in entries)
        {
            var len = SnapshotBlockizer.EntryFrameLength(payload.Length);
            SnapshotBlockizer.WriteEntryFrame((index, term, kind, payload), writer.GetSpan(len));
            writer.Advance(len);
        }
        var bytes = writer.WrittenSpan.ToArray();
        return new SnapshotSwarmContent(SwarmManifest.Build(bytes, blockSize, id), bytes);
    }

    private static Opaque16 Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(fill);
        return new Opaque16(bytes);
    }

    [Fact]
    public void HasManifest_OnlyOwnId()
    {
        var content = BuildContent(Id(0xAA), 16, (1, 1, RaftEntryKind.Command, new byte[40]));
        var source = new SnapshotSwarmBlockSource(content);

        source.HasManifest(content.Manifest.Id).Should().BeTrue();
        source.HasManifest(Id(0xBB)).Should().BeFalse();
    }

    [Fact]
    public void TryGetBlock_GeometricSlices_MatchContent()
    {
        var content = BuildContent(Id(0xAA), 16, (1, 1, RaftEntryKind.Command, new byte[9]), (2, 1, RaftEntryKind.Command, new byte[20]), (3, 2, RaftEntryKind.Command, new byte[5]));
        var source = new SnapshotSwarmBlockSource(content);

        for (var i = 0; i < content.Manifest.BlockCount; i++)
        {
            var ok = source.TryGetBlock(content.Manifest.Id, i, out var block);
            ok.Should().BeTrue($"block {i} 存在");
            block.Length.Should().Be((int)content.Manifest.BlockLength(i), $"block {i} 长度 = 几何推导");
            block.ToArray().Should().Equal(
                content.Content.AsSpan((int)content.Manifest.BlockOffset(i), (int)content.Manifest.BlockLength(i)).ToArray(),
                $"block {i} 逐字节一致");
        }
    }

    [Fact]
    public void TryGetBlock_LastBlockTruncated()
    {
        var content = BuildContent(Id(0xAA), 16, (1, 1, RaftEntryKind.Command, new byte[9]));   // 单条目帧 = 21 + 9 = 30 字节 → 2 块（末块 14 字节）
        var source = new SnapshotSwarmBlockSource(content);

        content.Manifest.BlockCount.Should().Be(2);
        source.TryGetBlock(content.Manifest.Id, 0, out var first).Should().BeTrue();
        source.TryGetBlock(content.Manifest.Id, 1, out var last).Should().BeTrue();
        first.Length.Should().Be(16);
        last.Length.Should().Be(14, "末块截短——30 字节内容 2 块");
    }

    [Fact]
    public void TryGetBlock_OutOfRangeOrWrongId_False()
    {
        var content = BuildContent(Id(0xAA), 16, (1, 1, RaftEntryKind.Command, new byte[40]));
        var source = new SnapshotSwarmBlockSource(content);

        source.TryGetBlock(content.Manifest.Id, content.Manifest.BlockCount, out _).Should().BeFalse("块号超界");
        source.TryGetBlock(content.Manifest.Id, -1, out _).Should().BeFalse("负块号");
        source.TryGetBlock(Id(0xBB), 0, out _).Should().BeFalse("内容标识不符");
    }
}
