using System.Diagnostics;
using TC.Tier.Runtime.AddressSpace;

namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// ★ L19 销案性能论证（2026-08-22）：占用扫描协议在十万级区间表上的成本曲线。
/// <para>场景：N 条互不相交终态 Committed 记录 + 一条整段在途 CompactLeased 宽记录（L19 磁盘实锤形态）。
/// 测量 CanAcquireUnsafe 探测延迟随 N 的增长率——协议要求 O(log n + k + m)（二分 + 在途小表），
/// 全表线性扫描会随 N 线性放大（违反底层组件原则，已否决）。</para>
/// </summary>
public static class ExtentTableScanPerfProbe
{
    public static void Run()
    {
        Console.WriteLine("=== ExtentTable Scan Perf (CanAcquireUnsafe) ===");
        Console.WriteLine("形态 A：纯互不相交终态记录（二分基线）");
        Console.WriteLine("形态 B：+ 整段在途宽记录（L19 场景——终态表二分 + _outstanding 小表）");

        foreach (var n in new[] { 1_000, 10_000, 100_000 })
        {
            const long step = 512;

            // ── 形态 A ──
            var segA = new Segment(0, maxOffset: n * step, growthLimit: n * step,
                stableState: StableState.Ready, compactThreshold: 256);
            for (var i = 0; i < n; i++)
                segA.InsertUnsafe(i * step, (i + 1) * step, ExtentStateCode.Committed, refresh: false);
            var nsA = Measure(segA, n, n * step);

            // ── 形态 B ──
            var segB = new Segment(0, maxOffset: n * step, growthLimit: n * step,
                stableState: StableState.Ready, compactThreshold: 256);
            for (var i = 0; i < n; i++)
                segB.InsertUnsafe(i * step, (i + 1) * step, ExtentStateCode.Committed, refresh: false);
            // 整段在途宽记录（start=0 < VisibleOffset → 中间插入路径，位于终态记录之前——磁盘实锤形态）
            segB.InsertUnsafe(0, n * step, ExtentStateCode.CompactLeased, refresh: false);
            var nsB = Measure(segB, n, n * step);

            Console.WriteLine($"N={n,7:N0}:  A(纯终态)={nsA,7:N0}ns/探测   B(+宽在途)={nsB,7:N0}ns/探测   增长率 vs N=1k 基准见下");
        }

        Console.WriteLine("结论判定：形态 B 相对形态 A 的增量应为常数级（在途小表 O(m)，m=1）；");
        Console.WriteLine("若任一形态随 N 线性放大 = 二分协议被破坏（需改方案，不得以全扫换正确性）。");
    }

    private static double Measure(Segment seg, int recordCount, long totalLen)
    {
        var rng = new Random(42);
        // 预热
        using (var lk = seg.AcquireExtentLock())
        {
            for (var i = 0; i < 1000; i++)
                seg.CanAcquireUnsafe(rng.NextInt64(totalLen - 512), rng.NextInt64(totalLen - 512), ExtentStateCode.AppendLeased);
        }

        const int probes = 20_000;
        var starts = new long[probes];
        for (var i = 0; i < probes; i++)
            starts[i] = rng.NextInt64(0, Math.Max(1, totalLen - 512));
        var sw = Stopwatch.StartNew();
        long t0 = sw.ElapsedTicks;
        using (var lk = seg.AcquireExtentLock())
        {
            for (var i = 0; i < probes; i++)
                seg.CanAcquireUnsafe(starts[i], starts[i] + 512, ExtentStateCode.AppendLeased);
        }
        long t1 = sw.ElapsedTicks;
        var ns = (t1 - t0) * 1e9 / Stopwatch.Frequency / probes;
        _ = recordCount;
        return ns;
    }
}
