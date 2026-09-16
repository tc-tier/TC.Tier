using System.Runtime.CompilerServices;
using FluentAssertions;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net.Raft;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// raft 存储夹具契约测试（spec-12 §8.1/§13.1——两实现同一契约：InMemoryRaftStore 缺省快路径 ∥
/// FsRaftStore 真持久化语义）+ Fs 专属（重启恢复/掉电半写截断）。
/// </summary>
public class RaftStoreContractTests
{
    public static TheoryData<string, Func<IRaftStore>> Stores => new()
    {
        { "InMemory", static () => new InMemoryRaftStore() },
        { "Fs", static () => NewFsStore() },
    };

    /// <summary>Fs 夹具根（TMP 重定向生效——测试临时目录纪律）。</summary>
    private static string FixtureRoot() => Path.Combine(Path.GetTempPath(), "raft-fixture");

    private static FsRaftStore NewFsStore()
    {
        var root = Path.Combine(FixtureRoot(), Guid.NewGuid().ToString("N"));
        var fs = TierFs.New($"local:///{root.Replace('\\', '/')}");
        var store = new FsRaftStore(fs, "node");
        store.InitializeAsync().AsTask().GetAwaiter().GetResult();
        return store;
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 元数据_任期投票原子写_回退拒绝(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        var candidate = NodeId.NewRandom();
        await store.WriteTermAndVoteAsync(5, candidate);
        store.Term.Should().Be(5);
        store.VotedFor.Should().Be(candidate);

        await store.WriteTermAndVoteAsync(6, NodeId.Empty);   // 抬任期清投票（降级共用路径）
        store.Term.Should().Be(6);
        store.VotedFor.Should().Be(NodeId.Empty);

        var act = () => store.WriteTermAndVoteAsync(5, candidate).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>("term 单调不降");
        await store.UpdateAppliedIndexAsync(42);
        store.AppliedIndex.Should().Be(42);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 追加_尾断言_纯追加与冲突回退一体(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (long Term, byte Kind, ReadOnlyMemory<byte> Content) E(long term, byte v) => (term, RaftEntryKind.Command, new[] { v });

        (await store.AppendAsync(0, [E(1, 1), E(1, 2), E(2, 3)])).Should().Be(3);
        store.LastLogIndex.Should().Be(3);
        store.LastLogTerm.Should().Be(2);

        store.TryGetEntry(2, out var term, out var kind, out var content).Should().BeTrue();
        term.Should().Be(1);
        kind.Should().Be(RaftEntryKind.Command);
        content.ToArray().Should().Equal(new byte[] { 2 });

        // 冲突回退一体：prevIndex=1 → 截 (1,3] 重写
        (await store.AppendAsync(1, [E(2, 9), E(2, 8)])).Should().Be(3);
        store.LastLogTerm.Should().Be(2);
        store.TryGetEntry(2, out term, out kind, out _).Should().BeTrue();
        term.Should().Be(2);
        store.TryGetEntry(3, out _, out _, out content).Should().BeTrue();
        content.ToArray().Should().Equal(new byte[] { 8 });

        var hole = () => store.AppendAsync(99, [E(2, 1)]).AsTask();
        await hole.Should().ThrowAsync<InvalidOperationException>("prevIndex 超尾=空洞违规");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 扫描_顺序有界_快照区外(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (1, RaftEntryKind.Command, new byte[] { 2 }), (1, RaftEntryKind.Command, new byte[] { 3 }), (2, RaftEntryKind.Command, new byte[] { 4 })]))
            .Should().Be(4);

        var scan = store.Scan(2, 2).ToList();
        scan.Should().HaveCount(2);
        scan[0].Index.Should().Be(2);
        scan[1].Index.Should().Be(3);

        store.Scan(99, 10).Should().BeEmpty();

        await store.TruncatePrefixToAsync(2);   // 快照点=2——(0,2] 截除
        store.SnapshotIndex.Should().Be(2);
        store.TryGetEntry(2, out _, out _, out _).Should().BeFalse("快照区条目已截除");
        store.LastLogIndex.Should().Be(4, "截头不影响尾");

        var afterPrefix = store.Scan(1, 10).ToList();   // fromIndex 收敛到快照点后
        afterPrefix.Select(e => e.Index).Should().Equal(3L, 4L);

        await store.TruncatePrefixToAsync(1);   // 幂等（≤ 快照点）
        store.SnapshotIndex.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 截尾_删至尾前_快照区保护(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (1, RaftEntryKind.Command, new byte[] { 2 }), (1, RaftEntryKind.Command, new byte[] { 3 })])).Should().Be(3);

        await store.TruncateSuffixFromAsync(2);
        store.LastLogIndex.Should().Be(1);
        store.TryGetEntry(2, out _, out _, out _).Should().BeFalse();
        store.TryGetEntry(1, out var term, out _, out _).Should().BeTrue();
        term.Should().Be(1);

        await store.TruncatePrefixToAsync(1);
        var act = () => store.TruncateSuffixFromAsync(1).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>("截尾不可删进快照区");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 双水位_追加即分配_持久化显式推进(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (1, RaftEntryKind.Command, new byte[] { 2 })])).Should().Be(2);

        store.AllocatedIndex.Should().Be(2);
        store.PersistedIndex.Should().Be(0, "持久化时机由协议层控制（追加≠落盘）");

        await store.WaitForPersistedAsync(2);
        store.PersistedIndex.Should().Be(2);

        var beyond = () => store.WaitForPersistedAsync(99).AsTask();
        await beyond.Should().ThrowAsync<InvalidOperationException>("等待超已分配尾违规");
    }

    /// <summary>Truncating append clamps the persisted watermark (out-of-order stale batch
    /// rewrite regression — persisted must never hang above the tail, or success responses
    /// overstate the match index and wedge replication).</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Append_ConflictRewrite_ClampsPersistedWatermark(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (1, RaftEntryKind.Command, new byte[] { 2 }), (1, RaftEntryKind.Command, new byte[] { 3 }), (1, RaftEntryKind.Command, new byte[] { 4 }), (1, RaftEntryKind.Command, new byte[] { 5 })])).Should().Be(5);
        await store.WaitForPersistedAsync(5);
        store.PersistedIndex.Should().Be(5);

        // 乱序旧批重写（prev=2——冲突回退截断 [3..5] 后重写 [3..4]）：尾回退 4，水位必须钳制 ≤ 4
        (await store.AppendAsync(2, [(1, RaftEntryKind.Command, new byte[] { 3 }), (1, RaftEntryKind.Command, new byte[] { 4 })])).Should().Be(4);
        store.LastLogIndex.Should().Be(4);
        store.PersistedIndex.Should().Be(4, "截断回退后持久化水位钳制到新尾——不得悬空超尾");
    }

    // ══ 快照读/导入面（spec-03/T5——快照 = 可导出的日志前缀镜像）══

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 快照读_推进后_前缀条目可导出(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (2, RaftEntryKind.Command, new byte[] { 2 }), (2, RaftEntryKind.Command, new byte[] { 3 }), (3, RaftEntryKind.Command, new byte[] { 4 })])).Should().Be(4);
        await store.WaitForPersistedAsync(4);
        await store.TruncatePrefixToAsync(2);   // 快照点=2——[1..2] 进入快照区（保留可导出）

        store.SnapshotIndex.Should().Be(2);
        var snapshot = new List<(long, long, byte, byte[])>();
        await foreach (var e in store.ReadSnapshotEntriesAsync(2)) snapshot.Add((e.Index, e.Term, e.Kind, e.Content.ToArray()));
        snapshot.Should().HaveCount(2, "快照区前缀可导出（快照传输源）");
        // 逐字段断言（元组含 byte[]——相等性=引用，载荷逐字节比较）
        snapshot[0].Item1.Should().Be(1);
        snapshot[0].Item2.Should().Be(1);
        snapshot[0].Item3.Should().Be(RaftEntryKind.Command);
        snapshot[0].Item4.Should().Equal(new byte[] { 1 });
        snapshot[1].Item1.Should().Be(2);
        snapshot[1].Item2.Should().Be(2);
        snapshot[1].Item3.Should().Be(RaftEntryKind.Command);
        snapshot[1].Item4.Should().Equal(new byte[] { 2 });

        store.TryGetEntry(2, out _, out _, out _).Should().BeFalse("主数据读面拒绝快照区");
        // ★ 快照边界 term 可读（契约——复制 lane 的边界 prev 必须携带真 term，发 0 会被
        //   无快照 follower 的 term 比对拒绝→安装→换届风暴）
        (await store.ReadLogTermAsync(2)).Should().Be(2, "边界 term=N₀ 条目（index 2）的 term");
        store.Invoking(s => s.ReadLogTermAsync(1).AsTask().GetAwaiter().GetResult())
            .Should().Throw<InvalidOperationException>("term 读面拒绝快照区内部（< N₀）");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 快照读_零覆盖点_空流(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 })])).Should().Be(1);

        var count = 0;
        await foreach (var _ in store.ReadSnapshotEntriesAsync(0)) count++;
        count.Should().Be(0, "snapshotIndex=0 = 空流（无快照）");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 快照导入_重锚_尾与水位一体(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (1, RaftEntryKind.Command, new byte[] { 2 }), (1, RaftEntryKind.Command, new byte[] { 3 })])).Should().Be(3);
        await store.WriteTermAndVoteAsync(4, NodeId.NewRandom());
        await store.UpdateAppliedIndexAsync(2);

        // follower 导入快照 [1..2]（term 5 的新内容——覆盖旧日志）
        static async IAsyncEnumerable<(long, long, byte, ReadOnlyMemory<byte>)> Snapshot([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return (1, 5, RaftEntryKind.Command, new byte[] { 9 });
            yield return (2, 5, RaftEntryKind.Command, new byte[] { 8 });
        }
        await store.ImportSnapshotAsync(2, Snapshot());

        store.SnapshotIndex.Should().Be(2);
        store.LastLogIndex.Should().Be(2, "日志重锚到快照覆盖点");
        store.PersistedIndex.Should().Be(2);
        store.AllocatedIndex.Should().Be(2);
        store.LastLogTerm.Should().Be(5, "尾条 term 随导入");
        store.Term.Should().Be(4, "任期是选举状态——导入不动");
        store.AppliedIndex.Should().Be(2, "applied 由业务重建路径推进——导入不动");

        store.PrevLogMatchesAsync(2, 5).AsTask().GetAwaiter().GetResult().Should().BeTrue("快照区信任快照");
        store.TryGetEntry(1, out _, out _, out _).Should().BeFalse("主数据读面拒绝快照区");

        // 导入后可续接追加（prevIndex=N₀ 合法——增量复制从 N₀+1 接续）
        (await store.AppendAsync(2, [(5, RaftEntryKind.Command, new byte[] { 7 })])).Should().Be(3);
        store.LastLogTerm.Should().Be(5);
        store.ReadLogTermAsync(3).AsTask().GetAwaiter().GetResult().Should().Be(5);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 快照导入_越界条目_抛(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        static async IAsyncEnumerable<(long, long, byte, ReadOnlyMemory<byte>)> Bad([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return (3, 1, RaftEntryKind.Command, new byte[] { 1 });   // 越界（snapshotIndex=2）
        }
        await store.Invoking(s => s.ImportSnapshotAsync(2, Bad()).AsTask())
            .Should().ThrowAsync<InvalidOperationException>("快照条目 index 须在 [1..N₀]");
    }

    // ══ 复制热路径读面（wire v3——CountEntries/WriteEntriesTo 直写契约）══

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 复制读面_计数与直写_一致于Scan(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }),
            (1, RaftEntryKind.Config, new byte[] { 2 }),
            (2, RaftEntryKind.Command, new byte[] { 3 }),
            (2, RaftEntryKind.Command, new byte[] { 4 })])).Should().Be(4);
        await store.WaitForPersistedAsync(3);   // 只持久化 [1..3]——第 4 条不在直写面

        store.CountEntries(1, 100).Should().Be(3, "只数已持久化区");
        store.CountEntries(2, 2).Should().Be(2, "maxCount 钳制");
        store.CountEntries(4, 10).Should().Be(0, "超持久化尾 = 0");
        store.CountEntries(99, 10).Should().Be(0);

        var writer = new TC.Tier.Core.Primitives.PooledBufferWriter();
        var terms = new long[4];
        var n = await store.WriteEntriesToAsync(1, 4, writer, terms);
        n.Should().Be(3, "写入钳制在已持久化区");
        terms[0].Should().Be(1);
        terms[1].Should().Be(1);
        terms[2].Should().Be(2);
        terms.AsSpan(3).ToArray().Should().OnlyContain(t => t == 0, "回填止于写入数");

        // 直写产物 = RaftEntriesRegion 布局，逐条与 Scan 一致（单拷贝正确性锚）
        RaftEntriesRegion.TryReadCount(writer.WrittenMemory, out var count, out var cursor).Should().BeTrue();
        count.Should().Be(3);
        foreach (var e in store.Scan(1, n))   // 区只含已持久化条数——Scan 同界对拍
        {
            RaftEntriesRegion.TryReadEntry(writer.WrittenMemory, ref cursor, out var term, out var kind, out var content)
                .Should().BeTrue();
            term.Should().Be(e.Term);
            kind.Should().Be(e.Kind);
            content.ToArray().Should().Equal(e.Content.ToArray());
        }
        cursor.Should().Be(writer.WrittenCount);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task 复制读面_越界与截断_防御性零写入(string medium, Func<IRaftStore> createStore)
    {
        var store = createStore();
        (await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 })])).Should().Be(1);
        await store.WaitForPersistedAsync(1);
        await store.TruncatePrefixToAsync(1);   // 快照点=1——主数据区从 2 起

        var writer = new TC.Tier.Core.Primitives.PooledBufferWriter();
        (await store.WriteEntriesToAsync(1, 1, writer, new long[1])).Should().Be(0, "fromIndex 落快照区 = 防御性零写入");
        (await store.WriteEntriesToAsync(2, 1, writer, new long[1])).Should().Be(0, "超持久化尾 = 防御性零写入");
        writer.WrittenCount.Should().Be(0, "零写入不产字节");
    }

    // ══ Fs 专属：真持久化语义 ══

    [Fact]
    public async Task Fs_重启恢复_元数据与日志全量复原()
    {
        var root = Path.Combine(FixtureRoot(), Guid.NewGuid().ToString("N"));
        var spec = $"local:///{root.Replace('\\', '/')}";
        var votedFor = NodeId.NewRandom();

        using (var fs = TierFs.New(spec))
        {
            using var store = new FsRaftStore(fs, "node");   // 句柄生命周期归 store——显式释放防跨"重启"占用
            await store.InitializeAsync();
            await store.WriteTermAndVoteAsync(7, votedFor);
            await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (2, RaftEntryKind.Command, new byte[] { 2 }), (2, RaftEntryKind.Command, new byte[] { 3 })]);
            await store.UpdateAppliedIndexAsync(2);
            await store.TruncatePrefixToAsync(1);
            await store.WaitForPersistedAsync(3);
        }

        using (var fs = TierFs.Open(spec))   // "重启"——同 spec 重开既有文件系统（New 要求空根）
        {
            var restored = new FsRaftStore(fs, "node");
            await restored.InitializeAsync();
            restored.Term.Should().Be(7);
            restored.VotedFor.Should().Be(votedFor);
            restored.AppliedIndex.Should().Be(2);
            restored.SnapshotIndex.Should().Be(1);
            restored.LastLogIndex.Should().Be(3);
            restored.LastLogTerm.Should().Be(2);
            restored.TryGetEntry(2, out var term, out var kind, out var content).Should().BeTrue();
            term.Should().Be(2);
            kind.Should().Be(RaftEntryKind.Command);
            content.ToArray().Should().Equal(new byte[] { 2 });
        }
    }

    [Fact]
    public async Task Fs_掉电半写_坏尾截断_好条目保留()
    {
        var root = Path.Combine(FixtureRoot(), Guid.NewGuid().ToString("N"));
        var spec = $"local:///{root.Replace('\\', '/')}";
        using var fs = TierFs.New(spec);

        var store = new FsRaftStore(fs, "node");
        await store.InitializeAsync();
        await store.AppendAsync(0, [(1, RaftEntryKind.Command, new byte[] { 1 }), (1, RaftEntryKind.Command, new byte[] { 2 })]);

        // 模拟掉电半写：log 尾部追加残缺帧（不足 20B 条目头——恢复时按坏尾截断）
        using (var handle = fs.Open("node/log", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite }))
        {
            var partial = new byte[10];   // 不足 20B 条目头（async 方法 stackalloc 不可用）
            handle.Write(handle.Length, partial);
        }

        var reopened = new FsRaftStore(fs, "node");
        await reopened.InitializeAsync();
        reopened.LastLogIndex.Should().Be(2, "残缺尾帧截断——完好条目保留");
        reopened.TryGetEntry(2, out _, out _, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Fs_快照导入_重启恢复_全量复原()
    {
        var root = Path.Combine(FixtureRoot(), Guid.NewGuid().ToString("N"));
        var spec = $"local:///{root.Replace('\\', '/')}";
        using (var fs = TierFs.New(spec))
        {
            using var store = new FsRaftStore(fs, "node");
            await store.InitializeAsync();
            await store.WriteTermAndVoteAsync(3, NodeId.NewRandom());
            static async IAsyncEnumerable<(long, long, byte, ReadOnlyMemory<byte>)> Snapshot([EnumeratorCancellation] CancellationToken ct = default)
            {
                await Task.Yield();
                yield return (1, 2, RaftEntryKind.Command, new byte[] { 9 });
                yield return (2, 3, RaftEntryKind.Command, new byte[] { 8 });
            }
            await store.ImportSnapshotAsync(2, Snapshot());
            (await store.AppendAsync(2, [(3, RaftEntryKind.Command, new byte[] { 7 })])).Should().Be(3);   // 快照后增量
        }

        using (var fs = TierFs.Open(spec))   // "重启"——同 spec 重开
        {
            var restored = new FsRaftStore(fs, "node");
            await restored.InitializeAsync();
            restored.SnapshotIndex.Should().Be(2, "meta snapshot 字段复原");
            restored.LastLogIndex.Should().Be(3);
            restored.LastLogTerm.Should().Be(3);
            restored.ReadLogTermAsync(3).AsTask().GetAwaiter().GetResult().Should().Be(3, "增量条目复原");

            var snapshot = new List<(long, long, byte, byte[])>();
            await foreach (var e in restored.ReadSnapshotEntriesAsync(2)) snapshot.Add((e.Index, e.Term, e.Kind, e.Content.ToArray()));
            snapshot.Should().HaveCount(2, "快照区前缀经重启保持可导出");
            snapshot[0].Item1.Should().Be(1);
            snapshot[0].Item2.Should().Be(2);
            snapshot[0].Item3.Should().Be(RaftEntryKind.Command);
            snapshot[0].Item4.Should().Equal(new byte[] { 9 });
        }
    }
}
