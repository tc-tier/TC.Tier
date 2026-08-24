using System.Diagnostics;
using System.Reflection;
// AppendDiag 在 Kernel.Device 命名空间，已通过上面 using 引入

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// 把 ★DIAG★ 日志打到控制台——收集设备内部诊断。
/// </summary>
internal sealed class SimpleConsoleLogger : ILogger
{
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log(LogLevel logLevel, string message, Exception? exception = null)
    {
        if (message.StartsWith("★DIAG★", StringComparison.Ordinal) || logLevel >= LogLevel.Warning)
            Console.WriteLine($"  [{logLevel}] {message}");
    }
}

/// <summary>
/// 通过反射读取 LocalStorageDevice / DeviceBase 的 internal 状态（避开 InternalsVisibleTo 公钥问题）。
/// 一次性绑定到 FieldInfo/MethodInfo，循环里 Invoke——开销在监控线程，不影响写线程。
/// </summary>
internal sealed class DeviceReflection
{
    private readonly object _device;
    private readonly FieldInfo _addressMapField;
    private readonly object _addressMap;
    private readonly MethodInfo _getSegment;
    private readonly FieldInfo _maxOffsetField;
    private readonly MethodInfo _getCount;
    private readonly MethodInfo _getMinSegId;

    public DeviceReflection(StorageEngine device)
    {
        _device = device;
        // 沿继承链向上找 _addressMap（在 DeviceBase 里）
        Type? t = typeof(StorageEngine);
        FieldInfo? amField = null;
        while (t != null && amField == null)
        {
            amField = t.GetField("_addressMap", BindingFlags.Instance | BindingFlags.NonPublic);
            t = t.BaseType;
        }
        if (amField == null)
            throw new InvalidOperationException("_addressMap field not found.");
        _addressMapField = amField;
        _addressMap = _addressMapField.GetValue(device)!;
        var amType = _addressMap.GetType();
        const BindingFlags ALL = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        _getSegment = amType.GetMethod("GetSegment", ALL)!;
        _getCount = amType.GetProperty("Count", ALL)!.GetGetMethod(nonPublic: true)!;
        _getMinSegId = amType.GetProperty("MinSegId", ALL)!.GetGetMethod(nonPublic: true)!;
        // Segment 类的 MaxOffset 字段
        _maxOffsetField = typeof(Segment).GetField("MaxOffset", ALL)!;
    }

    public long GetMaxOffset(int segId)
    {
        var seg = _getSegment.Invoke(_addressMap, new object[] { segId })!;
        return (long)_maxOffsetField.GetValue(seg)!;
    }
    public int Count => (int)_getCount.Invoke(_addressMap, null)!;
    public int MinSegId => (int)_getMinSegId.Invoke(_addressMap, null)!;
}

