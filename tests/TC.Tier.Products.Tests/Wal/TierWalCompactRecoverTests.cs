using TC.Tier.Core.IO;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 压缩/安装后持久化与恢复一致性回归（raft follower 序列的 WAL 层最小重建）：
/// append 1..25（逐条 commit）→ SnapshotAsync（物理截头）→ append 26 → Dispose → 重启恢复——
/// 核对恢复态（alloc/persisted/snapshot/内容）在 local+DIO+WT 与 mem 的一致性。
/// （截尾越头为非法操作——快照覆盖区由适配器"落入快照区"守卫挡，不在此列。）
/// </summary>
public class TierWalCompactRecoverTests
{
    private static async Task<TierWal> StartAsync(IFileSystem fs, string name)
        => await TierWalOptions.Default
            .WithWalName(name)
            .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
            .WithMaxUnflushedBytes(long.MaxValue)
            .WithMaxUnflushedCount(int.MaxValue)
            .Builder(fs)
            .StartAsync();

    private static byte[] Frame(int i) => BitConverter.GetBytes(0x5A5A0000 + i);

    [Theory]
    [InlineData("mem")]
    [InlineData("local")]
    public async Task Compact_Recover_WatermarkConsistency(string medium)
    {
        var dir = TestTempDir.Create($"tc-wal-diag3-{medium}");
        var fs = medium == "mem"
            ? TierFs.New("memory:")
            : TierFs.New($"local:///{dir.Replace('\\', '/')}/vol.tier");
        try
        {
            // 1. 逐条 append+commit（AE 应答前持久化契约）
            var wal = await StartAsync(fs, "wal");
            for (var i = 1; i <= 25; i++)
            {
                await wal.AppendBatchAsync([Frame(i)], default);
                await wal.CommitAsync(default);
                await wal.WaitForPersistedAsync(i, default);
            }
            wal.AllocatedIndex.Should().Be(25);
            wal.PersistedIndex.Should().Be(25);

            // 2. 宿主压缩（一体快照+物理截头）
            (await wal.SnapshotAsync(default)).Should().Be(25);
            wal.SnapshotIndex.Should().Be(25);
            wal.AllocatedIndex.Should().Be(25, "压缩后分配尾不变（尾语义）");
            wal.PersistedIndex.Should().Be(25, "压缩后持久化水位不变");

            // 3. 压缩后继续追加（raft 复制增量）
            await wal.AppendBatchAsync([Frame(26)], default);
            await wal.CommitAsync(default);
            await wal.WaitForPersistedAsync(26, default);
            wal.AllocatedIndex.Should().Be(26);

            // 4. 重启恢复——核对压缩后 checkpoint 恢复一致性
            await wal.DisposeAsync();
            var wal2 = await StartAsync(fs, "wal");
            wal2.SnapshotIndex.Should().Be(25, "恢复后快照视图");
            wal2.AllocatedIndex.Should().Be(26, "恢复后分配尾（不得回退陈旧代）");
            wal2.PersistedIndex.Should().Be(26, "恢复后持久化水位（不得回退陈旧代）");
            var read = new List<int>();
            await foreach (var e in wal2.ReadFromAsync(26, default))
                read.Add(BitConverter.ToInt32(e.Data.ToArray()) - 0x5A5A0000);
            read.Should().BeEquivalentTo([26], "恢复后增量可读");
            await wal2.DisposeAsync();
        }
        finally
        {
            fs.Dispose();
            TestTempDir.TryCleanup(dir);
        }
    }

    /// <summary>安装形态：TruncateSuffix(0) 清空 → 重灌 [1..N₀] → commit → 恢复（适配器 Import 的 WAL 面）。</summary>
    [Theory]
    [InlineData("mem")]
    [InlineData("local")]
    public async Task Import_Clear_Reappend_WatermarkConsistency(string medium)
    {
        var dir = TestTempDir.Create($"tc-wal-diag2-{medium}");
        var fs = medium == "mem"
            ? TierFs.New("memory:")
            : TierFs.New($"local:///{dir.Replace('\\', '/')}/vol.tier");
        try
        {
            var wal = await StartAsync(fs, "wal");
            // 预置脏尾（旧任期分叉——安装前形态）
            for (var i = 1; i <= 5; i++)
            {
                await wal.AppendBatchAsync([Frame(100 + i)], default);
                await wal.CommitAsync(default);
            }

            // 安装：清全部 → 重灌 [1..25] → commit（TierWalRaftStore.ImportSnapshotAsync 的 WAL 面）
            await wal.TruncateSuffixAsync(0, default);
            wal.AllocatedIndex.Should().Be(0, "清空后分配尾=0");
            var batch = Enumerable.Range(1, 25).Select(i => (ReadOnlyMemory<byte>)Frame(i)).ToArray();
            var r = await wal.AppendBatchAsync(batch, default);
            r.StartIndex.Should().Be(1, "重灌起始 index");
            await wal.CommitAsync(default);
            wal.AllocatedIndex.Should().Be(25, "重灌后分配尾");
            wal.PersistedIndex.Should().Be(25, "重灌后持久化水位");

            // 恢复核对
            await wal.DisposeAsync();
            var wal2 = await StartAsync(fs, "wal");
            wal2.AllocatedIndex.Should().Be(25, "安装后恢复分配尾");
            wal2.PersistedIndex.Should().Be(25, "安装后恢复持久化水位");
            var first = (await ReadAllAsync(wal2, 1))[0];
            BitConverter.ToInt32(first.Data.ToArray()).Should().Be(0x5A5A0001, "首条内容");
            await wal2.DisposeAsync();
        }
        finally
        {
            fs.Dispose();
            TestTempDir.TryCleanup(dir);
        }
    }

    private static async Task<List<TC.Tier.Products.Wal.WalEntry>> ReadAllAsync(TierWal wal, long from)
    {
        var list = new List<TC.Tier.Products.Wal.WalEntry>();
        await foreach (var e in wal.ReadFromAsync(from, default))
            list.Add(e);
        return list;
    }
}
