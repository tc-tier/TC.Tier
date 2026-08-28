namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// TierWAL 追加/提交/水位/重放——核心语义测试（地址=事实、index 顺序值、双水位）。
/// </summary>
public class TierWalTests
{
    [Fact]
    public async Task Start_EmptyWal_Ready()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol);
        wal.IsReady.Should().BeTrue();
        wal.AllocatedIndex.Should().Be(0);
        wal.PersistedIndex.Should().Be(0);
        wal.SnapshotIndex.Should().Be(0);
    }

    [Fact]
    public async Task AppendBatch_AssignsSequentialIndexes()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        var r = await wal.AppendBatchAsync(new ReadOnlyMemory<byte>[] { WalTestFactory.Entry(1), WalTestFactory.Entry(2), WalTestFactory.Entry(3) }, default);
        r.StartIndex.Should().Be(1);
        r.Count.Should().Be(3);
        wal.AllocatedIndex.Should().Be(3);
        wal.PersistedIndex.Should().Be(0);   // 未提交——双水位分离
    }

    [Fact]
    public async Task AppendSingle_Sequential_ThenBatch_ContinuesIndex()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        var r1 = await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        r1.StartIndex.Should().Be(1);
        var r2 = await wal.AppendBatchAsync(new ReadOnlyMemory<byte>[] { WalTestFactory.Entry(2), WalTestFactory.Entry(3) }, default);
        r2.StartIndex.Should().Be(2);
        wal.AllocatedIndex.Should().Be(3);
    }

    [Fact]
    public async Task Commit_AdvancesPersistedIndex()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        await wal.AppendBatchAsync(new ReadOnlyMemory<byte>[] { WalTestFactory.Entry(1), WalTestFactory.Entry(2) }, default);
        wal.IsPersisted(2).Should().BeFalse();

        await wal.CommitAsync(default);
        wal.PersistedIndex.Should().Be(2);
        wal.IsPersisted(2).Should().BeTrue();
        wal.IsPersisted(3).Should().BeFalse();
    }

    [Fact]
    public async Task AppendBatch_Empty_Throws()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol);
        var act = () => wal.AppendBatchAsync([], default);
        act.Should().Throw<ArgumentException>();
    }

    // ═══ 重放 ═══

    [Fact]
    public async Task ReadFrom_AfterCommit_ReturnsAllEntries()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.CommitAsync(default);

        var entries = await ReadAll(wal, 1, default);
        entries.Should().HaveCount(10);
        for (int i = 0; i < 10; i++)
        {
            entries[i].Index.Should().Be(i + 1);
            entries[i].Data.ToArray().Should().Equal(WalTestFactory.Entry(i + 1));
        }
    }

    [Fact]
    public async Task ReadFrom_MiddleIndex_StartsThere()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        await wal.AppendBatchAsync(Enumerable.Range(1, 100).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.CommitAsync(default);

        var entries = await ReadAll(wal, 50, default);
        entries.Should().HaveCount(51);   // 50..100
        entries[0].Index.Should().Be(50);
        entries[^1].Index.Should().Be(100);
    }

    [Fact]
    public async Task ReadFrom_EmptyWal_Empty()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol);
        (await ReadAll(wal, 1, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadFrom_PastAllocated_Empty()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        (await ReadAll(wal, 5, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadFrom_Uncommitted_NotReplayed()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        // 未 commit——重放边界 = EntryLog CommittedOffset（页契约可能已提交——用单条强制形态验证未提交不可见）
        // 注：ManualCommit 下页契约提交可能在 append 后发生（页满）——本测试用小块数据（不触发页满）
        (await ReadAll(wal, 1, default)).Should().BeEmpty();
    }

    // ═══ 单条提交形态（三维度全 0）═══

    [Fact]
    public async Task SingleForce_EveryAppend_PersistsImmediately()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.SingleForce);

        for (int i = 1; i <= 5; i++)
        {
            await wal.AppendSingleAsync(WalTestFactory.Entry(i), default);
            wal.IsPersisted(i).Should().BeTrue($"第 {i} 条单条强制应已持久化");
        }
    }

    [Fact]
    public async Task WaitForPersisted_AlreadyPersisted_ReturnsImmediately()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        await wal.CommitAsync(default);

        await wal.WaitForPersistedAsync(1, default);   // 已持久化——立即返回
    }

    [Fact]
    public async Task WaitForPersisted_AfterCommit_Completes()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);

        var wait = wal.WaitForPersistedAsync(1, default).AsTask();
        wait.IsCompleted.Should().BeFalse();   // 未提交——等待中
        await wal.CommitAsync(default);
        await wait;                            // 提交后完成
    }

    [Fact]
    public async Task WaitForPersisted_OutOfRange_Throws()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol);
        var act = () => wal.WaitForPersistedAsync(5, default).AsTask();
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ═══ 稀疏锚点定位（跨 AnchorInterval 边界）═══

    [Fact]
    public async Task ReadFrom_AcrossAnchorBoundary_Works()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        // 3000 条 > AnchorInterval(1024)——锚点二分 + 段内扫帧路径
        await wal.AppendBatchAsync(
            Enumerable.Range(1, 3000).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.CommitAsync(default);

        var entries = await ReadAll(wal, 2000, default);
        entries.Should().HaveCount(1001);   // 2000..3000
        entries[0].Index.Should().Be(2000);
        entries[^1].Index.Should().Be(3000);
        entries[0].Data.ToArray().Should().Equal(WalTestFactory.Entry(2000));
    }

    [Fact]
    public async Task Start_UnderlyingLogReady_BeforeReturn()
    {
        // ★ 生命周期契约：StartAsync 返回时底层 EntryLog 必须已就绪（恢复依赖 join）
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        wal.DiagnosticLog.IsReady.Should().BeTrue();
        wal.DiagnosticLog.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed);
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);   // 底层未就绪会在此抛"引擎 Recovering"
        await wal.CommitAsync(default);
        wal.PersistedIndex.Should().Be(1);
    }

    internal static async Task<List<WalEntry>> ReadAll(TierWal wal, long startIndex, CancellationToken ct)
    {
        var list = new List<WalEntry>();
        await foreach (var e in wal.ReadFromAsync(startIndex, ct)) list.Add(e);
        return list;
    }
}
