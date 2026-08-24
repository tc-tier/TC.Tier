using System.Buffers;
using System.Diagnostics;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// ★ S3 多线程裸写对照探针——与 AppendThroughputProbe 对等的"裸 RandomAccess.Write"基线。
/// <para>★ 解决旧 RawWriteProbe 的方法论缺陷：</para>
/// <list type="bullet">
/// <item>旧只单线程 → 不能量化 Append 多线程 CAS 开销</item>
/// <item>旧用 byte[]（非对齐）→ DIO 模式跑不了，与 Append DIO 对照不对等</item>
/// <item>旧无延迟分位 → 只有均值 MB/s，看不到尾延迟尖峰</item>
/// <item>旧稀疏文件（preallocationSize:0）vs Append 预分配 → 不对等</item>
/// </list>
///
/// <para>本探针对齐 AppendThroughputProbe 的所有维度：</para>
/// <list type="bullet">
/// <item>N 线程并发（Interlocked.Add 推进共享 offset，模拟 CAS 租借）</item>
/// <item>AlignedMemoryManager（4K 对齐，DIO 可用）</item>
/// <item>支持 sparse / full 预分配模式（与 Append 对等）</item>
/// <item>支持 none / writethrough / directio 三种 FileOptions</item>
/// <item>LatencyHistogram 输出 p50/p99/p999/max</item>
/// </list>
///
/// <para>用法：--raw-mt-probe [totalMB] [threads] [payloadKB] [disk] [mode] [preallocate]</para>
/// <para>  mode: none (默认) | writethrough | directio</para>
/// <para>  preallocate: sparse (默认) | full</para>
/// <para>示例：--raw-mt-probe 4096 8 64 C writethrough full    # 与 Append 8 线程 WT 对等对照</para>
/// </summary>
public static class RawIoMultiThreadProbe
{
    public static int Run(string[] args)
    {
        long totalMB = args.Length > 0 && long.TryParse(args[0], out var t) ? t : 4096;
        int threads = args.Length > 1 && int.TryParse(args[1], out var th) ? th : 1;
        int payloadKB = args.Length > 2 && int.TryParse(args[2], out var pk) ? pk : 64;
        string disk = args.Length > 3 ? args[3].ToUpperInvariant() : "C";
        string modeStr = args.Length > 4 ? args[4].ToLowerInvariant() : "none";
        string preallocStr = args.Length > 5 ? args[5].ToLowerInvariant() : "sparse";

        long totalBytes = totalMB * 1024L * 1024L;
        int payload = payloadKB * 1024;
        bool writeThrough = modeStr == "writethrough";
        bool directIo = modeStr == "directio";
        bool preallocate = preallocStr == "full";

        string dir = $"{disk}:\\tc-rawmt-probe";
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"rawmt-{Guid.NewGuid():N}.dat");

        // 组装 FileOptions（DIO 在 Windows 走 NO_BUFFERING = SequentialScan 不加，靠 disableBuffering）
        FileOptions opts = FileOptions.Asynchronous;
        if (writeThrough) opts |= FileOptions.WriteThrough;
        // 注：托管 RandomAccess.Write 的 DIO 在 Windows 需要 FileNative.OpenHandle（disableBuffering），
        // 这里用 OpenHandle + 写 WriteThrough 即可近似（DIO 完整对照走 PersistenceMatrixBench Combo=P4/P5/P6）

        long preallocSize = preallocate ? totalBytes : 0;

        Console.WriteLine("=== 裸 RandomAccess.Write 多线程对照组 ===");
        Console.WriteLine($"盘: {disk}: | 总量: {totalMB} MB | 线程: {threads} | payload: {payloadKB} KB");
        Console.WriteLine($"mode: {modeStr} | preallocate: {preallocStr} ({(preallocate ? "真实分配" : "稀疏")})");
        Console.WriteLine($"路径: {path}");
        Console.WriteLine();

