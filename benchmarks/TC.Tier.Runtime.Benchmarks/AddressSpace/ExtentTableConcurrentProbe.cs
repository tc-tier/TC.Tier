
namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

public static class ExtentTableConcurrentProbe
{
    public static int Run(string[] args)
    {
        var segSize = 256L * 1024 * 1024;
        var opsPerThread = 100_000;
        var payloadSize = 1024;

        Console.WriteLine($"=== V4 Concurrent ===");
        Console.WriteLine($"Segment: {segSize/1024/1024}MB  ops/thread: {opsPerThread/1000}K  payload: {payloadSize}B");
        Console.WriteLine($"{"Mode",-18} {"T",3} {"Time",8} {"ops/s",10} {"p50",8} {"p99",8} {"p999",8}");
        Console.WriteLine(new string('-', 75));

        foreach (int threads in new[] { 1, 4, 8, 12 })
        {
            // pure-write
            RunOnce(threads, opsPerThread, segSize, payloadSize, "pure-write",
                (dev, data, buf, i, t) => dev.Append(data));

            // pure-read (pre-fill then read)
            RunOnceRead(threads, opsPerThread, segSize, payloadSize);

            // mixed 1w3r (each thread: 1 write + 3 reads)
            RunOnce(threads, opsPerThread, segSize, payloadSize, "mixed-1w3r",
                (dev, data, buf, i, t) =>
                {
                    var addr = dev.Append(data);
                    dev.Read(addr, buf);
                    dev.Read(addr, buf);
                    dev.Read(addr, buf);
                });
        }
        return 0;
    }

    private static void RunOnce(int threads, int opsPerThread, long segSize, int payloadSize,
        string label, Action<StorageEngine, byte[], byte[], int, int> action)
    {
        var vol = new BenchVolume();
        try
        {
            var options = new StorageEngineOptions("v4c", segmentGrowthLimit: segSize).WithPreallocateFile(false).WithDeleteOnClose(true);
            var device = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


            var data = new byte[payloadSize];
            var readBuf = new byte[payloadSize];
            new Random(42).NextBytes(data);

            var totalOps = opsPerThread * threads;
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
                        action(device, data, readBuf, i, tid);
                        long elapsed = sw.ElapsedTicks - t0;
                        int idx = tid * opsPerThread + i;
                        latencies[idx] = elapsed;
                    }
                });
            }
            Task.WaitAll(tasks);
            sw.Stop();

            PrintStats(label, threads, totalOps, sw, latencies);

            device.Dispose();
        }
        finally { vol.Dispose(); }
    }

    private static void RunOnceRead(int threads, int opsPerThread, long segSize, int payloadSize)
    {
        var vol = new BenchVolume();
        try
        {
            var options = new StorageEngineOptions("v4cr", segmentGrowthLimit: segSize).WithPreallocateFile(false).WithDeleteOnClose(true);
            var device = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


            var data = new byte[payloadSize];
            new Random(42).NextBytes(data);
            var addrs = new LogicalAddress[opsPerThread * threads];
            for (int i = 0; i < addrs.Length; i++)
                addrs[i] = device.Append(data);

            var readBuf = new byte[payloadSize];
            var latencies = new long[opsPerThread * threads];
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
                        device.Read(addrs[tid * opsPerThread + i], readBuf);
                        latencies[tid * opsPerThread + i] = sw.ElapsedTicks - t0;
                    }
                });
            }
            Task.WaitAll(tasks);
            sw.Stop();

            PrintStats("pure-read", threads, opsPerThread * threads, sw, latencies);

            device.Dispose();
        }
        finally { vol.Dispose(); }
    }

    private static void PrintStats(string label, int threads, int totalOps, Stopwatch sw, long[] latencies)
    {
        Array.Sort(latencies);
        double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        double elapsed = sw.ElapsedMilliseconds / 1000.0;
        double throughput = totalOps / elapsed;
        double p50 = latencies[totalOps / 2] * nsPerTick / 1000.0;
        double p99 = latencies[(int)(totalOps * 0.99)] * nsPerTick / 1000.0;
        double p999 = latencies[(int)(totalOps * 0.999)] * nsPerTick / 1000.0;
        Console.WriteLine($"{label,-18} {threads,2}T {elapsed,7:F1}s {throughput/1000,9:F1}K {p50,7:F0}us {p99,7:F0}us {p999,7:F0}us");
    }
}
