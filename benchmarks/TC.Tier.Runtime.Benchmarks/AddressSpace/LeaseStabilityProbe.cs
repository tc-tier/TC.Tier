using System.Diagnostics;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// lease 协议稳定性诊断——10 万次 AppendLease，监控 GC/内存/延迟趋势。
/// </summary>
public static class LeaseStabilityProbe
{
    public static void Run()
    {
        // 生产模式（无诊断），128MB 段，4KB 记录
        var table = new SegmentTable(
            new SegmentTableSettings(128 * 1024 * 1024, 0, IndexCapacity: 64, SpinMilliseconds: 60_000),
            LeaseFactory.Default);

        const int total = 100_000;
        const int reportEvery = 10_000;

        var latencies = new long[total];
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var memBefore = Process.GetCurrentProcess().WorkingSet64;

        Console.WriteLine($"=== Lease 稳定性诊断：{total:N0} 次 AppendLease（Default 无诊断）===");
        Console.WriteLine($"环境：{Environment.ProcessorCount} 逻辑核，.NET {Environment.Version}");
        Console.WriteLine();
        Console.WriteLine($"{"次数",8} | {"Gen0",6} | {"Gen1",6} | {"Gen2",6} | {"内存MB",8} | {"p50ns",8} | {"p99ns",8} | {"maxns",8}");
        Console.WriteLine(new string('-', 75));

        for (var i = 0; i < total; i++)
        {
            var sw = Stopwatch.GetTimestamp();
            using var lease = table.AppendLease(4096);
            lease.Commit();
            latencies[i] = Stopwatch.GetTimestamp() - sw;

            if ((i + 1) % reportEvery == 0)
            {
                var slice = latencies.AsSpan((i + 1 - reportEvery)..(i + 1));
                slice.Sort();
                var p50 = slice[slice.Length / 2];
                var p99 = slice[(int)(slice.Length * 0.99)];
                var max = slice[^1];
                // Stopwatch.GetTimestamp() 的单位 → ns（Frequency 是 ticks/sec）
                var tickToNs = 1_000_000_000.0 / Stopwatch.Frequency;
                Console.WriteLine($"{i + 1,8} | " +
                    $"{GC.CollectionCount(0) - gen0Before,6} | " +
                    $"{GC.CollectionCount(1) - gen1Before,6} | " +
                    $"{GC.CollectionCount(2) - gen2Before,6} | " +
                    $"{Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024,8} | " +
                    $"{(long)(p50 * tickToNs),8} | " +
                    $"{(long)(p99 * tickToNs),8} | " +
                    $"{(long)(max * tickToNs),8}");
            }
        }

        var memAfter = Process.GetCurrentProcess().WorkingSet64;
        Console.WriteLine();
        Console.WriteLine($"=== 汇总 ===");
        Console.WriteLine($"总耗时：{latencies.Sum() * 1_000_000_000.0 / Stopwatch.Frequency:F1} μs");
        Console.WriteLine($"平均延迟：{latencies.Average() * 1_000_000_000.0 / Stopwatch.Frequency:F1} ns/op");
        Console.WriteLine($"吞吐：{total / (latencies.Sum() * 1.0 / Stopwatch.Frequency):F0} ops/s");
        Console.WriteLine($"GC: Gen0={GC.CollectionCount(0) - gen0Before} Gen1={GC.CollectionCount(1) - gen1Before} Gen2={GC.CollectionCount(2) - gen2Before}");
        Console.WriteLine($"内存增长：{(memAfter - memBefore) / 1024.0 / 1024.0:F1} MB（{memBefore / 1024 / 1024}MB → {memAfter / 1024 / 1024}MB）");
        Console.WriteLine($"段数：{table.SegCount}，区间表条目（seg0）：{table.SnapshotSegmentExtents(0).Count}");

        // 排序全量延迟，输出分位数
        Array.Sort(latencies);
        var ns = latencies.Select(l => l * 1_000_000_000.0 / Stopwatch.Frequency).ToArray();
        Console.WriteLine($"延迟分位数：p50={ns[total/2]:F0}ns p90={ns[(int)(total*0.9)]:F0}ns p99={ns[(int)(total*0.99)]:F0}ns p99.9={ns[(int)(total*0.999)]:F0}ns max={ns[^1]:F0}ns");

        table.Dispose();
    }
}
