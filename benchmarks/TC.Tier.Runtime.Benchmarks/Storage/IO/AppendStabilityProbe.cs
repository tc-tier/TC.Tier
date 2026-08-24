using System.Diagnostics;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// ★ Append 稳定落盘延迟探针——测生产最关心的"速度是否稳定、不忽高忽低"。
/// <para>现有 <see cref="AppendThroughputProbe"/> 只测吞吐均值，本探针补全延迟稳定性维度：</para>
/// <list type="bullet">
/// <item><b>延迟分布</b>：min / p50 / p90 / p99 / p999 / max（μs）</item>
/// <item><b>Jitter</b>（抖动比）= Max / P50 ——越接近 1 越稳定，越大越抖</item>
/// <item><b>CV</b>（变异系数）= StdDev / Mean ——&lt;0.1 极稳，&gt;1 严重抖动</item>
/// <item><b>GC during</b>：运行中 Gen0/1/2 GC 次数（GC 暂停是延迟尖峰主因）</item>
/// <item><b>吞吐衰减曲线</b>：每秒吞吐采样，看是否越跑越慢</item>
/// <item><b>稳定落盘</b>：WriteThrough 模式，每次写真实 fsync（不是 page cache 假象）</item>
/// </list>
///
/// <para>用法：--append-stability [totalMB] [segmentMB] [payloadKB] [disk] [mode] [durationSec]</para>
/// <para>  mode: writethrough (默认，真实落盘) | flush (周期 Flush group-commit) | cache (page cache)</para>
/// <para>示例：</para>
/// <para>  --append-stability 1024 64 64 C writethrough 60    # 1GB / 64MB 段 / 64K payload / C 盘 / 真实落盘 / 60 秒</para>
/// <para>  --append-stability 5120 256 64 D writethrough 300   # D 盘 HDD 5GB / 5 分钟（慢盘稳定性）</para>
/// </summary>
public static class AppendStabilityProbe
{
    public static int Run(string[] args)
    {
        long totalMB = args.Length > 0 && long.TryParse(args[0], out var t) ? t : 1024;
        int segmentMB = args.Length > 1 && int.TryParse(args[1], out var sg) ? sg : 64;
        int payloadKB = args.Length > 2 && int.TryParse(args[2], out var pk) ? pk : 64;
        // args[3] 历史盘符位已废弃（介质由 TC_BENCH_FS_SPEC 指定），保持后续参数位不变
        string modeStr = args.Length > 4 ? args[4].ToLowerInvariant() : "writethrough";
        int durationSec = args.Length > 5 && int.TryParse(args[5], out var d) ? d : 60;

        long totalBytes = totalMB * 1024L * 1024L;
        long segmentBytes = segmentMB * 1024L * 1024L;
        int payload = payloadKB * 1024;
        bool writeThrough = modeStr == "writethrough";
        bool periodicFlush = modeStr == "flush";

        // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        Console.WriteLine("=== Append 稳定落盘延迟探针 ===");
        Console.WriteLine($"介质: {BenchVolume.DefaultSpec} | 总量: {totalMB} MB | 段: {segmentMB} MB | payload: {payloadKB} KB");
        Console.WriteLine($"模式: {modeStr} | 持续: {durationSec}s（取 totalBytes 和 durationSec 先到者）");
        Console.WriteLine();

        using var vol = new BenchVolume();
        return RunOnce(vol, totalBytes, segmentBytes, payload, writeThrough, periodicFlush, durationSec);
    }

