using System.Buffers;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Snapshot blockizer contract (spec-12 §6.1 multi-source enhancement — snapshot entry stream
/// → content-addressed block list): manifest id round-trip, entry framing round-trip, build
/// content geometry/checksums, truncation defense.
/// </summary>
public class SnapshotSwarmTests
{
    // ═══ manifest id（快照覆盖点 ↔ 内容标识）═══

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(4L)]
    [InlineData(0x0102030405060708L)]
    [InlineData(long.MaxValue)]
    public void ManifestId_RoundTripsThroughSnapshotIndex(long index)
    {
        SnapshotBlockizer.SnapshotIndexOf(SnapshotBlockizer.ManifestIdFor(index)).Should().Be(index);
    }

    [Fact]
    public void ManifestId_DeterministicByteLayout()
    {
        // 字节 0..7 = index 小端，字节 8..15 = 零——锁死（holder 与下载方从同一 N₀ 推同一 Id）
        SnapshotBlockizer.ManifestIdFor(0x0102030405060708).ToString()
            .Should().Be("08070605040302010000000000000000");
        SnapshotBlockizer.ManifestIdFor(0).ToString().Should().Be(new string('0', 32));
    }

    [Fact]
    public void ManifestIdFor_NegativeIndex_Throws()
    {
        Action act = () => SnapshotBlockizer.ManifestIdFor(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ═══ 条目帧化往返 ═══

    [Fact]
    public async Task EntryFrame_RoundTripsThroughParse()
    {
        var entries = new (long, long, byte, byte[])[]
        {
            (1, 1, RaftEntryKind.Command, new byte[] { 0xAA }),
            (2, 1, RaftEntryKind.Command, new byte[] { 1, 2, 3 }),
            (3, 2, RaftEntryKind.Command, Array.Empty<byte>()),
            (4, 2, RaftEntryKind.Command, new byte[64]),
        };

        var writer = new ArrayBufferWriter<byte>();
        foreach (var e in entries)
        {
            var len = SnapshotBlockizer.EntryFrameLength(e.Item4.Length);
            SnapshotBlockizer.WriteEntryFrame((e.Item1, e.Item2, e.Item3, e.Item4), writer.GetSpan(len));
            writer.Advance(len);
        }

        var parsed = new List<(long, long, byte, byte[])>();
        await foreach (var e in SnapshotBlockizer.ParseEntriesAsync(writer.WrittenMemory))
            parsed.Add((e.Index, e.Term, e.Kind, e.Content.ToArray()));

        parsed.Should().HaveCount(entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            parsed[i].Item1.Should().Be(entries[i].Item1, "index 逐条一致");
            parsed[i].Item2.Should().Be(entries[i].Item2, "term 逐条一致");
            parsed[i].Item3.Should().Be(entries[i].Item3, "kind 逐条一致");
            parsed[i].Item4.Should().Equal(entries[i].Item4, "content 逐字节一致");
        }
    }

    [Fact]
    public void Parse_TruncatedContent_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        var len = SnapshotBlockizer.EntryFrameLength(8);
        SnapshotBlockizer.WriteEntryFrame((1, 1, RaftEntryKind.Command, new byte[8]), writer.GetSpan(len));
        writer.Advance(len);

        // 尾部截断 1 字节——header 完整但 payload 越界
        var truncated = writer.WrittenMemory[..^1];
        var act = async () =>
        {
            await foreach (var _ in SnapshotBlockizer.ParseEntriesAsync(truncated))
            {
            }
        };
        act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Parse_TruncatedHeader_Throws()
    {
        var bytes = new byte[SnapshotEntryHeaderCodec.StructSize - 1];
        var act = async () =>
        {
            await foreach (var _ in SnapshotBlockizer.ParseEntriesAsync(bytes))
            {
            }
        };
        act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ═══ 块化构建（holder 侧）═══

    private static async Task<InMemoryRaftStore> StoreWithEntriesAsync(int count)
    {
        var store = new InMemoryRaftStore();
        await store.InitializeAsync();
        var entries = new (long, byte, ReadOnlyMemory<byte>)[count];
        for (var i = 0; i < count; i++)
        {
            var payload = new byte[37];
            payload[0] = (byte)i;
            entries[i] = (i < count / 2 ? 1 : 2, RaftEntryKind.Command, payload);
        }
        (await store.AppendAsync(0, entries)).Should().Be(count);
        await store.WaitForPersistedAsync(count);
        return store;
    }

    [Fact]
    public async Task BuildContent_ManifestGeometryAndRoundTrip()
    {
        var store = await StoreWithEntriesAsync(10);
        var content = await SnapshotBlockizer.BuildContentAsync(store, 10, blockSize: 32);

        content.Manifest.Id.Should().Be(SnapshotBlockizer.ManifestIdFor(10), "Id = 覆盖点承载");
        content.Manifest.BlockCount.Should().Be((content.Content.Length + 31) / 32);
        content.Manifest.TotalBytes.Should().Be(content.Content.Length);

        // 逐块校验和与几何：Content 就是 SwarmManifest.Build 的输入——重放还原一致
        var replayed = new List<(long, long, byte, byte[])>();
        await foreach (var e in SnapshotBlockizer.ParseEntriesAsync(content.Content))
            replayed.Add((e.Index, e.Term, e.Kind, e.Content.ToArray()));
        replayed.Should().HaveCount(10);
        replayed[0].Item1.Should().Be(1);
        replayed[9].Item1.Should().Be(10);

        // 末块截短：末块长度 = TotalBytes - (BlockCount-1)*BlockSize
        var last = content.Manifest.BlockCount - 1;
        content.Manifest.BlockLength(last).Should()
            .Be(content.Manifest.TotalBytes - content.Manifest.BlockOffset(last));
    }

    [Fact]
    public async Task BuildContent_EmptySnapshot_ZeroBlocks()
    {
        var store = new InMemoryRaftStore();
        await store.InitializeAsync();
        var content = await SnapshotBlockizer.BuildContentAsync(store, 0, blockSize: 32);

        content.Manifest.TotalBytes.Should().Be(0);
        content.Manifest.BlockCount.Should().Be(0);
        content.Content.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildContent_InvalidBlockSize_Throws()
    {
        var store = new InMemoryRaftStore();
        await store.InitializeAsync();
        var act = async () => await SnapshotBlockizer.BuildContentAsync(store, 1, blockSize: 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
