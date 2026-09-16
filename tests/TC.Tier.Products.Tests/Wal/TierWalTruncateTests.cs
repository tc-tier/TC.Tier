namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// TierWAL 截断——raft 冲突修正（TruncateSuffix）/ 日志压缩（TruncatePrefix）+ 导出冻结。
/// </summary>
public class TierWalTruncateTests
{
    private static async Task<List<WalEntry>> ReadAll(TierWal wal, long startIndex, CancellationToken ct)
    {
        var list = new List<WalEntry>();
        await foreach (var e in wal.ReadFromAsync(startIndex, ct)) list.Add(e);
        return list;
    }

    // ═══ 尾截断（raft 冲突修正）═══

    [Fact]
    public async Task TruncateSuffix_RemovesTail_KeepsHead()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.CommitAsync(default);

        await wal.TruncateSuffixAsync(5, default);

        wal.AllocatedIndex.Should().Be(5);
        wal.PersistedIndex.Should().Be(5);   // 截断夹回持久化水位
        (await ReadAll(wal, 1, default)).Should().HaveCount(5);
        (await ReadAll(wal, 6, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task TruncateSuffix_EqualAllocated_NoOp()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendBatchAsync(Enumerable.Range(1, 5).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);

        await wal.TruncateSuffixAsync(5, default);
        wal.AllocatedIndex.Should().Be(5);
    }

    [Fact]
    public async Task TruncateSuffix_BeforeHead_Throws()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.CommitAsync(default);
        await wal.SnapshotAsync(default);   // raft 一体压缩（N₀ = 10——head 截到 11）

        var act = () => wal.TruncateSuffixAsync(4, default).AsTask();
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>清空主数据区（newTailIndex = head-1 = 0：raft 冲突回退到哨兵——leader prev=0
    /// 要求 follower 从头接受日志）：合法操作——清空后 index 从 1 重新续写、重启恢复不复活。</summary>
    [Fact]
    public async Task TruncateSuffix_ToHeadMinusOne_ClearsLog_AppendRestarts_RecoveryStaysEmpty()
    {
        using var vol = new TestVolume();
        await using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);

            await wal.TruncateSuffixAsync(0, default);   // 清空（head=1 → head-1=0 合法）

            wal.AllocatedIndex.Should().Be(0);
            (await ReadAll(wal, 1, default)).Should().BeEmpty();

            // 清空后从头续写（follower 接受 leader 的全新日志）
            await wal.AppendBatchAsync(Enumerable.Range(1, 3).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(100 + i)).ToList(), default);
            await wal.CommitAsync(default);
            var all = await ReadAll(wal, 1, default);
            all.Should().HaveCount(3);
            all.Select(e => e.Index).Should().BeEquivalentTo([1L, 2L, 3L]);
        }

        // 重启恢复：清空窗口内不留残影（截断点已随 meta 落盘）
        await using (var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            wal2.AllocatedIndex.Should().Be(3);
            (await ReadAll(wal2, 1, default)).Should().HaveCount(3);
        }
    }

    /// <summary>快照边界截断（snapshot N₀=10 后 head=11：TruncateSuffix(10) = head-1）——
    /// 清空主数据区保留快照区（raft 语义：follower 从 N₀+1 接续）。</summary>
    [Fact]
    public async Task TruncateSuffix_HeadMinusOne_AfterSnapshot_ClearsMainData()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.CommitAsync(default);
        await wal.SnapshotAsync(default);   // head=11、SnapshotIndex=10

        await wal.TruncateSuffixAsync(10, default);

        wal.AllocatedIndex.Should().Be(10);
        (await ReadAll(wal, 11, default)).Should().BeEmpty();
        wal.SnapshotIndex.Should().Be(10);   // 快照区不受影响
    }

    /// <summary>分叉覆盖序列（soak 楔死现场 N0 操作序列——2026-08-27 取证）：追加→提交→
    /// 追加新批→截断回退（分叉尾覆盖）→ ReadFrom(截断点) → 再追加——任何一步挂起即复现。</summary>
    [Fact]
    public async Task TruncateSuffix_ForkOverwrite_Sequence_NoHang()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        // 基线 1..118（共同前缀）
        for (var i = 0; i < 12; i++)
        {
            await wal.AppendBatchAsync(Enumerable.Range(1 + i * 10, 10)
                .Select(j => (ReadOnlyMemory<byte>)WalTestFactory.Entry(j)).ToList(), default);
        }
        await wal.CommitAsync(default);

        // 分叉批 119..127（B1——旧 leader 数据）
        await wal.AppendBatchAsync(Enumerable.Range(119, 9)
            .Select(j => (ReadOnlyMemory<byte>)WalTestFactory.Entry(j)).ToList(), default);
        await wal.CommitAsync(default);

        // 新 leader 批（B2）：截断到 118（去掉分叉 119..127）→ 读边界 term → 追加新 119..122
        await wal.TruncateSuffixAsync(118, default);
        await foreach (var e in wal.ReadFromAsync(118, default)) { break; }   // ★ 挂点候选 1
        await wal.AppendBatchAsync(Enumerable.Range(119, 4)
            .Select(j => (ReadOnlyMemory<byte>)WalTestFactory.Entry(j)).ToList(), default);   // ★ 挂点候选 2
        await wal.CommitAsync(default);

        wal.AllocatedIndex.Should().Be(122);
        var all = await ReadAll(wal, 110, default);
        all.Select(e => e.Index).Should().BeEquivalentTo(Enumerable.Range(110, 13).Select(i => (long)i));
    }

    [Fact]
    public async Task TruncateSuffix_ThenAppend_ContinuesFromNewTail()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.TruncateSuffixAsync(6, default);

        var r = await wal.AppendSingleAsync(WalTestFactory.Entry(7), default);
        r.StartIndex.Should().Be(7);
        await wal.CommitAsync(default);
        var all = await ReadAll(wal, 1, default);
        all.Should().HaveCount(7);
        all[^1].Index.Should().Be(7);
    }

    // ═══ 头截断（日志压缩）═══

    [Fact]
    public async Task TruncatePrefix_BeyondTail_Throws()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        var act = () => wal.TruncatePrefixAsync(12, default).AsTask();   // 11 = 截空（合法）——12 越界
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
