using System.Diagnostics;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Benchmarks.Primitives;

/// <summary>
/// 同步模式并发伸缩探针（独立计时程序，--sync-probe）——BDN Parallel 组高度不确定，并发数字用本探针。
/// <para>测三组关键形态（选型指南数据源）：</para>
/// <para>① 读者伸缩：N 线程纯读锤击 SpinRWLock 共享 vs LightEpoch 保护周期 vs Monitor vs RWLS——
///   单字共享锁的缓存行乒乓 vs epoch 每线程独占行的伸缩差。</para>
/// <para>② 写偏向落地：N-1 读者锤击 + 1 写者排他循环——写者 AcquireExclusive 平均/最大落地延迟
///   （写偏向保证 = 只等在途读者，不挨读者流饿）。</para>
/// <para>用法：dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --sync-probe [秒数]</para>
/// </summary>
public static class SyncModesProbe
{
    public static int Run(string[] args)
    {
        var seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 1.5;
        Console.WriteLine($"同步模式并发探针 ｜ 每组 {seconds}s ｜ i5-12400（6C/12T）参考值\n");

        Console.WriteLine("── ① 读者伸缩：N 线程纯读锤击（ops/s，越高越好；×1T=相对单线程伸缩）──");
        ReaderScaling("SpinRWLock 共享", seconds, n =>
        {
            var l = new SpinRWLock();
            RunThreads(n, () => { l.AcquireShared(); l.ReleaseShared(); }, (int)(seconds * 1000), out var ops);
            return ops;
        });
        ReaderScaling("LightEpoch 周期", seconds, n =>
        {
            var e = new LightEpoch();
            RunThreads(n, () => { e.Resume(); e.Suspend(); }, (int)(seconds * 1000), out var ops);
            return ops;
        });
        ReaderScaling("Monitor", seconds, n =>
        {
            var o = new object();
            RunThreads(n, () => { lock (o) { } }, (int)(seconds * 1000), out var ops);
            return ops;
        });
        ReaderScaling("RWLS 读", seconds, n =>
        {
            var r = new ReaderWriterLockSlim();
            RunThreads(n, () => { r.EnterReadLock(); r.ExitReadLock(); }, (int)(seconds * 1000), out var ops);
            return ops;
        });

        Console.WriteLine("\n── ② 写偏向落地：N-1 读者锤击 + 1 写者（写者每循环 AcquireExclusive+Release）──");
        foreach (var readers in new[] { 1, 3, 5, 11 })
            WriterUnderReaderHammer(readers, seconds);

        return 0;
    }

    private static void ReaderScaling(string name, double seconds, Func<int, long> runOnce)
    {
        var line = $"  {name,-18}";
        long baseOps = 0;
        foreach (var n in new[] { 1, 2, 4, 6 })
        {
            // 每组跑 3 轮取中位（首轮含 JIT/预热）
            var results = new long[3];
            for (var i = 0; i < 3; i++) results[i] = runOnce(n) / (long)Math.Max(seconds, 0.001);
            Array.Sort(results);
            var ops = results[1];
            if (n == 1) baseOps = ops;
            line += $"｜{n}T {ops / 1_000_000.0,6:F1}M";
            if (n > 1) line += $"(×{(double)ops / Math.Max(baseOps, 1),4:F1})";
        }
        Console.WriteLine(line);
    }

    private static void WriterUnderReaderHammer(int readers, double seconds)
    {
        var l = new SpinRWLock();
        var stop = 0L;
        var readerThreads = new Thread[readers];
        for (var i = 0; i < readers; i++)
        {
            readerThreads[i] = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0) { l.AcquireShared(); l.ReleaseShared(); }
            }) { IsBackground = true };
            readerThreads[i].Start();
        }

        // 写者循环：每次排他落地延迟记入环形桶（ns 级 Stopwatch.ElapsedTicks）
        var count = 0L;
        double sumMs = 0, maxMs = 0;
        var swTotal = Stopwatch.StartNew();
        while (swTotal.Elapsed.TotalSeconds < seconds)
        {
            var sw = Stopwatch.StartNew();
            l.AcquireExclusive();
            l.ReleaseExclusive();
            var ms = sw.Elapsed.TotalMilliseconds;
            sumMs += ms;
            if (ms > maxMs) maxMs = ms;
            count++;
        }
        Volatile.Write(ref stop, 1);
        foreach (var t in readerThreads) t.Join(500);

        Console.WriteLine($"  {readers,2} 读者 + 1 写者 ｜ 写者落地 ×{count} 次 ｜ 平均 {sumMs / Math.Max(count, 1) * 1000:F1} µs ｜ 最大 {maxMs * 1000:F1} µs");
    }

    private static void RunThreads(int n, Action body, int durationMs, out long totalOps)
    {
        var stop = 0L;
        var ops = new long[n];
        var threads = new Thread[n];
        var gate = new ManualResetEventSlim(false);
        for (var i = 0; i < n; i++)
        {
            var idx = i;
            threads[i] = new Thread(() =>
            {
                gate.Wait();
                var local = 0L;
                while (Volatile.Read(ref stop) == 0) { body(); local++; }
                Volatile.Write(ref ops[idx], local);
            }) { IsBackground = true };
            threads[i].Start();
        }
        gate.Set();
        Thread.Sleep(durationMs);
        Volatile.Write(ref stop, 1);
        foreach (var t in threads) t.Join(2000);
        totalOps = 0;
        foreach (var o in ops) totalOps += o;
    }
}