/// <summary>
/// Append 持续写入吞吐复现探针——取证「前几秒快→速度归零」那一刻的水位线状态。
/// <para>★ 关键：必须测真正的落盘行为，不能只测 Page Cache 写内存。</para>
/// <para>用法：AppendThroughputProbe [totalMB] [segmentMB] [threads] [payloadKB] [disk] [mode] [flushMB]</para>
/// <para>  disk:  D / C / T （选哪个盘跑——必须在慢盘/HDD 上才能复现 IO 问题）</para>
/// <para>  mode:  cache (默认,Page Cache) | writethrough (每写落盘) | flush (定期 Flush 模拟 group-commit)</para>
/// <para>  flushMB: mode=flush 时每写多少 MB 调一次 Flush()（默认 16）</para>
/// <para>示例：</para>
/// <para>  AppendThroughputProbe 5120 256 1 64 D writethrough   # HDD 5GB/256MB 段，每写落盘</para>
/// <para>  AppendThroughputProbe 5120 256 1 64 D flush 16         # HDD 5GB，每 16MB group-commit Flush</para>
/// <para>  AppendThroughputProbe 256 1 8 64 D flush 4             # HDD 8线程小段高频跨段 + 频繁 Flush</para>
/// </summary>
public static class AppendThroughputProbe
{
    public static int Run(string[] args)
    {
        long totalMB = args.Length > 0 && long.TryParse(args[0], out var t) ? t : 256;
        long segmentMB = args.Length > 1 && long.TryParse(args[1], out var s) ? s : 256;
        int threads = args.Length > 2 && int.TryParse(args[2], out var th) ? th : 1;
        int payloadKB = args.Length > 3 && int.TryParse(args[3], out var pk) ? pk : 64;
        // args[4] 历史盘符位已废弃（介质由 TC_BENCH_FS_SPEC 指定），保持后续参数位不变
        string mode = args.Length > 5 ? args[5].ToLowerInvariant() : "writethrough";
        long flushMB = args.Length > 6 && long.TryParse(args[6], out var fm) ? fm : 16;

        long totalBytes = totalMB * 1024L * 1024L;
        long segmentBytes = segmentMB * 1024L * 1024L;
        int payload = payloadKB * 1024;

        // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        bool writeThrough = mode == "writethrough";
        bool periodicFlush = mode == "flush";

        Console.WriteLine("=== Append 持续写入吞吐探针 ===");
        Console.WriteLine($"介质: {BenchVolume.DefaultSpec} | 总量: {totalMB} MB | 段: {segmentMB} MB | 线程: {threads} | payload: {payloadKB} KB");
        Console.WriteLine($"模式: {mode} {(periodicFlush ? $"(每 {flushMB} MB Flush)" : "")} | 预分配: True (测真实落盘)");
        Console.WriteLine($"跨段次数预估: {totalMB / segmentMB}");
        Console.WriteLine();

        using var vol = new BenchVolume();
        return RunOnce(vol, totalBytes, segmentBytes, threads, payload, writeThrough, periodicFlush, flushMB);
    }

    private static int RunOnce(BenchVolume vol, long totalBytes, long segmentBytes, int threads, int payload,
                                bool writeThrough, bool periodicFlush, long flushMB)
    {
        // ★ preallocateFile=true（默认）+ PersistenceMode 选落盘模式——测真实 IO 行为
        var logger = new SimpleConsoleLogger();
        var persistence = writeThrough ? FileOpenHints.WriteThrough : FileOpenHints.None;
        var options = new StorageEngineOptions("probe", segmentGrowthLimit: segmentBytes).WithPreallocateFile(true).WithHints(persistence);
        using var dev = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();

        var refl = new DeviceReflection(dev);

        long written = 0;
        long appendOps = 0;
        long lastFlushAtBytes = 0;
        long flushBytesThreshold = flushMB * 1024L * 1024L;
        var stopWatch = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        var payloadBuf = new byte[payload];
        for (int i = 0; i < payload; i++) payloadBuf[i] = (byte)(i & 0xFF);

        // ── 监控线程：每 500ms 打印水位线快照（慢盘用更长间隔）──
        var monitor = new Thread(() =>
        {
            long lastWritten = 0;
            double lastTime = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(500);
                double now = stopWatch.Elapsed.TotalSeconds;
                double dt = now - lastTime;
                if (dt <= 0) continue;

                long curWritten = Interlocked.Read(ref written);
                long deltaBytes = curWritten - lastWritten;
                double mbPerSec = (deltaBytes / (1024.0 * 1024.0)) / dt;

                var tail = dev.AllocatedTail;
                long activeMaxOffset = 0;
                int activeSegId = tail.SegId;
                try { activeMaxOffset = refl.GetMaxOffset(activeSegId); }
                catch { }

                long tailAbsBytes = (long)tail.SegId * segmentBytes + tail.Offset;
                long maxOffAbsBytes = (long)activeSegId * segmentBytes + activeMaxOffset;
                long gap = tailAbsBytes - maxOffAbsBytes;

                int segCount = refl.Count;
                int minSeg = refl.MinSegId;

                Console.WriteLine(
                    $"t={now,6:0.0}s | {mbPerSec,7:0.0} MB/s | " +
                    $"ops={Interlocked.Read(ref appendOps),8:N0} | " +
                    $"tail=seg{tail.SegId}@{tail.Offset / (1024 * 1024.0),6:0.0}MB | " +
                    $"MaxOff=seg{activeSegId}@{activeMaxOffset / (1024 * 1024.0),6:0.0}MB | " +
                    $"gap={gap / (1024 * 1024.0),7:0.0}MB | " +
                    $"segs[{minSeg}..{minSeg + segCount - 1}]({segCount})");

                lastWritten = curWritten;
                lastTime = now;
            }
        }) { IsBackground = true, Name = "probe-monitor" };
        monitor.Start();

