using TC.Tier.Core.IO;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 多节点传输模拟（设计稿 §4——网关语义）：leader 的 WAL 服务多个 follower 同步——
/// 快照安装（快照文件传输 → 冷节点导入）+ 冷节点汇报 N₀ + leader 推增量（AppendEntries 语义）→ 追平；
/// 多 follower 并发扇出；冷节点追平后重启自动载入。
/// ★ 传输 = 进程内 MemoryAsyncTransferPersistence（一个节点的 writer 会话 = 另一节点的 reader 会话——
///   模拟节点间连接，零网络依赖；后续可换真实 socket 模拟延迟/断连）。
/// </summary>
public class TierWalMultiNodeTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("tc-wal-multinode");
    private readonly List<IFileSystem> _fss = [];

    public void Dispose()
    {
        foreach (var fs in _fss) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
        GC.SuppressFinalize(this);
    }

    /// <summary>独立节点卷（不同引擎名——节点隔离）。</summary>
    private IFileSystem NewNodeVolume(string name)
    {
        var vol = Path.Combine(_dir, $"node-{name}-{Guid.NewGuid():N}.tier");
        var fs = TierFs.New($"virtual:///{vol.Replace('\\', '/')}");
        _fss.Add(fs);
        return fs;
    }

    private static TierWalOptions NodeOptions(string name) => TierWalOptions.Default
        .WithWalName($"wal-{name}")
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
            for (var j = 0; j < m; j++) batch[j] = WalTestFactory.Entry((int)(firstIndex + done + j));
            await wal.AppendBatchAsync(batch, default);
        }
    }

    /// <summary>
    /// 场景 1-4（设计稿 §4.1）：leader 写入 → 快照压缩 → 导出 → 冷节点安装（Import）→
    /// 冷节点汇报 N₀ → leader 从 (N₀, 尾] 推增量 → 追平。
    /// </summary>
    [Fact]
    public async Task LeaderSnapshot_InstallToColdNode_ReportN0_PushDelta_Catchup()
    {
        var leaderFs = NewNodeVolume("leader");
        var coldFs = NewNodeVolume("cold");
        var transfer = new MemoryAsyncTransferPersistence();

        // 1. leader：60,000 条 → 一体快照（N₀=60,000——head 截断）→ 导出（经注入传输面）
        long n0;
        await using (var leader = await NodeOptions("leader").Builder(leaderFs)
            .WithSnapshotPersistence(transfer).StartAsync())
        {
            await AppendRangeAsync(leader, 1, 60_000);
            await leader.CommitAsync(default);
            n0 = await leader.SnapshotAsync(default);
            n0.Should().Be(60_000);
            await leader.ExportSnapshotAsync(default);
            transfer.CommittedImage.Should().NotBeNull();

            // 2. 快照在途：leader 继续写（(N₀, 尾] 保留）
            await AppendRangeAsync(leader, 60_001, 5_000);
            await leader.CommitAsync(default);
            leader.AllocatedIndex.Should().Be(65_000);

            // 3. 冷节点（独立卷）：导入快照（安装）→ SnapshotIndex=N₀ → 镜像重建（帧流 60,000 条）
            var seeded = new MemoryAsyncTransferPersistence();
            seeded.Seed(transfer.CommittedImage!.Value);   // 传输来的像（进程内传输）
            await using var cold = await NodeOptions("cold").Builder(coldFs)
                .WithSnapshotPersistence(seeded).StartAsync();
            await cold.ImportSnapshotAsync(default);
            cold.SnapshotIndex.Should().Be(60_000, "冷节点安装快照——覆盖点恢复");
            var image = await TierWalSnapshotTests.ReadSnapshotAll(cold);
            TierWalSnapshotTests.CountSnapshotFrames(image).Should().Be(60_000, "镜像 = [Head..N₀] 全部条目");
            (await TierWalTests.ReadAll(cold, 60_001, default)).Should().BeEmpty("冷节点本地 entryLog 无增量");

            // 4. 冷节点汇报 N₀=60,000 → leader 从 (60,000, 尾] 推增量 → 追平（follower 本地续接）
            await PushDeltaCore(leader, cold, n0 + 1, default);

            cold.AllocatedIndex.Should().Be(5_000, "冷节点本地 5,000 条（raft 层 index 映射——本地顺序）");
            var first = (await TierWalTests.ReadAll(cold, 1, default))[0];
            first.Data.ToArray().Should().Equal(WalTestFactory.Entry(60_001), "首条内容 = leader 第 60,001 条");
        }
    }

    /// <summary>
    /// 场景 5（网关扇出）：多 follower 并发同步——同一快照像分发 + 增量分别推送 → 全部追平。
    /// </summary>
    [Fact]
    public async Task MultiFollower_FanoutSync_AllCatchUp()
    {
        var leaderFs = NewNodeVolume("leader");
        var f1Fs = NewNodeVolume("f1");
        var f2Fs = NewNodeVolume("f2");
        var transfer = new MemoryAsyncTransferPersistence();

        // leader：快照 + 导出 + 快照在途增量
        await using (var leader = await NodeOptions("leader").Builder(leaderFs)
            .WithSnapshotPersistence(transfer).StartAsync())
        {
            await AppendRangeAsync(leader, 1, 40_000);
            await leader.CommitAsync(default);
            await leader.SnapshotAsync(default);   // N₀ = 40,000
            await leader.ExportSnapshotAsync(default);
            await AppendRangeAsync(leader, 40_001, 3_000);
            await leader.CommitAsync(default);
            leader.AllocatedIndex.Should().Be(43_000);

            // 网关扇出：同一像分发给两个 follower（各自独立卷 + 注入各自的传输面）
            foreach (var (fFs, name) in new[] { (f1Fs, "f1"), (f2Fs, "f2") })
            {
                var seeded = new MemoryAsyncTransferPersistence();
                seeded.Seed(transfer.CommittedImage!.Value);
                await using var f = await NodeOptions(name).Builder(fFs)
                    .WithSnapshotPersistence(seeded).StartAsync();
                await f.ImportSnapshotAsync(default);
                f.SnapshotIndex.Should().Be(40_000, $"{name} 安装快照");

                // leader 推增量（AppendEntries 语义——逐 follower 推送；内容对齐验证在 PushDeltaCore 内）
                await PushDeltaCore(leader, f, 40_001, default);

                f.AllocatedIndex.Should().Be(3_000, $"{name} 追平（本地顺序）");
            }
        }
    }

    /// <summary>冷节点追平后重启：自动载入快照 + 主数据增量——状态完整。</summary>
    [Fact]
    public async Task ColdNode_Restart_AfterCatchup_AutoLoads()
    {
        var leaderFs = NewNodeVolume("leader");
        var coldFs = NewNodeVolume("cold");
        var transfer = new MemoryAsyncTransferPersistence();

        await using (var leader = await NodeOptions("leader").Builder(leaderFs)
            .WithSnapshotPersistence(transfer).StartAsync())
        {
            await AppendRangeAsync(leader, 1, 30_000);
            await leader.CommitAsync(default);
            await leader.SnapshotAsync(default);   // N₀ = 30,000
            await leader.ExportSnapshotAsync(default);
            await AppendRangeAsync(leader, 30_001, 2_000);
            await leader.CommitAsync(default);

            // 冷节点：安装快照 + 追平
            var seeded = new MemoryAsyncTransferPersistence();
            seeded.Seed(transfer.CommittedImage!.Value);
            await using (var cold = await NodeOptions("cold").Builder(coldFs)
                .WithSnapshotPersistence(seeded).StartAsync())
            {
                await cold.ImportSnapshotAsync(default);
                await PushDeltaCore(leader, cold, 30_001, default);
                cold.AllocatedIndex.Should().Be(2_000);
            }
        }

        // ★ 冷节点重启（同卷）：自动载入快照（SnapshotIndex=N₀）+ 主数据增量——完整状态
        await using (var cold2 = await NodeOptions("cold").Builder(coldFs).StartAsync())
        {
            cold2.SnapshotIndex.Should().Be(30_000, "重启自动载入快照");
            cold2.AllocatedIndex.Should().Be(2_000);
            var image = await TierWalSnapshotTests.ReadSnapshotAll(cold2);
            TierWalSnapshotTests.CountSnapshotFrames(image).Should().Be(30_000, "镜像完整");
            var tail = await TierWalTests.ReadAll(cold2, 1, default);
            tail.Should().HaveCount(2_000, "主数据增量完整（本地顺序）");
            tail[^1].Data.ToArray().Should().Equal(WalTestFactory.Entry(32_000), "末条内容 = leader 第 32,000 条");
        }
    }

    /// <summary>
    /// 追平模拟（AppendEntries 语义——leader 推 (start, 尾] 增量给 follower）。
    /// ★ follower 本地 index 顺序分配（raft 层维护自己的 index 映射——TierWAL index 是存储顺序）；
    ///   验证 = follower 读回内容与 leader 推送条目一致（条目内容含 index 标识——"entry-{i}"）。
    /// </summary>
    private static async Task PushDeltaCore(TierWal leader, TierWal follower, long startIndex, CancellationToken ct)
    {
        var expected = new List<byte[]>();
        var batch = new List<ReadOnlyMemory<byte>>();
        await foreach (var e in leader.ReadFromAsync(startIndex, ct))
        {
            expected.Add(e.Data.ToArray());
            batch.Add(e.Data);
            if (batch.Count >= 1000)
            {
                await follower.AppendBatchAsync(batch, ct);
                await follower.CommitAsync(ct);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await follower.AppendBatchAsync(batch, ct);
            await follower.CommitAsync(ct);
        }

        // ★ 内容对齐验证：follower 本地 [1..N] 读回 = leader 推送条目（同一字节序）
        var actual = await TierWalTests.ReadAll(follower, 1, ct);
        actual.Should().HaveCount(expected.Count, "追平条目数一致");
        for (int i = 0; i < expected.Count; i++)
            actual[i].Data.ToArray().Should().Equal(expected[i], $"第 {i + 1} 条内容一致（raft 层 index 映射）");
    }
}