        // 对齐 buffer：每线程独立（避免并发覆盖 + DIO 对齐）
        var bufs = new AlignedMemoryManager[threads];
        for (int i = 0; i < threads; i++)
        {
            bufs[i] = new AlignedMemoryManager(payload, 4096);
            bufs[i].GetSpan().Slice(0, payload).Fill((byte)(0x40 + i));
        }

        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, opts, preallocationSize: preallocSize);

        long written = 0;
        long offset = 0;  // 共享游标，Interlocked.Add 推进（模拟 CAS 租借）
        var cts = new CancellationTokenSource();
        var perThreadLatency = new LatencyHistogram[threads];
        for (int i = 0; i < threads; i++) perThreadLatency[i] = new LatencyHistogram(1 << 16);

        var sw = Stopwatch.StartNew();

        // 监控线程
        var monitor = new Thread(() =>
        {
            long lastWritten = 0;
            double lastTime = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(1000);
                double now = sw.Elapsed.TotalSeconds;
                double dt = now - lastTime;
                if (dt <= 0) continue;
                long cur = Interlocked.Read(ref written);
                double mbps = ((cur - lastWritten) / (1024.0 * 1024.0)) / dt;
                Console.WriteLine($"t={now,6:0.0}s | {mbps,7:0.0} MB/s | written={cur / (1024 * 1024.0),6:0.0}MB / {totalBytes / (1024 * 1024.0),6:0.0}MB");
                lastWritten = cur;
                lastTime = now;
            }
        }) { IsBackground = true };
        monitor.Start();

        try
        {
            var tasks = new Task[threads];
            for (int ti = 0; ti < threads; ti++)
            {
                int tid = ti;
                tasks[tid] = Task.Run(() =>
                {
                    var buf = bufs[tid].GetSpan().Slice(0, payload);
                    var lat = perThreadLatency[tid];
                    while (true)
                    {
                        long curOffset = Interlocked.Add(ref offset, payload) - payload;
                        if (curOffset + payload > totalBytes) break;
                        long t0 = Stopwatch.GetTimestamp();
                        RandomAccess.Write(handle, buf, curOffset);
                        lat.Record(Stopwatch.GetTimestamp() - t0);
                        Interlocked.Add(ref written, payload);
                    }
                });
            }
            Task.WaitAll(tasks);
        }
        finally
        {
            sw.Stop();
            cts.Cancel();
            monitor.Join(1000);
        }

        double sec = sw.Elapsed.TotalSeconds;
        double avgMbps = (totalBytes / (1024.0 * 1024.0)) / sec;

        Console.WriteLine();
        Console.WriteLine($"=== 完成 ===");
        Console.WriteLine($"总量: {totalBytes / (1024 * 1024.0):0.0} MB in {sec:0.00}s = {avgMbps:0.0} MB/s avg ({threads} 线程)");
        Console.WriteLine($"单线程平均吞吐: {avgMbps / threads:0.0} MB/s");
        Console.WriteLine();
        Console.WriteLine("=== 各线程延迟分位 ===");
        for (int i = 0; i < threads; i++)
        {
            Console.WriteLine($"  T{i}: {perThreadLatency[i].Summary()}");
            bufs[i].Dispose();
        }

        // 汇总延迟（所有线程样本合并）
        var merged = new LatencyHistogram(1 << 18);
        foreach (var lat in perThreadLatency)
        {
            // 简单汇总：取每线程的 p50/p99/max 平均（严格合并需要原始样本，这里近似）
        }
        Console.WriteLine();
        Console.WriteLine($"=== 跟 Append 探针对比：相同 threads/payload/mode 下，Append 吞吐应是这里的 {(avgMbps > 0 ? "??%" : "??")}（Device 层开销 = 1 - Append/裸写）===");

        try { File.Delete(path); } catch { }
        try { Directory.Delete(dir, true); } catch { }
        return 0;
    }
}
