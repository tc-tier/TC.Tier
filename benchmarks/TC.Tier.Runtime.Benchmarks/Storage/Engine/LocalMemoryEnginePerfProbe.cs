
namespace TC.Tier.Runtime.Benchmarks.Storage.Engine;

/// <summary>
/// StorageEngineBase 性能 probe——测改造后新版本 lease 锁外 IO 集成路径吞吐。
/// 与 LogicalAddressRegistryPerfProbe 对比定位瓶颈层（纯逻辑 vs 含字节拷贝集成）。
/// </summary>
public static class LocalMemoryEnginePerfProbe
{
    public static int Run(string[] args)
    {
        int[] threadCounts = new[] { 1, 2, 4, 6, 8, 12, 16 };
        long segSize = 64 * 1024;
        int payload = 64;
        int opsPerThread = 50_000;

        Console.WriteLine("=== StorageEngineBase Perf (new lease IO) ===");
        Console.WriteLine($"SegSize:{segSize / 1024}KB Payload:{payload}B Ops/thread:{opsPerThread}");
        Console.WriteLine($"{"T",3} {"Time",8} {"ops/s",12} {"p50",8} {"p99",8} {"p999",8}");
        Console.WriteLine(new string('-', 60));

        foreach (int threads in threadCounts)
            RunAppend(threads, opsPerThread, segSize, payload);
        return 0;
    }

    private static void RunAppend(int threads, int opsPerThread, long segSize, int payload)
    {
        var vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:）
        try
        {
            var options = new StorageEngineOptions("memperf", segmentGrowthLimit: segSize).WithPreallocateFile(true);
            using var dev = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();

            var data = new byte[payload];
            int totalOps = opsPerThread * threads;
            var latencies = new long[totalOps];
            var sw = Stopwatch.StartNew();
            var tasks = new Task[threads];
            for (int t = 0; t < threads; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        long t0 = sw.ElapsedTicks;
                        dev.Append(data);
                        latencies[tid * opsPerThread + i] = sw.ElapsedTicks - t0;
                    }
                });
            }
            if (!Task.WaitAll(tasks, TimeSpan.FromSeconds(60)))
            {
                Console.WriteLine($"{threads,2}T  TIMEOUT (possible deadlock)");
                return;
            }
            sw.Stop();
            PrintStats(threads, totalOps, sw, latencies);
        }
        finally { vol.Dispose(); }
    }

    private static void PrintStats(int threads, int totalOps, Stopwatch sw, long[] latencies)
    {
        Array.Sort(latencies);
        double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        double elapsed = sw.ElapsedMilliseconds / 1000.0;
        double throughput = totalOps / elapsed;
        double p50 = latencies[totalOps / 2] * nsPerTick / 1000.0;
        double p99 = latencies[(int)(totalOps * 0.99)] * nsPerTick / 1000.0;
        double p999 = latencies[(int)(totalOps * 0.999)] * nsPerTick / 1000.0;
        Console.WriteLine($"{threads,2}T {elapsed,7:F1}s {throughput / 1000,11:F1}K {p50,7:F1}us {p99,7:F1}us {p999,7:F1}us");
    }
}
