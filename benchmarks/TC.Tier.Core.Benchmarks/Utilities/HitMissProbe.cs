using System.Buffers;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// 最小复现：验证 PinnedBufferPool 在同线程 rent-return 循环下是否真的命中。
/// 不靠猜——直接打印 hits/misses，定位 ScalabilityBench 全 miss 的根因。
/// 用法：dotnet run -c Release -- --probe-hits
/// </summary>
internal static class HitMissProbe
{
    public static int Run()
    {
        Console.WriteLine("=== HitMissProbe：同线程 rent-return 循环命中率 ===");
        using var pool = new PinnedBufferPool(maxPerBucket: 4);

        // 单线程：100 次 rent-return，应全命中（1 miss + 99 hits）
        for (int i = 0; i < 100; i++)
        {
            var b = pool.Rent(4096);
            pool.Return(b);
        }
        Console.WriteLine($"单线程 100 次：hits={pool.CacheHits}, misses={pool.CacheMisses}");
        Console.WriteLine($"  期望：misses=1（首次）, hits=99");

        // 多线程：4 线程各 1000 次
        var pool2 = new PinnedBufferPool(maxPerBucket: 16);
        var threads = new Thread[4];
        for (int t = 0; t < 4; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var b = pool2.Rent(4096);
                    pool2.Return(b);
                }
            }) { IsBackground = true };
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();
        Console.WriteLine($"4 线程各 1000 次：hits={pool2.CacheHits}, misses={pool2.CacheMisses}");
        Console.WriteLine($"  期望：misses≈4（每线程首次）, hits≈3996");
        Console.WriteLine($"  实际 misses={pool2.CacheMisses} → {(pool2.CacheMisses <= 8 ? "✅ 命中正常" : "❌ 持续 miss（池有 bug 或 bench 配置问题）")}");

        return 0;
    }
}
