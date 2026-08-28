using TC.Tier.Products.Tests.Wal;
using TC.Tier.Runtime.Benchmarks.Storage;

namespace TC.Tier.Products.Benchmarks.Wal;

/// <summary>
/// 契约③：冷节点追赶 ↔ 收敛时间（设计稿 §3）。
/// <para>落后节点 = 快照传输 + 快照后 entryLog 增量重放。存储侧口径：快照导出吞吐 + 导入 +
///   从 N₀+1 增量重放 = 收敛时间（快照传输用内存面近零——真实网络传输不在存储侧考验范围）。</para>
/// <para>流程：主节点写 count 条提交 → 一体快照（N₀=count）→ 导出（经注入传输面）→ 继续写 delta
///   （快照在途日志照常）→ 冷节点导入 → 重放 (N₀, N₀+delta]。</para>
/// </summary>
public static class WalCatchupProbe
{
    public static async Task Run(string spec, int count = 200_000, int delta = 20_000, int entrySize = 64, string hints = "none")
    {
        WalProbeCommon.PrintHeader($"契约③ 冷节点追赶（{spec}，快照 {count:N0} 条 + 增量 {delta:N0} 条 × {entrySize}B，IO 模式 {WalProbeCommon.HintsName(WalProbeCommon.HintsOf(hints))}）");

        using var vol = new BenchVolume(WalProbeCommon.SpecOf(spec));
        var options = WalProbeCommon.GroupCommit().WithHints(WalProbeCommon.HintsOf(hints));

        double exportSeconds;
        long imageBytes;
        long n0;
        var image = new MemoryAsyncTransferPersistence();

        // === 主节点：写 count 条提交 → 一体快照 → 导出 → 快照在途写 delta ===
        await using (var wal1 = await options.Builder(vol.Fs).WithSnapshotPersistence(image).StartAsync())
        {
            await AppendRangeAsync(wal1, 1, count, entrySize).ConfigureAwait(false);
            await wal1.CommitAsync(default).ConfigureAwait(false);
            Console.WriteLine($"主节点：{count:N0} 条已持久化（PersistedIndex={wal1.PersistedIndex:N0}）");

            // 一体快照（N₀ = 当前 PersistedIndex——镜像帧流 + 截断）
            var snapSw = Stopwatch.StartNew();
            n0 = await wal1.SnapshotAsync(default).ConfigureAwait(false);
            snapSw.Stop();
            Console.WriteLine($"快照生成：N0={n0:N0}，耗时 {snapSw.Elapsed.TotalSeconds:F2}s" +
                $"（{count / snapSw.Elapsed.TotalSeconds:N0} 条/s）");

            // 快照导出（经注入传输面——Header N₀ + 帧流 + Footer）
            var sw = Stopwatch.StartNew();
            await wal1.ExportSnapshotAsync(default).ConfigureAwait(false);
            sw.Stop();
            exportSeconds = sw.Elapsed.TotalSeconds;
            imageBytes = image.CommittedImage!.Value.Length;
            Console.WriteLine($"快照导出：N0={n0:N0}，{imageBytes / 1024.0 / 1024.0:F2} MB，" +
                $"耗时 {exportSeconds:F2}s（{count / exportSeconds:N0} 条/s，" +
                $"{imageBytes / 1024.0 / 1024.0 / exportSeconds:F1} MB/s）");

            // 快照在途：继续写 delta（导出后增量——快照内容与 (N₀, 尾] 衔接）
            await AppendRangeAsync(wal1, count + 1, delta, entrySize).ConfigureAwait(false);
            await wal1.CommitAsync(default).ConfigureAwait(false);
            Console.WriteLine($"增量写入：{delta:N0} 条（PersistedIndex={wal1.PersistedIndex:N0}）");
        }

        // === 冷节点：导入 + 增量重放 ===
        var seeded = new MemoryAsyncTransferPersistence();
        seeded.Seed(image.CommittedImage!.Value);
        await using var wal2 = await options.Builder(vol.Fs).WithSnapshotPersistence(seeded).StartAsync();

        var imp = Stopwatch.StartNew();
        await wal2.ImportSnapshotAsync(default).ConfigureAwait(false);
        imp.Stop();
        Console.WriteLine($"冷节点导入：耗时 {imp.Elapsed.TotalSeconds:F3}s，SnapshotIndex={wal2.SnapshotIndex:N0}");

        long replayed = 0;
        var rep = Stopwatch.StartNew();
        await foreach (var e in wal2.ReadFromAsync(n0 + 1, default).ConfigureAwait(false))
        {
            replayed++;
            if (replayed == 1 && e.Index != n0 + 1)
                Console.WriteLine($"  [警告] 首条 index={e.Index} != 预期 {n0 + 1}");
        }
        rep.Stop();

        var converge = exportSeconds + imp.Elapsed.TotalSeconds + rep.Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"增量重放：{replayed:N0} 条，耗时 {rep.Elapsed.TotalSeconds:F2}s（{replayed / rep.Elapsed.TotalSeconds:N0} 条/s）");
        Console.WriteLine($"收敛时间（存储侧）：导出 {exportSeconds:F2}s + 导入 {imp.Elapsed.TotalSeconds:F3}s + 重放 {rep.Elapsed.TotalSeconds:F2}s = {converge:F2}s");
    }

    private static async Task AppendRangeAsync(ITierWal wal, int firstIndex, int n, int entrySize)
    {
        const int batchSize = 1000;
        for (var done = 0; done < n; done += batchSize)
        {
            var m = Math.Min(batchSize, n - done);
            var batch = new ReadOnlyMemory<byte>[m];
            for (var j = 0; j < m; j++) batch[j] = WalProbeCommon.Entry(firstIndex + done + j, entrySize);
            await wal.AppendBatchAsync(batch, default).ConfigureAwait(false);
        }
    }
}
