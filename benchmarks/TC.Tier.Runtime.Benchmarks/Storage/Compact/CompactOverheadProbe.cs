using System.Diagnostics;

namespace TC.Tier.Runtime.Benchmarks.Storage.Compact;

/// <summary>
/// ★ 生产级缺口补测——Compact 写放大 + 随机混合负载 + ClampReadable 真实碎片衰减。
/// </summary>
public static class CompactOverheadProbe
{
    public static int Run(string[] args)
    {
        bool all = args.Length == 0;
        bool compact = all || args.Contains("--compact");
        bool mixed = all || args.Contains("--mixed");
        bool clamp = all || args.Contains("--clamp");

        if (compact) RunCompactOverhead().GetAwaiter().GetResult();
        if (mixed) RunRandomMixedWorkload();
        if (clamp) RunClampReadableFragmentation();
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    //  1. Compact 写放大 + 分阶段耗时 + 不同空洞率曲线
    // ═══════════════════════════════════════════════════════════════

    private static async Task RunCompactOverhead()
    {
        Console.WriteLine("=== Compact Overhead (write amplification + phased timing) ===");
        const long segSize = 256L * 1024 * 1024;
        const int blockSize = 256 * 1024;
        var data = new byte[blockSize];
        new Random(42).NextBytes(data);

        double[] holeRatios = { 0.0, 0.1, 0.3, 0.5, 0.7, 0.9 };
        Console.WriteLine($"{"Hole%",-8} {"Alive(MB)",-12} {"Migrated(MB)",-14} {"WA",-8} {"Scan(ms)",-10} {"Copy(ms)",-10} {"Total(ms)",-12}");
        Console.WriteLine(new string('-', 74));

        foreach (double holeRatio in holeRatios)
        {
            var vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:）
            try
            {
                var options = new StorageEngineOptions("test", segmentGrowthLimit: segSize).WithPreallocateFile(true);
                using var dev = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


                int blocks = (int)(segSize / blockSize);
                int holeBlocks = (int)(blocks * holeRatio);
                var addresses = new LogicalAddress[blocks];

                var sw = Stopwatch.StartNew();

                for (int i = 0; i < blocks; i++)
                    addresses[i] = dev.Append(data);

                long aliveBytes = blocks * (long)blockSize;

                if (holeBlocks > 0)
                {
                    var rng = new Random(42);
                    var indices = Enumerable.Range(0, blocks - 1).OrderBy(_ => rng.Next()).Take(holeBlocks).ToList();
                    var t0 = Stopwatch.GetTimestamp();
                    foreach (int idx in indices)
                    {
                        var from = addresses[idx];
                        var to = addresses[idx + 1];
                        if (from.SegId == to.SegId)
                            dev.Reclaim(from, to);
                    }

                    aliveBytes = (blocks - holeBlocks) * (long)blockSize;
                }

                var scanStart = Stopwatch.GetTimestamp();
                var copyStart = Stopwatch.GetTimestamp();
                var totalSw = Stopwatch.StartNew();

                var lastAddr = addresses[^1];
                var toAddr = lastAddr.SegId == addresses[0].SegId
                    ? new LogicalAddress(lastAddr.SegId, lastAddr.Offset + blockSize)
                    : new LogicalAddress(lastAddr.SegId, 0);
                using var compactCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var result = await dev.StartRangeCompact(addresses[0], toAddr, addresses)
                    .WaitAsync(compactCts.Token);

                totalSw.Stop();
                long migrated = result.MigrationMap.Count(kv => kv.Value.HasValue) * (long)blockSize;
                double wa = aliveBytes > 0 ? (double)migrated / aliveBytes : 0;

                Console.WriteLine(
                    $"{holeRatio * 100,5:F0}%  {aliveBytes / 1024.0 / 1024,8:F0}   {migrated / 1024.0 / 1024,10:F0}     {wa,6:F2}x  {totalSw.ElapsedMilliseconds,6}ms");
            }
            finally
            {
                vol.Dispose();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  2. 随机 Write/Read/PunchHole 三操作交织
    // ═══════════════════════════════════════════════════════════════

    private static void RunRandomMixedWorkload()
    {
        Console.WriteLine("=== Random Mixed Workload (Write/Read/PunchHole interleaved) ===");
        const int segSize = 64 * 1024 * 1024;
        const int payload = 4096;
        const int prefillCount = 2000;
        const int ops = 5000;

        var vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:）
        try
        {
            var options = new StorageEngineOptions("test", segmentGrowthLimit: segSize).WithPreallocateFile(true);
            using var dev = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


            var data = new byte[payload];
            new Random(42).NextBytes(data);
            var addresses = new List<LogicalAddress>();
            for (int i = 0; i < prefillCount; i++)
                addresses.Add(dev.Append(data));

            double[] writeRatios = { 0.1, 0.5, 0.9 };
            Console.WriteLine($"{"Write%",-8} {"Read%",-8} {"Punch%",-8} {"p50(us)",-12} {"p99(us)",-12} {"p999(us)",-12} {"Throughput",-12}");
            Console.WriteLine(new string('-', 72));

            foreach (double writeRatio in writeRatios)
            {
                double readRatio = 1.0 - writeRatio - 0.05;
                double reclaimRatio = 0.05;

                var rng = new Random(42);
                var latencies = new List<double>(ops);
                var sw = Stopwatch.StartNew();

                for (int i = 0; i < ops; i++)
                {
                    double roll = rng.NextDouble();
                    var t0 = Stopwatch.GetTimestamp();
                    if (roll < writeRatio)
                    {
                        int idx = rng.Next(addresses.Count);
                        dev.Write(addresses[idx], data);
                        addresses[idx] = new LogicalAddress(addresses[idx].SegId, addresses[idx].Offset);
                    }
                    else if (roll < writeRatio + readRatio)
                    {
                        var buf = new byte[payload];
                        dev.Read(addresses[rng.Next(addresses.Count)], buf);
                    }
                    else
                    {
                        int idx = rng.Next(addresses.Count - 1);
                        var from = addresses[idx];
                        var to = addresses[idx + 1];
                        if (from.SegId == to.SegId)
                        {
                            dev.Reclaim(from, to);
                            addresses.RemoveAt(idx + 1);
                        }
                    }

                    latencies.Add((Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency * 1_000_000);
                }

                sw.Stop();
                var sorted = latencies.OrderBy(x => x).ToArray();
                Console.WriteLine(
                    $"{writeRatio * 100,5:F0}%  {readRatio * 100,5:F0}%  {reclaimRatio * 100,4:F0}%   {sorted[(int)(sorted.Length * 0.5)],8:F1}  {sorted[(int)(sorted.Length * 0.99)],8:F1}  {sorted[(int)(sorted.Length * 0.999)],8:F1}  {ops / sw.Elapsed.TotalSeconds,8:F0} ops/s");
            }
        }
        finally
        {
            vol.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  3. ClampReadable 真实碎片衰减（打洞后读路径退化）
    // ═══════════════════════════════════════════════════════════════

    private static void RunClampReadableFragmentation()
    {
        Console.WriteLine("=== ClampReadable Under Real Fragmentation (post PunchHole) ===");
        const int segSize = 64 * 1024 * 1024;
        const int payload = 256 * 1024;

        var vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:）
        try
        {
            var options = new StorageEngineOptions("test", segmentGrowthLimit: segSize).WithPreallocateFile(true);
            using var dev = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


            var data = new byte[payload];
            new Random(42).NextBytes(data);
            int blocks = segSize / payload;

            var addresses = new LogicalAddress[blocks];
            for (int i = 0; i < blocks; i++)
                addresses[i] = dev.Append(data);

            int[] punchLevels = { 0, 10, 30, 60, 120 };
            Console.WriteLine($"{"Punches",-10} {"Allocated",-12} {"Temp Read p50(ns)",-18} {"Temp Read p99(ns)",-18}");
            Console.WriteLine(new string('-', 58));

            foreach (int punchCount in punchLevels)
            {
                var rng = new Random(42);
                for (int p = 0; p < punchCount; p++)
                {
                    int idx = rng.Next(addresses.Length - 2);
                    var from = addresses[idx];
                    var to = addresses[idx + 2];
                    if (from.SegId == to.SegId && from.Offset < to.Offset)
                        dev.Reclaim(from, to);
                }

                var latencies = new List<double>(1000);
                for (int t = 0; t < 1000; t++)
                {
                    int idx = rng.Next(addresses.Length);
                    var buf = new byte[payload];
                    var start = Stopwatch.GetTimestamp();
                    dev.Read(addresses[idx], buf);
                    latencies.Add((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency * 1_000_000_000);
                }

                var sorted = latencies.OrderBy(x => x).ToArray();
                int aliveSegs = (int)(blocks - punchCount * 1.5);
                if (aliveSegs < 1) aliveSegs = 1;
                Console.WriteLine(
                    $"{punchCount,-10} ~{aliveSegs * payload / 1024 / 1024,-8}MB  {sorted[(int)(sorted.Length * 0.5)],12:F1}         {sorted[(int)(sorted.Length * 0.99)],12:F1}");
            }
        }
        finally
        {
            vol.Dispose();
        }
    }
}
