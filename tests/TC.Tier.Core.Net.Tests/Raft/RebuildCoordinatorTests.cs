using System.Collections.Concurrent;
using FluentAssertions;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Tests.Fixtures;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// RebuildCoordinator dual-source read contract tests (spec-03 §7 snapshot-region replay —
/// applied &lt; N₀ cross-boundary ranges are served from the snapshot read face, the live tail
/// from the main-data face; one call covers the whole range).
/// </summary>
public class RebuildCoordinatorTests
{
    /// <summary>Recording state machine (apply order recorded by index).</summary>
    private sealed class RecordingMachine : IStateMachine
    {
        public readonly ConcurrentDictionary<long, byte[]> Applied = new();
        public readonly List<long> Order = [];   // 单线程调用流——List 保序（bag 无序语义不可用于顺序断言）

        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Applied[index] = command.ToArray();
            Order.Add(index);
            return ValueTask.CompletedTask;
        }
    }

    private static (long Term, byte Kind, ReadOnlyMemory<byte> Content) Cmd(long v) => (1, RaftEntryKind.Command, new[] { (byte)v });

    /// <summary>Store with entries 1..8 (term 1, payload = index byte), snapshot N₀ = 5, applied = 2.</summary>
    private static async Task<InMemoryRaftStore> SeededStoreAsync()
    {
        var store = new InMemoryRaftStore();
        await store.InitializeAsync();
        await store.AppendAsync(0, [Cmd(1), Cmd(2), Cmd(3), Cmd(4), Cmd(5), Cmd(6), Cmd(7), Cmd(8)]);
        await store.WaitForPersistedAsync(8);   // 协议形态——应答前落盘水位推进（rebuild 只重放已持久化区间）
        await store.TruncatePrefixToAsync(5);
        await store.UpdateAppliedIndexAsync(2);
        return store;
    }

    /// <summary>Restart rebuild with applied below snapshot: entries [3..5] come from the snapshot
    /// face and [6..8] from the live face — the machine ends up at 8 with no gaps.</summary>
    [Fact]
    public async Task Rebuild_AppliedBelowSnapshot_ReplaysSnapshotRegionAndLiveTail()
    {
        var store = await SeededStoreAsync();
        var machine = new RecordingMachine();
        var configs = new List<ClusterConfig>();

        var applied = await RebuildCoordinator.RebuildAsync(store, machine, configs.Add);

        applied.Should().Be(8);
        machine.Applied.Should().HaveCount(6);
        machine.Applied.Keys.Should().BeEquivalentTo(new long[] { 3, 4, 5, 6, 7, 8 });
        machine.Order.ToArray().Should().BeInAscendingOrder("严格与日志序一致");
        store.AppliedIndex.Should().Be(8);
    }

    /// <summary>Range entirely inside the snapshot region is served by the snapshot face alone —
    /// entries below the range start are not applied.</summary>
    [Fact]
    public async Task ApplyCommittedRange_BelowSnapshotOnly_ReplaysSnapshotFace()
    {
        var store = await SeededStoreAsync();
        var machine = new RecordingMachine();

        var applied = await RebuildCoordinator.ApplyCommittedRangeAsync(
            store, machine, 3, 5, _ => { });

        applied.Should().Be(5);
        machine.Applied.Keys.Should().BeEquivalentTo(new long[] { 3, 4, 5 });
    }

    /// <summary>Range entirely above the snapshot is served by the live face — no snapshot-face
    /// double-apply (regression).</summary>
    [Fact]
    public async Task ApplyCommittedRange_AboveSnapshotOnly_ReplaysLiveFace()
    {
        var store = await SeededStoreAsync();
        var machine = new RecordingMachine();

        var applied = await RebuildCoordinator.ApplyCommittedRangeAsync(
            store, machine, 6, 8, _ => { });

        applied.Should().Be(8);
        machine.Applied.Keys.Should().BeEquivalentTo(new long[] { 6, 7, 8 });
    }

    /// <summary>Cross-boundary range in one call: [4..5] snapshot face + [6..7] live face,
    /// each entry exactly once.</summary>
    [Fact]
    public async Task ApplyCommittedRange_CrossesSnapshotBoundary_SinglePassNoGapNoDuplicate()
    {
        var store = await SeededStoreAsync();
        var machine = new RecordingMachine();

        var applied = await RebuildCoordinator.ApplyCommittedRangeAsync(
            store, machine, 4, 7, _ => { });

        applied.Should().Be(7);
        machine.Applied.Keys.Should().BeEquivalentTo(new long[] { 4, 5, 6, 7 });
        machine.Order.Count(x => x == 5).Should().Be(1, "跨界条目恰好一次");
    }

    /// <summary>Config entry inside the snapshot region is discriminated and notified in order.</summary>
    [Fact]
    public async Task Rebuild_ConfigEntryInSnapshotRegion_NotifiesConfigChanged()
    {
        var store = new InMemoryRaftStore();
        await store.InitializeAsync();
        var config = new ClusterConfig([new ClusterMember(NodeId.NewRandom(), "")]);
        await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }),
            (1, RaftEntryKind.Command, new byte[] { 2 }),
            (1, RaftEntryKind.Config, config.Serialize()),
            (1, RaftEntryKind.Command, new byte[] { 4 })]);
        await store.WaitForPersistedAsync(4);
        await store.TruncatePrefixToAsync(4);
        await store.UpdateAppliedIndexAsync(1);
        var machine = new RecordingMachine();
        var configs = new List<ClusterConfig>();

        var applied = await RebuildCoordinator.RebuildAsync(store, machine, configs.Add);

        applied.Should().Be(4);
        machine.Applied.Keys.Should().BeEquivalentTo(new long[] { 2, 4 }, "配置条目分流不进状态机");
        configs.Should().ContainSingle();
        configs[0].Members.Should().Equal(config.Members);
    }

    /// <summary>Empty range is a no-op returning from-1.</summary>
    [Fact]
    public async Task ApplyCommittedRange_EmptyRange_ReturnsFromMinusOne()
    {
        var store = await SeededStoreAsync();
        var machine = new RecordingMachine();

        var applied = await RebuildCoordinator.ApplyCommittedRangeAsync(
            store, machine, 7, 6, _ => { });

        applied.Should().Be(6);
        machine.Applied.Should().BeEmpty();
    }

    /// <summary>Fs restart (spec-13.1 persistent fixture): snapshot region survives a reopen and
    /// the dual-source rebuild replays through the boundary.</summary>
    [Fact]
    public async Task Rebuild_FsRestart_AppliedBelowSnapshot_ReplaysBothFaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "raft-rebuild-fixture", Guid.NewGuid().ToString("N"));
        var fs = TierFs.New($"local:///{root.Replace('\\', '/')}");
        try
        {
            var store = new FsRaftStore(fs, "node");
            await store.InitializeAsync();
            await store.AppendAsync(0, [Cmd(1), Cmd(2), Cmd(3), Cmd(4), Cmd(5), Cmd(6)]);
            await store.TruncatePrefixToAsync(4);
            await store.UpdateAppliedIndexAsync(1);
            store.Dispose();

            var restored = new FsRaftStore(fs, "node");
            await restored.InitializeAsync();
            try
            {
                restored.SnapshotIndex.Should().Be(4);
                var machine = new RecordingMachine();
                var applied = await RebuildCoordinator.RebuildAsync(restored, machine, _ => { });
                applied.Should().Be(6);
                machine.Applied.Keys.Should().BeEquivalentTo(new long[] { 2, 3, 4, 5, 6 });
            }
            finally
            {
                restored.Dispose();
            }
        }
        finally
        {
            fs.Dispose();
        }
    }
}
