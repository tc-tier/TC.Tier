using System.Diagnostics;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

// ★ CA1001 抑制：探针生命周期由 Run 管理
#pragma warning disable CA1001

namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// M5 高并发 Append 探针——IsAllocatedBelow 快路径 no-op cmpxchg16b 的缓存行竞争验证：
/// 多线程并发 AppendLease（每笔含 1 次推进 CAS + 1 次 IsAllocatedBelow no-op CAS）——
/// 高并发下 cmpxchg16b RMW 的共享缓存行乒乓是真实热点（用户已实测确认）。
/// 用法：--lease-append-probe [threads] [seconds]
/// </summary>
internal static class LeaseAppendProbe
{
    public static int Run(string[] args)
    {
        var threads = args.Length > 1 && int.TryParse(args[1], out var t) ? t : 4;
        var seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 5;
        const int growthLimit = 512 * 1024 * 1024;
        const int unitLen = 4096;

        using var table = new SegmentTable(
            new SegmentTableSettings(growthLimit, 0, IndexCapacity: 64, SpinMilliseconds: 60_000),
            LeaseFactory.Default);
        var sw = Stopwatch.StartNew();
        var ops = new long[threads];
        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var local = 0L;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                using var lease = table.AppendLease(unitLen);
                lease.Commit();
                local++;
            }
            ops[t] = local;
        })).ToArray();
        Task.WaitAll(tasks);
        var total = ops.Sum();
        Console.WriteLine($"高并发 AppendLease × {threads} 线程：{total / 1e6:F2}M ops/s"
            + $"（{total / sw.Elapsed.TotalSeconds / 1e6:F2}M ops/s 实际）");
        return 0;
    }
}
