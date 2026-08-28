using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Benchmarks.Storage;
using TC.Tier.Runtime.Structures.Snapshot;
using TC.Tier.Runtime.Storage;

namespace TC.Tier.Products.Benchmarks.Wal;

/// <summary>
/// 快照存储选型基准（设计稿 tierwal-mirror-snapshot-design.md §3.4——方案 A/B 数字定结构）。
/// <para>场景 = raft 定期压缩：镜像累计 <paramref name="imageMb"/> MB（条目流）、
///   <paramref name="snapshots"/> 次快照、增量均匀（每次 = 镜像/snapshots）：</para>
/// <para> - 方案 B 全量重写：每次快照 = 读源全量 [Head..N₀] 条目 → StreamSnapshot 新帧 → 截旧帧（原子替换）</para>
/// <para> - 方案 A 段增量：每次快照 = 读源增量 (N₀_old, N₀_new] 条目 → StreamSnapshot 追加新段（帧），读时顺序拼接</para>
/// <para> - 回收：B = 每次 TruncatePrefix 截旧（逻辑截断）；A = 段累积 → 合并为新基线段（低频全量重写——
///   成本 = B 单次全量写，对照引用不另测）</para>
/// <para>★ 同机同轮同形：同一介质两卷、同 StreamSnapshot 帧几何；写帧内容 = [N₀ 8B] + 条目流（64B/条），
///   条目即时生成（两方案同口径——B 生成全量、A 生成增量，处理字节量差异如实入账）。</para>
/// </summary>
public static class WalSnapshotStoreProbe
{
    public static async Task Run(string spec, int imageMb = 256, int snapshots = 4, int entrySize = 64, string hints = "none")
    {
        WalProbeCommon.PrintHeader(
            $"快照存储选型：方案 A（段增量） vs 方案 B（全量重写）——镜像 {imageMb}MB × {snapshots} 次快照 × {entrySize}B 条目，" +
            $"介质 {spec} / {WalProbeCommon.HintsName(WalProbeCommon.HintsOf(hints))}");

        long totalEntries = (long)imageMb * 1024 * 1024 / entrySize;   // 最终镜像条数
        long deltaEntries = totalEntries / snapshots;                  // 每次快照增量条数

        // ═══ 方案 B：全量重写 + 原子替换 ═══
        Console.WriteLine("[方案 B 全量重写]");
        using (var vol = new BenchVolume(WalProbeCommon.SpecOf(spec)))
        {
            await using var snapshot = NewSnapshot(vol.Fs, "snap-b");
            snapshot.Initialize();
            await snapshot.WaitForReadyAsync(default).ConfigureAwait(false);

            long totalWriteBytes = 0;
            double totalWriteSeconds = 0;
            var lastStart = snapshot.WriteAddress;
            for (int k = 1; k <= snapshots; k++)
            {
                long n0 = k * deltaEntries;
                var frameStart = snapshot.WriteAddress;   // 本帧起点（写前）
                var (seconds, bytes) = await WriteFrameAsync(snapshot, n0, deltaEntries, entrySize,
                    /*全量 = 从头到 n0*/ n0).ConfigureAwait(false);
                totalWriteSeconds += seconds;
                totalWriteBytes += bytes;
                Console.WriteLine($"  快照 {k}（N0={n0:N0}）：写 {bytes / 1024.0 / 1024.0:F1} MB，{seconds:F2}s" +
                    $"（{bytes / seconds / 1024.0 / 1024.0:F1} MB/s）");

                // ★ 原子替换：新帧完整落盘后截旧帧（TierWAL 旧实现同模式——截到本帧起点 = 只保留最新帧）。
                //   ★ 引擎 ReclaimHead 打洞要求地址对齐 AllocationUnit（4096）——帧起点 = 上帧尾（不对齐），
                //     截断边界向下对齐（保留区含上帧尾部 ≤4KB 碎片；读从精确帧起点起——碎片不可见）。
                if (k > 1) snapshot.TruncatePrefix(new LogicalAddress(frameStart.SegId, AlignDown4096(frameStart.Offset)));
                lastStart = frameStart;
            }
            Console.WriteLine($"  合计：写 {totalWriteBytes / 1024.0 / 1024.0:F1} MB / {totalWriteSeconds:F2}s" +
                $"（{totalWriteBytes / totalWriteSeconds / 1024.0 / 1024.0:F1} MB/s）+ 截旧 {snapshots - 1} 次（含在耗时内）");

            // 读回：单段读（最新全量）
            var (readSeconds, readBytes) = await ReadFramesAsync(snapshot, [lastStart]).ConfigureAwait(false);
            Console.WriteLine($"  读回：{readBytes / 1024.0 / 1024.0:F1} MB / {readSeconds:F2}s" +
                $"（{readBytes / readSeconds / 1024.0 / 1024.0:F1} MB/s，单段）");
        }

        // ═══ 方案 A：段增量追加 + 拼接读 ═══
        Console.WriteLine();
        Console.WriteLine("[方案 A 段增量]");
        using (var vol = new BenchVolume(WalProbeCommon.SpecOf(spec)))
        {
            await using var snapshot = NewSnapshot(vol.Fs, "snap-a");
            snapshot.Initialize();
            await snapshot.WaitForReadyAsync(default).ConfigureAwait(false);

            var frameStarts = new List<LogicalAddress> { snapshot.WriteAddress };   // 段起点（逻辑地址）
            long totalWriteBytes = 0;
            double totalWriteSeconds = 0;
            for (int k = 1; k <= snapshots; k++)
            {
                long n0 = k * deltaEntries;
                var (seconds, bytes) = await WriteFrameAsync(snapshot, n0, deltaEntries, entrySize,
                    /*增量 = deltaEntries*/ deltaEntries).ConfigureAwait(false);
                totalWriteSeconds += seconds;
                totalWriteBytes += bytes;
                Console.WriteLine($"  快照 {k}（N0={n0:N0}）：写 {bytes / 1024.0 / 1024.0:F1} MB（增量），{seconds:F2}s" +
                    $"（{bytes / seconds / 1024.0 / 1024.0:F1} MB/s）");
                frameStarts.Add(snapshot.WriteAddress);
            }
            Console.WriteLine($"  合计：写 {totalWriteBytes / 1024.0 / 1024.0:F1} MB / {totalWriteSeconds:F2}s" +
                $"（{totalWriteBytes / totalWriteSeconds / 1024.0 / 1024.0:F1} MB/s，无重写旧段）");

            // 读回：逐段顺序拼接（模拟段表遍历 + 段内帧读）
            var (readSeconds, readBytes) = await ReadFramesAsync(snapshot, frameStarts).ConfigureAwait(false);
            Console.WriteLine($"  读回（拼接 {snapshots} 段）：{readBytes / 1024.0 / 1024.0:F1} MB / {readSeconds:F2}s" +
                $"（{readBytes / readSeconds / 1024.0 / 1024.0:F1} MB/s）");
        }

        // ═══ 回收对照 ═══
        Console.WriteLine();
        Console.WriteLine("[回收对照]");
        Console.WriteLine($"  B：每次快照截旧（TruncatePrefix 逻辑截断）——已含在写耗时内，累计 {snapshots - 1} 次");
        Console.WriteLine($"  A：段累积合并 = 全量重写一次（低频触发）——成本对照 = B 最后一次全量写" +
            $"（{totalEntries * entrySize / 1024.0 / 1024.0:F1} MB 镜像规模，见上表）");
    }

