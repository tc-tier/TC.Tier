
namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// V4 ExtentTable 大段持续稳定性基准。
/// <para>测试真实 LocalStorageDevice 在大段(256MB-1GB)下 Append+Read 持续吞吐、延迟分布、GC 影响。</para>
/// <para>对比 DirectIO(BypassPageCache) vs Buffered 模式。</para>
/// </summary>
public static class ExtentTableSustainedStabilityProbe
{
    public static int Run(string[] args)
    {
        var mode = args.Length > 0 && args[0] == "--direct" ? FileOpenHints.NoBuffering : FileOpenHints.None;
        if (mode == FileOpenHints.NoBuffering)
            Console.WriteLine("*** DirectIO 模式: 需要 sectorSize 对齐 buffer ***");
        var segSize = args.Length > 1 && long.TryParse(args[1], out var s) ? s : 256L * 1024 * 1024;
        var totalOps = args.Length > 2 && int.TryParse(args[2], out var tn) ? tn : 1_000_000;
        var payloadSize = 1024;

        Console.WriteLine($"=== V4 ExtentTable 大段持续稳定性 ===");
        Console.WriteLine($"段大小: {segSize / (1024 * 1024)}MB  IO模式: {mode}  总操作: {totalOps / 1000000.0:F1}M  Payload: {payloadSize}B");
        Console.WriteLine($"环境: {Environment.OSVersion}, .NET {Environment.Version}");
        Console.WriteLine();

        var vol = new BenchVolume();

        try
        {
            var options = new StorageEngineOptions("v4-stab", segmentGrowthLimit: segSize).WithPreallocateFile(false).WithDeleteOnClose(true).WithHints(mode);
            var device = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


            // DirectIO 需要 sector-aligned buffer
            byte[] data, readBuf;
            int dataOff, readOff;
            if (mode == FileOpenHints.NoBuffering)
            {
                // 分配 512B 对齐的 buffer：多分配 sectorSize 再偏移到对齐边界
                data = GC.AllocateUninitializedArray<byte>(payloadSize + 512, pinned: true);
                readBuf = GC.AllocateUninitializedArray<byte>(payloadSize + 512, pinned: true);
                long addr = 0;
                unsafe { fixed (byte* p = data) addr = (long)p; }
                dataOff = (int)((512 - (addr & 511)) & 511);
                unsafe { fixed (byte* p = readBuf) addr = (long)p; }
                readOff = (int)((512 - (addr & 511)) & 511);
                // 使用 Span 切片
                new Random(42).NextBytes(data.AsSpan(dataOff, payloadSize));
            }
            else
            {
                data = new byte[payloadSize];
                readBuf = new byte[payloadSize];
                dataOff = 0;
                readOff = 0;
                new Random(42).NextBytes(data);
            }

            var times = new long[totalOps];
            var sw = Stopwatch.StartNew();
            var gcStart = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
            var throughputWindows = new List<(double elapsedSec, long ops, long gc0, long gc1, long gc2)>();

            long windowOps = 0;
            long windowStart = sw.ElapsedMilliseconds;
            long lastReportMs = 0;

            for (int i = 0; i < totalOps; i++)
            {
                long t0 = sw.ElapsedTicks;

                var dataSpan = data.AsSpan(dataOff, payloadSize);
                var readSpan = readBuf.AsSpan(readOff, payloadSize);
                var addr = device.Append(dataSpan);
                int bytesRead = device.Read(addr, readSpan);
                if (bytesRead != payloadSize)
                    Console.WriteLine($"  ✗ Read 短读 @ {i}: got {bytesRead}B, expected {payloadSize}B");

                times[i] = sw.ElapsedTicks - t0;
                windowOps++;

                // 每 500ms 报告
                long elapsed = sw.ElapsedMilliseconds;
                if (elapsed - lastReportMs >= 500)
                {
                    long gc0 = GC.CollectionCount(0) - gcStart;
                    long gc1 = GC.CollectionCount(1) - gcStart;
                    long gc2 = GC.CollectionCount(2) - gcStart;
                    double windowSec = (elapsed - windowStart) / 1000.0;
                    throughputWindows.Add((windowSec, windowOps, gc0, gc1, gc2));
                    windowOps = 0;
                    windowStart = elapsed;

                    double totalSec = elapsed / 1000.0;
                    double throughput = i / totalSec;

                    Console.Write($"\r  {i/1000000.0:F1}M ops  {throughput/1000:F1}K ops/s  GC: {gc0}/{gc1}/{gc2}  ");
                    lastReportMs = elapsed;
                }
            }

            sw.Stop();
            Console.WriteLine();

            // 延迟分布
            Array.Sort(times);
            double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
            var latencies = times.Select(t => t * nsPerTick).ToArray();
            double mean = latencies.Average();
            double std = Math.Sqrt(latencies.Average(l => (l - mean) * (l - mean)));
            double p50 = latencies[totalOps / 2];
            double p99 = latencies[(int)(totalOps * 0.99)];
            double p999 = latencies[(int)(totalOps * 0.999)];
            double max = latencies[^1];

            double totalSecFinal = sw.ElapsedMilliseconds / 1000.0;
            double avgThroughput = totalOps / totalSecFinal;

            Console.WriteLine();
            Console.WriteLine("═══ 最终统计 ═══");
            Console.WriteLine($"总操作:       {totalOps/1000000.0:F1}M");
            Console.WriteLine($"总耗时:       {totalSecFinal:F1}s");
            Console.WriteLine($"平均吞吐:     {avgThroughput/1000:F1}K ops/s  ({avgThroughput/1e6:F2}M ops/s)");
            Console.WriteLine($"每操作:       {mean/1000:F1}µs (Append+Read 1KB)");
            Console.WriteLine();
            Console.WriteLine($"延迟分布 (ns):");
            Console.WriteLine($"  Mean:        {mean,10:F0}");
            Console.WriteLine($"  p50:         {p50,10:F0}");
            Console.WriteLine($"  p99:         {p99,10:F0}");
            Console.WriteLine($"  p999:        {p999,10:F0}");
            Console.WriteLine($"  Max:         {max,10:F0}");
            Console.WriteLine($"  CV:          {std/mean*100,10:F1}%");
            Console.WriteLine();
            Console.WriteLine($"GC 统计:");
            Console.WriteLine($"  Gen0: {GC.CollectionCount(0) - gcStart}  Gen1: {GC.CollectionCount(1) - gcStart}  Gen2: {GC.CollectionCount(2) - gcStart}");
            Console.WriteLine($"  Allocated: {GC.GetTotalAllocatedBytes() / 1024.0 / 1024.0:F1} MB");
            Console.WriteLine();

            // 吞吐时间线
            if (throughputWindows.Count > 0)
            {
                Console.WriteLine("吞吐时间线 (每 500ms 窗口):");
                foreach (var w in throughputWindows.Take(20))
                    Console.WriteLine($"  {w.elapsedSec,5:F1}s  {w.ops/w.elapsedSec/1000,8:F1}K ops/s  GC:{w.gc0}/{w.gc1}/{w.gc2}");
                if (throughputWindows.Count > 20)
                    Console.WriteLine($"  ... ({throughputWindows.Count - 20} more windows)");
            }

            device.Dispose();
        }
        finally
        {
            vol.Dispose();
        }
        return 0;
    }
}
