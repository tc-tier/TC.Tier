
namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// 补三个缺口: 1) PunchHole 性能 2) 不同IO大小 3) 混合负载
/// </summary>
public static class ExtentTableGapProbes
{
    public static int Run(string[] args)
    {
        Gap1_PunchHole();
        Gap2_IOSizes();
        Gap3_ReadWriteRatios();
        return 0;
    }

    // ═══════════════════ 缺口1: PunchHole 性能 ═══════════════════
    static void Gap1_PunchHole()
    {
        Console.WriteLine("=== GAP1: PunchHole ===");
        var vol = new BenchVolume();
        try
        {
            var dev = NewDevice(vol.Fs, 256L * 1024 * 1024);
            var data = new byte[4096]; new Random(42).NextBytes(data);

            // 预填: 1000 个 4KB Append
            var addrs = new LogicalAddress[1000];
            for (int i = 0; i < 1000; i++) addrs[i] = dev.Append(data);

            // 测量 PunchHole 延迟 — 逐个打洞 4KB
            var sw = Stopwatch.StartNew();
            var times = new long[1000];
            for (int i = 0; i < 1000; i++)
            {
                long t0 = sw.ElapsedTicks;
                dev.Reclaim(addrs[i], new LogicalAddress(addrs[i].SegId, addrs[i].Offset + 4096));
                times[i] = sw.ElapsedTicks - t0;
            }
            Array.Sort(times);
            double ns = 1e9 / Stopwatch.Frequency;
            Console.WriteLine($"  1000x PunchHole 4KB:");
            Console.WriteLine($"    mean={times.Average()*ns/1000:F0}us p50={times[500]*ns/1000:F0}us p99={times[990]*ns/1000:F0}us");

            // 测量碎片化后 ExtentTable k 增长 — 无法直接访问, 通过延迟变化间接测量
            Console.WriteLine($"    PunchHole done, measuring post-fragmentation Append...");

            // 再写 100 个 Append, 测 Insert 延迟变化
            var times2 = new long[100];
            for (int i = 0; i < 100; i++)
            {
                long t0 = sw.ElapsedTicks;
                dev.Append(data);
                times2[i] = sw.ElapsedTicks - t0;
            }
            Array.Sort(times2);
            Console.WriteLine($"    Append after 1000x PunchHole: p50={times2[50]*ns/1000:F0}us p99={times2[99]*ns/1000:F0}us");

            dev.Dispose();
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════ 缺口2: 不同IO大小 ═══════════════════
    static void Gap2_IOSizes()
    {
        Console.WriteLine("\n=== GAP2: IO Sizes ===");
        Console.WriteLine($"{"Size",6} {"ops/s",10} {"p50",8} {"p99",8} {"p999",8}");

        foreach (int size in new[] { 64, 256, 1024, 4096, 65536, 1048576 })
        {
            int ops = size >= 65536 ? 5000 : 50000;
            var vol = new BenchVolume();
            try
            {
                var dev = NewDevice(vol.Fs, 256L * 1024 * 1024);
                var data = new byte[size]; new Random(42).NextBytes(data);
                var readBuf = new byte[size];
                var sw = Stopwatch.StartNew();
                var times = new long[ops];

                for (int i = 0; i < ops; i++)
                {
                    long t0 = sw.ElapsedTicks;
                    var addr = dev.Append(data);
                    dev.Read(addr, readBuf);
                    times[i] = sw.ElapsedTicks - t0;
                }
                sw.Stop();
                Array.Sort(times);
                double ns = 1e9 / Stopwatch.Frequency;
                double thr = ops / (sw.ElapsedMilliseconds / 1000.0);
                double p50 = times[ops / 2] * ns / 1000.0;
                double p99 = times[(int)(ops * 0.99)] * ns / 1000.0;
                double p999 = times[(int)(ops * 0.999)] * ns / 1000.0;

                string sz = size >= 1048576 ? $"{size/1048576}MB" : size >= 1024 ? $"{size/1024}KB" : $"{size}B";
                Console.WriteLine($"{sz,6} {thr/1000,9:F1}K {p50,7:F0}us {p99,7:F0}us {p999,7:F0}us");

                dev.Dispose();
            }
            finally { vol.Dispose(); }
        }
    }

    // ═══════════════════ 缺口3: 读写比例 ═══════════════════
    static void Gap3_ReadWriteRatios()
    {
        Console.WriteLine("\n=== GAP3: Read/Write Ratios (4T, 256MB段) ===");
        Console.WriteLine($"{"Ratio",8} {"ops/s",10} {"p50",8} {"p99",8} {"p999",8}");

        foreach (var (wr, rd, label) in new[] { (1,9,"1:9"), (5,5,"5:5"), (9,1,"9:1") })
        {
            var vol = new BenchVolume();
            try
            {
                var dev = NewDevice(vol.Fs, 256L * 1024 * 1024);
                var data = new byte[1024]; new Random(42).NextBytes(data);
                var buf = new byte[1024];
                int opsPerThread = 50000;
                int totalOps = opsPerThread * 4;
                var times = new long[totalOps];
                var addrs = new LogicalAddress[totalOps];

                // prefill
                for (int i = 0; i < totalOps; i++) addrs[i] = dev.Append(data);

                var sw = Stopwatch.StartNew();
                var tasks = new Task[4];
                for (int t = 0; t < 4; t++)
                {
                    int tid = t;
                    tasks[t] = Task.Run(() =>
                    {
                        var rnd = new Random(42 + tid);
                        int baseIdx = tid * opsPerThread;
                        for (int i = 0; i < opsPerThread; i++)
                        {
                            long t0 = sw.ElapsedTicks;
                            if (rnd.Next(10) < wr)
                                addrs[baseIdx + i] = dev.Append(data);
                            else
                                dev.Read(addrs[baseIdx + i], buf);
                            times[baseIdx + i] = sw.ElapsedTicks - t0;
                        }
                    });
                }
                Task.WaitAll(tasks);
                sw.Stop();
                Array.Sort(times);
                double ns = 1e9 / Stopwatch.Frequency;
                double thr = totalOps / (sw.ElapsedMilliseconds / 1000.0);
                Console.WriteLine($"{label,8} {thr/1000,9:F1}K {times[totalOps/2]*ns/1000,7:F0}us {times[(int)(totalOps*0.99)]*ns/1000,7:F0}us {times[(int)(totalOps*0.999)]*ns/1000,7:F0}us");

                dev.Dispose();
            }
            finally { vol.Dispose(); }
        }
    }

    static StorageEngine NewDevice(IFileSystem fs, long segSize)
    {
        var options = new StorageEngineOptions("gap", segmentGrowthLimit: segSize).WithPreallocateFile(false).WithDeleteOnClose(true);
        var dev = (StorageEngine)options.Builder(fs, logger: new NullLogger()).Start();

        return dev;
    }

}
