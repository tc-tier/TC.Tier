using TC.Tier.Core.IO;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 契约③ virtual 介质多页/快照衔接回归（根因：LogBase 游标 frameless 缓冲"扇区余量"硬编码 512，
/// SectorSize=4096（raw 卷块大小）时满页帧 padding 4,084B 罩不住下一帧头——页界探测断链，
/// 恢复水位截断在单页，增量重放 0 条）。
/// </summary>
public class TierWalVirtualMultiPageTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("tc-wal-virtual-multipage");
    private readonly List<IFileSystem> _fss = [];

    public void Dispose()
    {
        foreach (var fs in _fss) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
        GC.SuppressFinalize(this);
    }

    private IFileSystem NewVirtualVolume()
    {
        var vol = Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.tier");
        var fs = TierFs.New($"virtual:///{vol.Replace('\\', '/')}");
        _fss.Add(fs);
        return fs;
    }

    private static TierWalOptions GroupCommitOptions() => TierWalOptions.Default
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

    [Fact]
    public async Task Recovery_Virtual_MultiPage_ReadsAllEntries()
    {
        using var fs = NewVirtualVolume();
        var options = GroupCommitOptions();

        // 66,000 条 = 47,662/页 × 2 页 + 尾页（跨两次页界——页界探测断链的触发面）
        await using (var wal1 = await options.Builder(fs).StartAsync())
        {
            await AppendRangeAsync(wal1, 1, 66_000);
            await wal1.CommitAsync(default);
        }

        await using var wal2 = await options.Builder(fs).StartAsync();
        wal2.AllocatedIndex.Should().Be(66_000, "恢复扫描必须跨页读到全部 entry（frameless 缓冲扇区余量按 SectorSize）");
        wal2.PersistedIndex.Should().Be(66_000);

        long count = 0;
        long first = 0, last = 0;
        await foreach (var e in wal2.ReadFromAsync(1, default))
        {
            if (count == 0) first = e.Index;
            last = e.Index;
            count++;
        }
        count.Should().Be(66_000, "从头重放必须跨页完整");
        first.Should().Be(1);
        last.Should().Be(66_000);
    }

    [Fact]
    public async Task Catchup_Virtual_ImportThenDeltaReplay()
    {
        using var fs = NewVirtualVolume();
        var options = GroupCommitOptions();
        var image = new MemoryAsyncTransferPersistence();

        // 主节点：60,000 条（跨页）→ 一体快照（N₀=60,000）→ 导出经注入传输面 → 增量 6,000 条（跨页尾）
        await using (var wal1 = await options.Builder(fs).WithSnapshotPersistence(image).StartAsync())
        {
            await AppendRangeAsync(wal1, 1, 60_000);
            await wal1.CommitAsync(default);
            await wal1.SnapshotAsync(default);
            await wal1.ExportSnapshotAsync(default);
            await AppendRangeAsync(wal1, 60_001, 6_000);
            await wal1.CommitAsync(default);
        }

        // 冷节点：导入 → SnapshotIndex 恢复 → 从 N₀+1 重放增量（跨页衔接）
        var seeded = new MemoryAsyncTransferPersistence();
        seeded.Seed(image.CommittedImage!.Value);
        await using var wal2 = await options.Builder(fs).WithSnapshotPersistence(seeded).StartAsync();
        await wal2.ImportSnapshotAsync(default);
        wal2.SnapshotIndex.Should().Be(60_000);

        long replayed = 0;
        long first = 0;
        await foreach (var e in wal2.ReadFromAsync(60_001, default))
        {
            if (replayed == 0) first = e.Index;
            replayed++;
        }
        replayed.Should().Be(6_000, "快照后增量必须完整重放（跨页衔接不断链）");
        first.Should().Be(60_001);
    }
}
