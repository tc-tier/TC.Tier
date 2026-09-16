using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Snapshot stream transfer scenarios (spec-03 §2/T5 single-source baseline — export via
/// stream session 0x04, import reconstructs the snapshot region byte-exact: the parity
/// contract between source and target stores).
/// </summary>
public class SnapshotStreamTransferTests
{
    private sealed record TransferFixture(InProcessTransportHub Hub, InProcessNode A, InProcessNode B,
        InMemoryRaftStore StoreA, InMemoryRaftStore StoreB, SnapshotStreamTransfer TransferA, SnapshotStreamTransfer TransferB)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
            await Hub.DisposeAsync();
        }
    }

    private static async Task<TransferFixture> CreateAsync()
    {
        var hub = new InProcessTransportHub();
        var a = hub.Register(NodeId.NewRandom());
        var b = hub.Register(NodeId.NewRandom());
        a.Start();
        b.Start();
        var storeA = new InMemoryRaftStore();
        var storeB = new InMemoryRaftStore();
        await storeA.InitializeAsync();
        await storeB.InitializeAsync();
        var transferA = new SnapshotStreamTransfer(a, storeA);
        var transferB = new SnapshotStreamTransfer(b, storeB);
        await transferA.StartAsync();
        await transferB.StartAsync();
        return new TransferFixture(hub, a, b, storeA, storeB, transferA, transferB);
    }

    private static async Task<List<(long, long, byte, byte[])>> CollectAsync(InMemoryRaftStore store, long snapshotIndex)   // CA1859：具体形
    {
        var list = new List<(long, long, byte, byte[])>();
        await foreach (var e in store.ReadSnapshotEntriesAsync(snapshotIndex)) list.Add((e.Index, e.Term, e.Kind, e.Content.ToArray()));
        return list;
    }

    /// <summary>对拍（验收契约——单源产物逐字节一致）：导出 → 导入 → 目标快照区与源逐条一致 + 快照点推进。</summary>
    [Fact]
    public async Task ExportImport_TargetSnapshotRegion_ByteExact()
    {
        await using var fx = await CreateAsync();
        (await fx.StoreA.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (2, RaftEntryKind.Command, new byte[] { 2 }), (2, RaftEntryKind.Command, new byte[] { 3 }), (3, RaftEntryKind.Command, new byte[] { 4 })])).Should().Be(4);
        await fx.StoreA.WaitForPersistedAsync(4);

        await fx.TransferA.ExportSnapshotAsync(fx.B.Self);   // 导出：快照点推进到已持久化尾（实现侧策略）+ 流写
        fx.StoreA.SnapshotIndex.Should().Be(4, "导出前快照点推进到已持久化尾（快照=已持久化日志前缀）");

        await fx.TransferB.ImportSnapshotAsync(fx.A.Self);   // 导入：消费已入站会话

        fx.StoreB.SnapshotIndex.Should().Be(4);
        fx.StoreB.LastLogIndex.Should().Be(4, "导入重锚到快照覆盖点");
        var source = await CollectAsync(fx.StoreA, 4);
        var target = await CollectAsync(fx.StoreB, 4);
        target.Should().HaveCount(4);
        for (var i = 0; i < 4; i++)
        {
            target[i].Item1.Should().Be(source[i].Item1, "index 逐条一致");
            target[i].Item2.Should().Be(source[i].Item2, "term 逐条一致");
            target[i].Item3.Should().Be(source[i].Item3, "kind 逐条一致");
            target[i].Item4.Should().Equal(source[i].Item4, "content 逐字节一致（对拍契约）");
        }
    }

    /// <summary>流式形态：大快照（1000 条 × 1KB）导出导入——逐帧 O(单帧) 搬运行为验证。</summary>
    [Fact]
    public async Task ExportImport_LargeSnapshot_StreamsThrough()
    {
        await using var fx = await CreateAsync();
        var entries = new (long, byte, ReadOnlyMemory<byte>)[1000];
        for (var i = 0; i < 1000; i++)
        {
            var payload = new byte[1024];
            payload[0] = (byte)i;
            entries[i] = (1, RaftEntryKind.Command, payload);
        }
        (await fx.StoreA.AppendAsync(0, entries)).Should().Be(1000);
        await fx.StoreA.WaitForPersistedAsync(1000);

        await fx.TransferA.ExportSnapshotAsync(fx.B.Self);
        await fx.TransferB.ImportSnapshotAsync(fx.A.Self);

        fx.StoreB.LastLogIndex.Should().Be(1000);
        var sample = await CollectAsync(fx.StoreB, 1000);
        sample.Should().HaveCount(1000);
        sample[0].Item4[0].Should().Be(0);
        sample[999].Item4[0].Should().Be((byte)(999 & 0xFF), "末条首字节 = 999 mod 256（定位一致）");
    }

    /// <summary>顺序违规（spec-03 §2 时序契约）：follower 无已入站会话调导入 = 抛（leader 应先导出再握手）。</summary>
    [Fact]
    public async Task Import_WithoutIncomingSession_Throws()
    {
        await using var fx = await CreateAsync();
        await fx.TransferB.Invoking(t => t.ImportSnapshotAsync(fx.A.Self).AsTask())
            .Should().ThrowAsync<InvalidOperationException>("无来源会话——顺序违规");
    }

    /// <summary>空快照：无日志导出（仅 preamble）——导入后快照点 0、空快照区。</summary>
    [Fact]
    public async Task ExportImport_EmptySnapshot_PreambleOnly()
    {
        await using var fx = await CreateAsync();
        await fx.TransferA.ExportSnapshotAsync(fx.B.Self);   // 空日志：preamble(0) + 零条目帧
        await fx.TransferB.ImportSnapshotAsync(fx.A.Self);

        fx.StoreB.SnapshotIndex.Should().Be(0);
        fx.StoreB.LastLogIndex.Should().Be(0);
        (await CollectAsync(fx.StoreB, 0)).Should().BeEmpty();
    }
    /// <summary>E1 压缩：可压大条目（DEFLATE 包络路径）——导出→导入逐条一致（往返一致 + 收益）。</summary>
    [Fact]
    public async Task ExportImport_CompressibleLargeEntries_ByteExact()
    {
        await using var fx = await CreateAsync();
        var compressible = new byte[4096];
        Array.Fill(compressible, (byte)0x5A);
        var batch = new List<(long Term, byte Kind, ReadOnlyMemory<byte> Content)>();
        for (long i = 1; i <= 20; i++) batch.Add((1, RaftEntryKind.Command, compressible));
        (await fx.StoreA.AppendAsync(0, batch)).Should().Be(20);
        await fx.StoreA.WaitForPersistedAsync(20);

        await fx.TransferA.ExportSnapshotAsync(fx.B.Self);   // 导出：逐帧压缩包络（大帧 DEFLATE）
        await fx.TransferB.ImportSnapshotAsync(fx.A.Self);   // 导入：解包络重装

        fx.StoreB.SnapshotIndex.Should().Be(20);
        var source = await CollectAsync(fx.StoreA, 20);
        var target = await CollectAsync(fx.StoreB, 20);
        target.Should().HaveCount(20);
        for (var i = 0; i < 20; i++)
        {
            target[i].Item1.Should().Be(source[i].Item1, "index 逐条一致");
            target[i].Item2.Should().Be(source[i].Item2, "term 逐条一致");
            target[i].Item3.Should().Be(source[i].Item3, "kind 逐条一致");
            target[i].Item4.AsSpan().SequenceEqual(source[i].Item4).Should().BeTrue($"内容逐字节一致（条目 {i}）——压缩往返一致");
        }
    }

}