    /// <summary>新建快照结构（同规格两卷——帧几何一致，同形对照）。</summary>
    private static StreamSnapshot NewSnapshot(IFileSystem fs, string name) => new(fs,
        new StreamSnapshotSettings(new StorageEngineOptions($"bench.{name}", 64L << 20,
            enableSegmentation: true, preallocateFile: false)));

    /// <summary>截断边界对齐（引擎 ReclaimHead 打洞契约：地址对齐 AllocationUnit 4096）。</summary>
    private static long AlignDown4096(long offset) => offset & ~4095L;

    /// <summary>
    /// 写一帧：[N₀ 8B] + entries 条条目（即时生成）。返回 (耗时, 写字节)。
    /// <paramref name="entries"/> = 全量条数（B）或增量条数（A）——处理字节量差异如实入账。
    /// </summary>
    private static async Task<(double Seconds, long Bytes)> WriteFrameAsync(
        StreamSnapshot snapshot, long n0, long deltaEntries, int entrySize, long entries)
    {
        var sw = Stopwatch.StartNew();
        var prefix = new byte[sizeof(long)];
        BitConverter.TryWriteBytes(prefix, n0);
        await using (var writer = snapshot.OpenWrite())
        {
            await writer.WriteAsync(prefix, default).ConfigureAwait(false);

            var buf = new byte[128 * 1024];
            int filled = 0;
            long baseIndex = n0 - deltaEntries + 1;   // 增量起点（即时生成条目标识）
            for (long i = 0; i < entries; i++)
            {
                var e = WalProbeCommon.Entry((int)(baseIndex + i), entrySize);
                if (filled + entrySize > buf.Length)
                {
                    await writer.WriteAsync(buf.AsMemory(0, filled), default).ConfigureAwait(false);
                    filled = 0;
                }
                e.CopyTo(buf, filled);
                filled += entrySize;
            }
            if (filled > 0)
                await writer.WriteAsync(buf.AsMemory(0, filled), default).ConfigureAwait(false);
            await writer.CompleteAsync(default).ConfigureAwait(false);
        }
        sw.Stop();
        return (sw.Elapsed.TotalSeconds, sizeof(long) + entries * entrySize);
    }

    /// <summary>
    /// 读回全部帧：每段 [start, nextStart) 区间顺序读（B = 单段最新全量；A = 逐段拼接），
    /// 最后一段终点 = 当前写尾。返回 (耗时, 读字节)。
    /// </summary>
    private static async Task<(double Seconds, long Bytes)> ReadFramesAsync(
        StreamSnapshot snapshot, List<LogicalAddress> frameStarts)
    {
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        var buf = new byte[128 * 1024];
        var end = snapshot.WriteAddress;
        for (int i = 0; i < frameStarts.Count; i++)
        {
            var segEnd = i + 1 < frameStarts.Count ? frameStarts[i + 1] : end;
            if (segEnd <= frameStarts[i]) continue;   // 哨兵元素 = 写尾（最后一段终点）——空区间跳过
            await using var reader = snapshot.OpenReadRange(frameStarts[i], segEnd);
            int n;
            while ((n = await reader.ReadDataAsync(buf, default).ConfigureAwait(false)) > 0)
                bytes += n;
        }
        sw.Stop();
        return (sw.Elapsed.TotalSeconds, bytes);
    }
}
