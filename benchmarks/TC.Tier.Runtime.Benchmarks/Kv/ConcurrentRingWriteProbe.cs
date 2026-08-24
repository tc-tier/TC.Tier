using System.Diagnostics;
using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ Ring 并发写吞吐探针——多写者无锁窗口改造的前后对照口径。
/// <para>变体：single-write（每线程逐条 Write——lock 串行基线）/ batch-write（每线程自己的
///   WriteBatch——批持锁版并发互斥基线 / 窗口领取版并发并行）。</para>
/// <para>口径：N 线程并发写总墙钟 → 吞吐（op/s）；mem 介质（写路径纯 CPU 口径）。
/// 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks --
///   --concurrent-write-probe [writers] [perWriter]</para>
/// </summary>
public static class ConcurrentRingWriteProbe
{
    public static void Run(int writers = 8, int perWriter = 20_000)
    {
        Console.WriteLine($"[probe] Ring 并发写吞吐 writers={writers} perWriter={perWriter} entry=64B medium=mem");
        RunVariant("single-write", writers, perWriter, useBatch: false);
        RunVariant("batch-write", writers, perWriter, useBatch: true);
    }

    private static void RunVariant(string name, int writers, int perWriter, bool useBatch)
    {
        using var fs = TierFs.New("memory:");
        using var ring = NewRing(fs);

        var value = new byte[64];
        new Random(7).NextBytes(value);
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            long keyBase = (long)w * perWriter;
            if (useBatch)
            {
                for (long k = 0; k < perWriter;)
                {
                    using var batch = ring.BeginWriteBatch();
                    int budget = 512;   // 批内 512 条后换新批（批粒度可控）
                    while (k < perWriter && budget-- > 0)
                    {
                        batch.Append(keyBase + k, value);
                        k++;
                    }
                }
            }
            else
            {
                for (long k = 0; k < perWriter; k++)
                    ring.Write(keyBase + k, value);
            }
        })).ToArray();

        Task.WaitAll(workers);
        sw.Stop();

        long total = (long)writers * perWriter;
        double secs = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"[probe] {name,-14} {total,10:N0} ops in {secs,6:F2}s → {total / secs,12:N0} op/s  ({writers} writers × {perWriter})");
    }

    private static RingOfLong NewRing(IFileSystem fs)
    {
        var settings = new BlittableRingSettings(
            new StorageEngineOptions("cw-ring", 64L << 20, enableSegmentation: true,
                preallocateFile: true, deleteOnClose: false))
        {
            PageSize = 8192,
            MemorySize = 32L << 20,
        };
        var ring = new RingOfLong(settings, fs);
        ring.Initialize();
        ring.WaitForReady();
        return ring;
    }
}
