using TC.Tier.Runtime.Benchmarks.Storage;

namespace TC.Tier.Products.Benchmarks.Wal;

/// <summary>
/// 契约②：重放吞吐 ↔ 恢复时间 RTO（设计稿 §3）。
/// <para>节点重启从 entryLog 重放，重放速度 = 恢复时间。写入 count 条（组提交批量）→ 显式 Commit →
///   从随机起点（count/2）全量重放至尾——测<b>任意起点定位</b>（段表二分 + 段内扫帧）+
///   顺序重放吞吐（条/s、MB/s）。</para>
/// <para>基线：50 万条 9ms 是 Log 底层内存口径；TierWAL 上层帧解析口径待实测（磁盘为本契约主口径）。</para>
/// </summary>
public static class WalReplayProbe
{
    public static async Task Run(string spec, int count = 500_000, int entrySize = 64, int? startOverride = null, string hints = "none")
    {
        WalProbeCommon.PrintHeader($"契约② 重放吞吐（{spec}，{count:N0} 条 × {entrySize}B，IO 模式 {WalProbeCommon.HintsName(WalProbeCommon.HintsOf(hints))}）");

        using var vol = new BenchVolume(WalProbeCommon.SpecOf(spec));
        var options = WalProbeCommon.GroupCommit().WithHints(WalProbeCommon.HintsOf(hints));
        await using var wal = await WalProbeCommon.StartAsync(vol.Fs, options);

        // 写入：批 1000 组提交（AppendBatchAsync 攒批 + 单次 CommitAsync）
        var batchSize = 1000;
        var sw = Stopwatch.StartNew();
        for (var done = 0; done < count; done += batchSize)
        {
            var n = Math.Min(batchSize, count - done);
            var batch = new ReadOnlyMemory<byte>[n];
            for (var j = 0; j < n; j++) batch[j] = WalProbeCommon.Entry(done + j + 1, entrySize);
            await wal.AppendBatchAsync(batch, default).ConfigureAwait(false);
        }
        await wal.CommitAsync(default).ConfigureAwait(false);
        sw.Stop();
        Console.WriteLine($"写入 {count:N0} 条耗时 {sw.Elapsed.TotalSeconds:F2}s（组提交吞吐 {count / sw.Elapsed.TotalSeconds:N0} 条/s）");
        Console.WriteLine($"PersistedIndex={wal.PersistedIndex:N0} AllocatedIndex={wal.AllocatedIndex:N0}");

        // 重放起点（默认 count/2 固定口径；startOverride 供定位成本 O(1)/O(n) 对照实验）
        var startIndex = startOverride ?? count / 2;
        long replayed = 0;
        long bytes = 0;
        double firstEntryMs = -1;
        var replay = Stopwatch.StartNew();
        await foreach (var e in wal.ReadFromAsync(startIndex, default).ConfigureAwait(false))
        {
            if (replayed == 0) firstEntryMs = replay.Elapsed.TotalMilliseconds;
            replayed++;
            bytes += e.Data.Length;
        }
        replay.Stop();

        Console.WriteLine();
        Console.WriteLine($"重放起点 index={startIndex:N0} → 尾：共 {replayed:N0} 条 / {bytes / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"首次产出延迟（定位 = 段表二分 + 段内扫帧）：{firstEntryMs:F3} ms");
        Console.WriteLine($"重放吞吐：{replayed / replay.Elapsed.TotalSeconds:N0} 条/s，{bytes / 1024.0 / 1024.0 / replay.Elapsed.TotalSeconds:F1} MB/s");
        Console.WriteLine($"RTO 外推：100 万条 ≈ {1_000_000.0 / (replayed / replay.Elapsed.TotalSeconds):F2}s");
    }
}