        // 用 ConcurrentQueue 收集活跃段 MaxOffset 快照（监控线程读）
        // (written / appendOps / stopWatch / cts / payloadBuf 已在前面声明)

        // ── 写入线程（含定期 Flush 模拟 group-commit 落盘）──
        // ★ 每线程独立 LatencyHistogram——结束时输出 p50/p99/p999，与 RawIoMultiThreadProbe 对等
        var perThreadLatency = new LatencyHistogram[threads];
        for (int i = 0; i < threads; i++) perThreadLatency[i] = new LatencyHistogram(1 << 17);

        var writeTasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            writeTasks[t] = Task.Run(() =>
            {
                long perThread = totalBytes / threads;
                long localWritten = 0;
                var lat = perThreadLatency[tid];
                while (localWritten < perThread)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    long t0 = Stopwatch.GetTimestamp();
                    dev.Append(payloadBuf);
                    lat.Record(Stopwatch.GetTimestamp() - t0);
                    localWritten += payload;
                    long total = Interlocked.Add(ref written, payload);
                    Interlocked.Increment(ref appendOps);

                    // 定期 Flush 模拟 group-commit（仅 periodicFlush 模式）
                    if (periodicFlush)
                    {
                        long last = Interlocked.Read(ref lastFlushAtBytes);
                        if (total - last >= flushBytesThreshold &&
                            Interlocked.CompareExchange(ref lastFlushAtBytes, total, last) == last)
                        {
                            long tf = Stopwatch.GetTimestamp();
                            dev.Flush();  // ★ 强制落盘——这才会暴露真正的 IO 瓶颈
                            lat.Record(Stopwatch.GetTimestamp() - tf);  // Flush 延迟也算进分布
                        }
                    }
                }
            });
        }

        // 等写入完成，或超时 300s（卡死时不杀进程，留时间抓 dump）
        Console.WriteLine($"★ Benchmark PID={Environment.ProcessId}（卡死时用 dotnet-dump collect -p {Environment.ProcessId} 抓 dump）");
        var completed = Task.WaitAll(writeTasks, TimeSpan.FromSeconds(300));
        stopWatch.Stop();
        cts.Cancel();
        monitor.Join(500);

        Console.WriteLine();
        if (!completed)
        {
            Console.WriteLine($"!!! 超时 60s 未完成。已完成 {Interlocked.Read(ref written) / (1024 * 1024.0):0.0} MB / {totalBytes / (1024 * 1024.0):0.0} MB");
            Console.WriteLine($"!!! 最终 Tail={dev.AllocatedTail}, 段数={refl.Count}");
            return 2;
        }

        double totalSec = stopWatch.Elapsed.TotalSeconds;
        double avgMb = (totalBytes / (1024.0 * 1024.0)) / totalSec;
        Console.WriteLine($"完成: {totalBytes / (1024 * 1024.0):0.0} MB in {totalSec:0.00}s = {avgMb:0.0} MB/s avg");
        Console.WriteLine($"最终 Tail={dev.AllocatedTail}, 段数={refl.Count}");
        // ★ 延迟分位输出（与 RawIoMultiThreadProbe 对等，便于量化 Device 层尾延迟开销）
        Console.WriteLine("=== 各线程延迟分位 ===");
        for (int i = 0; i < threads; i++)
            Console.WriteLine($"  T{i}: {perThreadLatency[i].Summary()}");
        return 0;
    }
}