    private static int RunOnce(BenchVolume vol, long totalBytes, long segmentBytes, int payload,
                                bool writeThrough, bool periodicFlush, int durationSec)
    {
        var persistence = writeThrough ? FileOpenHints.WriteThrough : FileOpenHints.None;
        var options = new StorageEngineOptions("stab", segmentGrowthLimit: segmentBytes).WithPreallocateFile(true).WithHints(persistence);
        using var dev = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


        var payloadBuf = new byte[payload];
        for (int i = 0; i < payload; i++) payloadBuf[i] = (byte)(i & 0xFF);

        long written = 0;
        long ops = 0;
        long flushBytesThreshold = 16 * 1024L * 1024L;  // periodicFlush 模式每 16MB flush
        long lastFlushAtBytes = 0;

        // ★ 延迟采集：每次 Append 记 ticks（容量按预估 ops 上限，避免动态扩展开销）
        int maxSamples = (int)Math.Min(totalBytes / payload + 1024, 5_000_000);
        var latencies = new long[maxSamples];
        int sampleCount = 0;

        // ★ GC 基线（运行前后差值 = 运行中 GC 次数）
        long gc0Before = GC.CollectionCount(0);
        long gc1Before = GC.CollectionCount(1);
        long gc2Before = GC.CollectionCount(2);
        long totalAllocBefore = GC.GetTotalAllocatedBytes();

        // ★ 吞吐衰减采样：每秒一个 bucket
        var throughputSamples = new List<(double sec, double mbps)>();
        long lastSecWritten = 0;
        double lastSecTime = 0;

        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        long durationTicks = Stopwatch.Frequency * durationSec;

        // ★ CPU 利用率采集基线（监控线程每秒采样）
        // Process.TotalProcessorTime 是所有逻辑核的累积 CPU 时间（内核+用户），
        // 归一化到整机：cpuPct = deltaCpuSec / (deltaWallSec × ProcessorCount) × 100
        var proc = Process.GetCurrentProcess();
        int cpuCores = Environment.ProcessorCount;  // 本机 12 逻辑核
        TimeSpan lastCpuTime = proc.TotalProcessorTime;
        double lastCpuWallSec = 0;
        var cpuSamples = new List<(double sec, double cpuPct)>();

        Console.WriteLine($"[环境] CPU: {cpuCores} 逻辑核（单线程顺序 Append 预期 CPU≈{100.0/cpuCores:0.0}%，若远低于说明 IO bound）");

        // 监控线程：每秒采样吞吐 + CPU%
        var monitor = new Thread(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(1000);
                double now = sw.Elapsed.TotalSeconds;
                long curWritten = Interlocked.Read(ref written);
                long delta = curWritten - lastSecWritten;
                double mbps = (delta / (1024.0 * 1024.0)) / (now - lastSecTime);
                throughputSamples.Add((now, mbps));

                // CPU% 采样（与吞吐同步对齐）
                TimeSpan curCpu = proc.TotalProcessorTime;
                double cpuDeltaSec = (curCpu - lastCpuTime).TotalSeconds;
                double wallDelta = now - lastCpuWallSec;
                double cpuPct = wallDelta > 0 ? (cpuDeltaSec / (wallDelta * cpuCores)) * 100.0 : 0;
                cpuSamples.Add((now, cpuPct));
                Console.WriteLine($"t={now,6:0.0}s | {mbps,7:0.0} MB/s | CPU={cpuPct,5:0.0}% | written={curWritten / (1024 * 1024.0),6:0.0}MB");
                lastCpuTime = curCpu;
                lastCpuWallSec = now;
                lastSecWritten = curWritten;
                lastSecTime = now;
            }
        }) { IsBackground = true, Name = "stab-monitor" };
        monitor.Start();

        // ★ 主写循环：单线程顺序 Append（最纯粹的延迟稳定性测量）
        try
        {
            while (written < totalBytes && sw.ElapsedTicks < durationTicks)
            {
                if (cts.Token.IsCancellationRequested) break;
                long t0 = Stopwatch.GetTimestamp();
                dev.Append(payloadBuf);
                long elapsed = Stopwatch.GetTimestamp() - t0;

                if (sampleCount < latencies.Length)
                    latencies[sampleCount++] = elapsed;

                written += payload;
                ops++;

                if (periodicFlush)
                {
                    if (written - lastFlushAtBytes >= flushBytesThreshold)
                    {
                        long tf0 = Stopwatch.GetTimestamp();
                        dev.Flush();
                        long tfElapsed = Stopwatch.GetTimestamp() - tf0;
                        if (sampleCount < latencies.Length)
                            latencies[sampleCount++] = tfElapsed;  // Flush 延迟也算进分布
                        lastFlushAtBytes = written;
                    }
                }
            }
        }
        finally
        {
            sw.Stop();
            cts.Cancel();
            monitor.Join(1000);
        }

        // ★ GC 后置采样
        long gc0After = GC.CollectionCount(0);
        long gc1After = GC.CollectionCount(1);
        long gc2After = GC.CollectionCount(2);
        long totalAllocAfter = GC.GetTotalAllocatedBytes();

        double sec = sw.Elapsed.TotalSeconds;
        double avgMbps = (written / (1024.0 * 1024.0)) / sec;

        // ── 计算延迟分位 ──
        double ticksPerUs = Stopwatch.Frequency / 1_000_000.0;
        var sortedSamples = new long[sampleCount];
        Array.Copy(latencies, sortedSamples, sampleCount);
        Array.Sort(sortedSamples);

        double MinUs() => sortedSamples[0] / ticksPerUs;
        double PctUs(double pct)
        {
            double rank = pct / 100.0 * (sampleCount - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            if (lo == hi) return sortedSamples[lo] / ticksPerUs;
            return (sortedSamples[lo] + (sortedSamples[hi] - sortedSamples[lo]) * (rank - lo)) / ticksPerUs;
        }
        double MaxUs() => sortedSamples[sampleCount - 1] / ticksPerUs;

        // 均值 + StdDev + CV
        double sumTicks = 0;
        for (int i = 0; i < sampleCount; i++) sumTicks += latencies[i];
        double meanTicks = sumTicks / sampleCount;
        double sumSqDiff = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            double diff = latencies[i] - meanTicks;
            sumSqDiff += diff * diff;
        }
        double stdDevTicks = Math.Sqrt(sumSqDiff / sampleCount);
        double meanUs = meanTicks / ticksPerUs;
        double stdDevUs = stdDevTicks / ticksPerUs;
        double cv = stdDevUs / meanUs;  // 变异系数

        double p50 = PctUs(50);
        double p90 = PctUs(90);
        double p99 = PctUs(99);
        double p999 = PctUs(99.9);
        double minL = MinUs();
        double maxL = MaxUs();
        double jitter = maxL / p50;  // 抖动比

        // 吞吐稳定性（CV）
        double tputMean = throughputSamples.Count > 0 ? throughputSamples.Average(s => s.mbps) : 0;
        double tputStdDev = throughputSamples.Count > 0
            ? Math.Sqrt(throughputSamples.Average(s => Math.Pow(s.mbps - tputMean, 2)))
            : 0;
        double tputCV = tputMean > 0 ? tputStdDev / tputMean : 0;

        // ── 报告输出 ──
        Console.WriteLine();
        Console.WriteLine($"╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║          Append 稳定落盘延迟分析报告                          ║");
        Console.WriteLine($"╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"【总量】 {written / (1024 * 1024.0):0.0} MB / {sec:0.00}s = {avgMbps:0.0} MB/s avg ({ops} ops)");
        Console.WriteLine();
        Console.WriteLine($"【延迟分布】（μs，n={sampleCount}）");
        Console.WriteLine($"  min  = {minL,12:0.00}");
        Console.WriteLine($"  p50  = {p50,12:0.00}");
        Console.WriteLine($"  p90  = {p90,12:0.00}");
        Console.WriteLine($"  p99  = {p99,12:0.00}");
        Console.WriteLine($"  p999 = {p999,12:0.00}");
        Console.WriteLine($"  max  = {maxL,12:0.00}");
        Console.WriteLine($"  mean = {meanUs,12:0.00}    StdDev = {stdDevUs,12:0.00}");
        Console.WriteLine();
        Console.WriteLine($"【稳定性指标】");
        Console.WriteLine($"  Jitter (Max/P50)    = {jitter,6:0.00}×   {(jitter < 10 ? "✅ 稳定" : jitter < 100 ? "🟡 中等抖动" : "❌ 严重抖动")}");
        Console.WriteLine($"  CV (StdDev/Mean)    = {cv,6:0.00}     {(cv < 0.1 ? "✅ 极稳" : cv < 1.0 ? "🟡 中等" : "❌ 高变异")}");
        Console.WriteLine($"  p99/p50             = {p99 / p50,6:0.00}×   （尾延迟相对中位数的倍数）");
        Console.WriteLine();
        Console.WriteLine($"【GC during 运行】");
        Console.WriteLine($"  Gen0 = {gc0After - gc0Before,6}    Gen1 = {gc1After - gc1Before,6}    Gen2 = {gc2After - gc2Before,6}");
        Console.WriteLine($"  TotalAllocated = {(totalAllocAfter - totalAllocBefore) / (1024.0 * 1024):0.0} MB");
        Console.WriteLine();
        Console.WriteLine($"【吞吐稳定性】（{throughputSamples.Count} 个 1s 采样）");
        Console.WriteLine($"  mean = {tputMean,6:0.0} MB/s    StdDev = {tputStdDev,6:0.0} MB/s");
        Console.WriteLine($"  CV   = {tputCV,6:0.00}        {(tputCV < 0.1 ? "✅ 速度恒定" : tputCV < 0.3 ? "🟡 轻微波动" : "❌ 忽高忽低")}");
        Console.WriteLine($"  min  = {(throughputSamples.Count > 0 ? throughputSamples.Min(s => s.mbps) : 0),6:0.0} MB/s    max = {(throughputSamples.Count > 0 ? throughputSamples.Max(s => s.mbps) : 0),6:0.0} MB/s");
        Console.WriteLine();
        // ★ CPU 利用率分析（关键：判断 IO bound vs CPU bound）
        Console.WriteLine($"【CPU 利用率】（{cpuCores} 逻辑核，{cpuSamples.Count} 个 1s 采样）");
        if (cpuSamples.Count > 0)
        {
            double cpuMean = cpuSamples.Average(s => s.cpuPct);
            double cpuMax = cpuSamples.Max(s => s.cpuPct);
            double cpuMin = cpuSamples.Min(s => s.cpuPct);
            double cpuStdDev = Math.Sqrt(cpuSamples.Average(s => Math.Pow(s.cpuPct - cpuMean, 2)));
            double cpuCV = cpuMean > 0 ? cpuStdDev / cpuMean : 0;
            Console.WriteLine($"  mean = {cpuMean,5:0.0}%    max = {cpuMax,5:0.0}%    min = {cpuMin,5:0.0}%    StdDev = {cpuStdDev,5:0.0}%");
            Console.WriteLine($"  CV   = {cpuCV,5:0.00}");
            // 判定瓶颈类型：单线程顺序 Append 理论上 CPU≈1/核数（如 12 核 ≈ 8.3%）
            double singleThreadExpected = 100.0 / cpuCores;
            if (cpuMean < singleThreadExpected * 0.7)
                Console.WriteLine($"  → 🔴 IO bound（CPU 仅 {cpuMean:0.0}% << 理论 {singleThreadExpected:0.0}%）：瓶颈在 IO/落盘，不在 CPU——延迟尖峰来自 OS fsync/调度");
            else if (cpuMean < singleThreadExpected * 1.5)
                Console.WriteLine($"  → 🟢 接近单核理论值 {singleThreadExpected:0.0}%：CPU 利用充分，瓶颈在单线程串行处理");
            else
                Console.WriteLine($"  → 🟡 CPU 偏高（{cpuMean:0.0}% > 理论 {singleThreadExpected:0.0}%）：有 GC/锁竞争消耗 CPU");
        }
        Console.WriteLine();
        Console.WriteLine($"【最终判定】");
        bool stableLatency = jitter < 10 && cv < 1.0;
        bool stableThroughput = tputCV < 0.3;
        if (stableLatency && stableThroughput)
            Console.WriteLine($"  ✅✅ 落盘稳定：延迟 Jitter={jitter:0.00}× CV={cv:0.00}，吞吐 CV={tputCV:0.00}（速度恒定，无忽高忽低）");
        else if (stableLatency || stableThroughput)
            Console.WriteLine($"  🟡 部分稳定：延迟稳定={stableLatency}，吞吐稳定={stableThroughput}（详见上面指标）");
        else
            Console.WriteLine($"  ❌ 不稳定：延迟 Jitter={jitter:0.00}× CV={cv:0.00}，吞吐 CV={tputCV:0.00}（有忽高忽低现象）");

        return 0;
    }
}
