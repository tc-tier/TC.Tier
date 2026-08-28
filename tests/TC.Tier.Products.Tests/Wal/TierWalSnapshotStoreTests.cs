using TC.Tier.Core.IO;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 镜像快照部件（第三部件——本地存储 = IncrementalSnapshot 增量段）：SnapshotAsync 一体压缩
/// （主数据 [Head..N₀] 镜像帧流 → 本地快照 → 截断）→ 冷启动自动载入（SnapshotIndex = N₀）→
/// raft 经 ReadSnapshotEntriesAsync 流式重建 + ReadFromAsync(N₀+1) 主数据增量回放；
/// TruncatePrefix 快照覆盖校验（先快照后截断）；快照缺失回退全量回放；快照内容损坏读时暴露。
/// </summary>
public class TierWalSnapshotStoreTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("tc-wal-snapshot-store");
    private readonly List<IFileSystem> _fss = [];

    public void Dispose()
    {
        foreach (var fs in _fss) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
        GC.SuppressFinalize(this);
    }

    private IFileSystem NewVolume()
    {
        var vol = Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.raw");
        var fs = TierFs.New($"virtual:///{vol.Replace('\\', '/')}");
        _fss.Add(fs);
        return fs;
    }

    private static TierWalOptions ManualCommit() => TierWalOptions.Default
        .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
        .WithMaxUnflushedBytes(long.MaxValue)
        .WithMaxUnflushedCount(int.MaxValue);

    private static async Task AppendRangeAsync(TierWal wal, long firstIndex, int n)
    {
        const int batchSize = 1000;
        for (var done = 0; done < n; done += batchSize)
        {
            var m = Math.Min(batchSize, n - done);
            var batch = new ReadOnlyMemory<byte>[m];
            for (var j = 0; j < m; j++) batch[j] = new byte[64];
            await wal.AppendBatchAsync(batch, default);
        }
    }

    /// <summary>聚合流式读回快照帧流（测试断言用；生产 raft 分块消费）。</summary>
    private static async Task<byte[]> ReadSnapshotAll(TierWal wal)
    {
        using var ms = new MemoryStream();
        await foreach (var chunk in wal.ReadSnapshotEntriesAsync(default))
            ms.Write(chunk.Span);
        return ms.ToArray();
    }

    /// <summary>解析快照帧流 → 条目数（每条 = [len 4B][payload] 一帧）。</summary>
    private static int CountSnapshotEntries(byte[] frameStream)
    {
        int count = 0;
        int off = 0;
        while (off + 4 <= frameStream.Length)
        {
            int len = BitConverter.ToInt32(frameStream, off);
            if (len <= 0 || len > WalSnapshotFormat.MaxPayloadLength || off + 4 + len > frameStream.Length) break;
            off += 4 + len;
            count++;
        }
        return count;
    }

    [Fact]
    public async Task Snapshot_ThenTruncate_Restart_AutoLoadsAndReplays()
    {
        using var fs = NewVolume();

        // 主节点：60,000 条 → 一体快照（N₀=60,000——镜像帧流 60,000 条 + head 截到 60,001）→ 增量 5,000
        await using (var wal = await ManualCommit().Builder(fs).StartAsync())
        {
            await AppendRangeAsync(wal, 1, 60_000);
            await wal.CommitAsync(default);
            var n0 = await wal.SnapshotAsync(default);
            n0.Should().Be(60_000, "快照覆盖点 = 当前 PersistedIndex");
            wal.SnapshotIndex.Should().Be(60_000);

            await AppendRangeAsync(wal, 60_001, 5_000);
            await wal.CommitAsync(default);
            wal.AllocatedIndex.Should().Be(65_000);
            (await TierWalTests.ReadAll(wal, 60_001, default)).Should().HaveCount(5_000);
        }

        // 冷启动：自动载入快照（SnapshotIndex=N₀）→ raft 流式重建镜像 + 主数据增量回放
        await using var wal2 = await ManualCommit().Builder(fs).StartAsync();
        wal2.SnapshotIndex.Should().Be(60_000, "冷启动自动载入——SnapshotIndex 恢复");
        CountSnapshotEntries(await ReadSnapshotAll(wal2)).Should().Be(60_000, "镜像帧流 = [Head..N₀] 全部条目");
        wal2.AllocatedIndex.Should().Be(65_000);

        var replayed = await TierWalTests.ReadAll(wal2, 60_001, default);
        replayed.Should().HaveCount(5_000, "载快照后回放 (N₀, 尾] 主数据增量");
        replayed[0].Index.Should().Be(60_001);
        replayed[^1].Index.Should().Be(65_000);
    }

    [Fact]
    public async Task TruncatePrefix_WithoutSnapshot_Rejected()
    {
        using var fs = NewVolume();
        await using var wal = await ManualCommit().Builder(fs).StartAsync();
        await AppendRangeAsync(wal, 1, 100);
        await wal.CommitAsync(default);

        // 无快照截断 = 被截区不可恢复——拒绝（raft 先快照后截断）
        var act = () => wal.TruncatePrefixAsync(50, default).AsTask();
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("SnapshotAsync", "提示先持久化镜像");
    }

    [Fact]
    public async Task Snapshot_Missing_Restart_DataUnrecoverable()
    {
        using var fs = NewVolume();
        await using (var wal = await ManualCommit().Builder(fs).StartAsync())
        {
            await AppendRangeAsync(wal, 1, 10_000);
            await wal.CommitAsync(default);
            await wal.SnapshotAsync(default);   // 一体压缩——主数据 [1..10,000] 已截断
        }

        // 删除快照存储（模拟快照丢失）→ 重启：raft 语义 = 快照是已截日志的唯一恢复源——丢失不可恢复
        foreach (var e in fs.EnumerateFiles("*", recursive: true))
            if (e.Name.Contains(".snapshot", StringComparison.Ordinal))
                fs.Delete(e.Name);

        await using var wal2 = await ManualCommit().Builder(fs).StartAsync();
        wal2.SnapshotIndex.Should().Be(0, "快照缺失——无镜像可载");
        (await ReadSnapshotAll(wal2)).Should().BeEmpty("无快照——流式读回空流");
        wal2.AllocatedIndex.Should().Be(10_000, "主数据尾水位仍恢复");
        var act = () => TierWalTests.ReadAll(wal2, 1, default);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>("已截断区间不可读——快照丢失 = 数据不可恢复（raft 多副本兜底）");
    }

    [Fact]
    public async Task Snapshot_Corrupt_Restart_SnapshotIndexRecovered_ReadThrows()
    {
        using var fs = NewVolume();
        await using (var wal = await ManualCommit().Builder(fs).StartAsync())
        {
            await AppendRangeAsync(wal, 1, 10_000);
            await wal.CommitAsync(default);
            await wal.SnapshotAsync(default);
        }

        // 篡改快照数据引擎段文件帧头（破坏 magic——meta 引擎 [段表] 不动；内容损坏读时暴露）
        foreach (var e in fs.EnumerateFiles("*", recursive: true))
            if (e.Name.Contains(".snapshot", StringComparison.Ordinal) && !e.Name.Contains(".meta", StringComparison.Ordinal))
            {
                using var h = fs.Open(e.Name, new FileOpenOptions
                { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
                h.Write(0, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });   // 帧头 magic 破坏
            }

        await using var wal2 = await ManualCommit().Builder(fs).StartAsync();
        wal2.SnapshotIndex.Should().Be(10_000, "段表（meta）O(1) 恢复——覆盖点不受内容损坏影响");
        wal2.AllocatedIndex.Should().Be(10_000);
        var act = () => ReadSnapshotAll(wal2);
        await act.Should().ThrowAsync<InvalidDataException>("快照内容损坏——流式读回帧头校验失败暴露");
    }
}
