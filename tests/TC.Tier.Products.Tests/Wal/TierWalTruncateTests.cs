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
