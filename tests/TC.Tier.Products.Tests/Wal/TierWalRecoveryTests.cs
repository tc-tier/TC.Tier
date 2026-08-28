namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// TierWAL 跨实例恢复——opaque 容器 O(1) 解析 + 统一扫描重建锚点（= 重放成本）+ 水位还原。
/// </summary>
public class TierWalRecoveryTests
{
    [Fact]
    public async Task Restart_RecoversIndexesAndData()
    {
        using var vol = new TestVolume();
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 100).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        wal2.AllocatedIndex.Should().Be(100);
        wal2.PersistedIndex.Should().Be(100);
        wal2.IsReady.Should().BeTrue();

        var entries = await TierWalTests.ReadAll(wal2, 1, default);
        entries.Should().HaveCount(100);
        entries[^1].Index.Should().Be(100);
        entries[^1].Data.ToArray().Should().Equal(WalTestFactory.Entry(100));
    }

    [Fact]
    public async Task Restart_UncommittedData_ScannedBack()
    {
        using var vol = new TestVolume();
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            // 不显式 commit——Dispose 只刷页不推进 commit（页契约提交可能发生，但 opaque 不 stage）
            await wal.AppendBatchAsync(Enumerable.Range(1, 50).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        // ★ 统一扫描恢复：已落盘数据全部可见（raft 恢复后按 commitIndex apply，未提交由协议层截断）
        wal2.AllocatedIndex.Should().Be(50);
        wal2.PersistedIndex.Should().Be(0);   // 从未显式提交——raft 可应答水位 = 0
        (await TierWalTests.ReadAll(wal2, 1, default)).Should().HaveCount(50);
    }

    [Fact]
    public async Task Restart_PartialCommit_AllocatedBeyondPersisted()
    {
        using var vol = new TestVolume();
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 100).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);         // persisted = 100
            await wal.AppendBatchAsync(Enumerable.Range(101, 50).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);   // 未提交
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        // ★ WAL 一致性语义：未 commit 的数据不恢复（EntryLog 恢复走 meta 水位 100——
        //   Dispose 只刷页不推进 commit 边界）
        wal2.AllocatedIndex.Should().Be(100);
        wal2.PersistedIndex.Should().Be(100);
        (await TierWalTests.ReadAll(wal2, 101, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Restart_HeadTruncation_Preserved()
    {
        using var vol = new TestVolume();
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 100).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
            await wal.SnapshotAsync(default);   // raft 一体压缩（N₀ = 100——head 截到 101）
            await wal.AppendBatchAsync(Enumerable.Range(101, 40).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
            await wal.SnapshotAsync(default);   // 再压缩（N₀ = 140——head 截到 141）
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        wal2.AllocatedIndex.Should().Be(140);
        wal2.PersistedIndex.Should().Be(140);
        wal2.SnapshotIndex.Should().Be(140, "二次快照覆盖点");
        var act = () => TierWalTests.ReadAll(wal2, 140, default);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();   // 已截断区间不可读（head=141）
        (await TierWalTests.ReadAll(wal2, 141, default)).Should().BeEmpty();   // 尾部已全部截断
    }

    [Fact]
    public async Task Restart_TailTruncation_Preserved()
    {
        using var vol = new TestVolume();
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 100).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
            await wal.TruncateSuffixAsync(60, default);
            // ★ dispose 前直连扫描（数据应在）
            long before = 0;
            using (var cur = wal.DiagnosticLog.OpenCursor(LogicalAddress.Empty, wal.DiagnosticLog.TailAddress))
                while (cur.MoveNext()) before++;
            before.Should().Be(60);
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        long direct = 0;
        using (var cur = wal2.DiagnosticLog.OpenCursor(LogicalAddress.Empty, wal2.DiagnosticLog.TailAddress))
            while (cur.MoveNext()) direct++;
        direct.Should().Be(60);
        wal2.AllocatedIndex.Should().Be(60);
        (await TierWalTests.ReadAll(wal2, 1, default)).Should().HaveCount(60);
        (await TierWalTests.ReadAll(wal2, 61, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Restart_AppendContinues()
    {
        using var vol = new TestVolume();
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 100).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        var r = await wal2.AppendSingleAsync(WalTestFactory.Entry(101), default);
        r.StartIndex.Should().Be(101);
        await wal2.CommitAsync(default);
        (await TierWalTests.ReadAll(wal2, 1, default)).Should().HaveCount(101);
    }

    [Fact]
    public async Task Restart_AcrossAnchorBoundary()
    {
        using var vol = new TestVolume();
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 3000).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        wal2.AllocatedIndex.Should().Be(3000);
        var entries = await TierWalTests.ReadAll(wal2, 2500, default);
        entries.Should().HaveCount(501);
        entries[0].Index.Should().Be(2500);
    }

    [Fact]
    public async Task Restart_SingleForce_RecoversImmediatelyPersisted()
    {
        using var vol = new TestVolume();
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.SingleForce))
        {
            for (int i = 1; i <= 20; i++)
                await wal.AppendSingleAsync(WalTestFactory.Entry(i), default);
            wal.PersistedIndex.Should().Be(20);   // 单条强制 = 每条已持久化
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.SingleForce);
        wal2.AllocatedIndex.Should().Be(20);
        wal2.PersistedIndex.Should().Be(20);
    }
}
