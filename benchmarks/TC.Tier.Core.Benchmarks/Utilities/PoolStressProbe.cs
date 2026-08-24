using System.Buffers;
using System.Diagnostics;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// 可观测性探针（非 BenchmarkDotNet）——覆盖规范里 BDN 测不了的项目：
///   二.1 稳态内存占用 / 二.2 内存泄漏验证 / 二.3 GC 频率
///   三.4 并发正确性压力 / 四.4 Dispose 并发安全
///
/// 用法：dotnet run -c Release -- --probe
/// （Program.cs 检测到 --probe 首参时调用本类的 Run）
/// </summary>
internal static class PoolStressProbe
{
    private const int Size = 4096;
    private const int Align = 4096;

    public static int Run()
    {
        Console.WriteLine("================ PoolStressProbe ================");
        LeakedManagedAllocationCheck();          // 二.1 + 二.2（托管分配角度）
        NativeMemoryLeakCheck();                  // 二.2（非托管内存角度）
        GcFrequencyUnderLoad();                   // 二.3
        ConcurrentCorrectnessStress();            // 三.4
        DisposeConcurrencySafety();               // 四.4
        ThreadLocalNativeLeakCheck();             // P0 复现：thread-local 非托管内存泄漏
        Console.WriteLine("================ Probe OK ================");
        return 0;
    }

    // ── P0 复现：thread-local 栈里的 aligned buffer 在 Dispose 后是否泄漏 ──
    // 工作线程租借 aligned buffer 归还到 thread-local 栈 → 池 Dispose → 检查 buffer 是否被释放。
    // 直接验证：让工作线程捕获它放进 thread-local 栈的 buffer 引用，Dispose 后检查 IsDisposed。
    private static void ThreadLocalNativeLeakCheck()
    {
        Console.WriteLine("\n[P0 复现] thread-local 非托管内存泄漏检查");
        var pool = new PinnedBufferPool(maxPerBucket: 64);
        var done = new ManualResetEventSlim(false);
        AlignedMemoryManager? lastReturned = null; // 工作线程最后一次归还的 buffer（会留在 thread-local 栈顶）

        var worker = new Thread(() =>
        {
            // 租借并归还：归还后 buffer 进 thread-local 栈（本线程私有）
            var b = pool.RentAligned(Size, Align);
            pool.ReturnAligned(b);
            lastReturned = b; // 捕获引用，它现在在 thread-local 栈里
            done.Wait(); // 保持线程存活，thread-local 栈不被线程退出回收
        }) { IsBackground = true };
        worker.Start();
        Thread.Sleep(100);

        Console.WriteLine($"  Dispose 前：buffer 在 thread-local 栈，IsDisposed = {lastReturned!.IsDisposed}（应为 False）");

        pool.Dispose();
        // 让 Dispose 的清理逻辑有机会跑（ClearBucket 只清 Global，不清 thread-local）
        Thread.Sleep(50);

        bool stillAlive = !lastReturned.IsDisposed;
        Console.WriteLine($"  Dispose 后：buffer.IsDisposed = {lastReturned.IsDisposed}");
        Console.WriteLine($"  结论：{(stillAlive
            ? "❌ 确认泄漏 — thread-local 栈的 native 内存（AlignedAlloc）未在 Dispose 释放"
            : "✅ 无泄漏 — Dispose 已释放 thread-local 栈的 native 内存")}");

        done.Set();
        worker.Join();
    }

    // ── 三.1 诊断：power-of-2 优化后，8 线程每线程真实吞吐 + 命中率 ──
    private static void ScalabilityPerThreadThroughput()
    {
        Console.WriteLine("\n[三.1 诊断] power-of-2 优化后并发吞吐分解");
        int[] threadCounts = { 1, 8 };
        foreach (int n in threadCounts)
        {
            using var pool = new PinnedBufferPool(maxPerBucket: 256);
            var perThreadOps = new long[n];
            var perThreadMs = new double[n];
            var ready = new CountdownEvent(n);
            var go = new ManualResetEventSlim(false);
            var threads = new Thread[n];
            for (int t = 0; t < n; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    // 预热本线程 thread-local 栈
                    var warm = pool.Rent(4096);
                    pool.Return(warm);
                    for (int i = 0; i < 16; i++) { var b = pool.Rent(4096); pool.Return(b); }
                    ready.Signal();
                    go.Wait();

                    var sw = Stopwatch.StartNew();
                    long ops = 0;
                    for (int i = 0; i < 1_000_000; i++)
                    {
                        var b = pool.Rent(4096);
                        pool.Return(b);
                        ops++;
                    }
                    sw.Stop();
                    perThreadOps[tid] = ops;
                    perThreadMs[tid] = sw.Elapsed.TotalMilliseconds;
                }) { IsBackground = true };
                threads[t].Start();
            }
            ready.Wait();
            go.Set();
            foreach (var th in threads) th.Join();

            double totalOps = 0, maxMs = 0;
            for (int t = 0; t < n; t++) { totalOps += perThreadOps[t]; if (perThreadMs[t] > maxMs) maxMs = perThreadMs[t]; }
            double avgPerThreadOps = (totalOps / n) / (maxMs / 1000.0);
            Console.WriteLine($"  {n} 线程：总吞吐 {totalOps / (maxMs / 1000.0) / 1e6:F1}M ops/s，" +
                              $"每线程平均 {avgPerThreadOps / 1e6:F1}M ops/s，hits={pool.CacheHits} misses={pool.CacheMisses}");
        }
    }

    // ── 二.1 + 二.2：百万级 Rent/Return 后，托管分配增量应 ≈ 0（命中路径不进 GC 堆）──
    private static void LeakedManagedAllocationCheck()
    {
        Console.WriteLine("\n[二.1/二.2] 托管分配泄漏检查 — 百万级 Rent/Return");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);

        using var pool = new PinnedBufferPool(maxPerBucket: 64);
        // 预热，后续全命中
        pool.Return(pool.Rent(Size));

        const int N = 1_000_000;
        for (int i = 0; i < N; i++)
        {
            var b = pool.Rent(Size);
            pool.Return(b);
        }

        long after = GC.GetTotalAllocatedBytes(precise: true);
        double deltaPerOp = (after - before) / (double)N;
        Console.WriteLine($"  {N:N0} 次 Rent/Return，托管分配增量 = {after - before:N0} B（{deltaPerOp:F2} B/op）");
        Console.WriteLine($"  结论：{(deltaPerOp < 1.0 ? "✅ 近似零分配（命中路径不进 GC 堆）" : "⚠️ 存在托管分配泄漏")}");
    }

    // ── 二.2：非托管内存泄漏 — Rent 不 Return（模拟归还失败），Dispose 后应回落 ──
    private static void NativeMemoryLeakCheck()
    {
        Console.WriteLine("\n[二.2] 非托管内存泄漏检查 — 池 Dispose 后私有内存回落");
        long baseline = Process.GetCurrentProcess().PrivateMemorySize64;

        var pool = new PinnedBufferPool(maxPerBucket: 1024);
        // 分配大量 buffer 并归还（进池），测 Dispose 是否释放 native
        var held = new AlignedMemoryManager[512];
        for (int i = 0; i < held.Length; i++)
            held[i] = pool.RentAligned(Size, Align);
        for (int i = 0; i < held.Length; i++)
            pool.ReturnAligned(held[i]);

        long peak = Process.GetCurrentProcess().PrivateMemorySize64;
        pool.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        long after = Process.GetCurrentProcess().PrivateMemorySize64;
        Console.WriteLine($"  baseline={baseline / 1024 / 1024}MB  peak={peak / 1024 / 1024}MB  after-dispose={after / 1024 / 1024}MB");
        long leaked = after - baseline;
        Console.WriteLine($"  净增长 = {leaked / 1024 / 1024}MB（私有字节，含 GC 堆噪声，小幅波动正常）");
        Console.WriteLine($"  结论：{(leaked < 50 * 1024 * 1024 ? "✅ Dispose 后回落，无明显 native 泄漏" : "⚠️ 需人工复核（私有字节含 GC 噪声）")}");
    }

    // ── 二.3：高并发负载下 GC 回收次数（Gen0/1/2）──
    private static void GcFrequencyUnderLoad()
    {
        Console.WriteLine("\n[二.3] GC 频率 — 高并发负载下 Gen0/1/2 回收次数");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int g0Before = GC.CollectionCount(0), g1Before = GC.CollectionCount(1), g2Before = GC.CollectionCount(2);

        using var pool = new PinnedBufferPool(maxPerBucket: 256);
        // 预热
        for (int i = 0; i < 16; i++)
            pool.Return(pool.Rent(Size));

        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 200_000; i++)
            {
                var b = pool.Rent(Size);
                pool.Return(b);
            }
        });

        int g0 = GC.CollectionCount(0) - g0Before;
        int g1 = GC.CollectionCount(1) - g1Before;
        int g2 = GC.CollectionCount(2) - g2Before;
        Console.WriteLine($"  8 线程 × 200K 次 Rent/Return（共 1.6M op）：Gen0={g0}, Gen1={g1}, Gen2={g2}");
        Console.WriteLine($"  结论：{(g0 == 0 && g1 == 0 && g2 == 0
            ? "✅ 零 GC 回收（池化命中完全消除 GC 压力）"
            : "⚠️ 有 GC 回收（检查是否有冷启动 miss 或 thread-local 初次分配）")}");
    }

    // ── 三.4：并发正确性 — 多线程随机 Rent/Return，校验无异常、无重复租借 ──
    private static void ConcurrentCorrectnessStress()
    {
        Console.WriteLine("\n[三.4] 并发正确性压力 — 多线程随机 Rent/Return");
        using var pool = new PinnedBufferPool(maxPerBucket: 64);
        var rentedSet = new HashSet<byte[]>();
        var lockObj = new object();
        int violations = 0;
        var sw = Stopwatch.StartNew();

        Parallel.For(0, 16, _ =>
        {
            var rng = new Random(Environment.CurrentManagedThreadId);
            for (int i = 0; i < 100_000; i++)
            {
                var b = pool.Rent(Size);
                b[0] = 42; // 写入，若被他人同时持有会数据竞争（这里只检测引用唯一性）
                lock (lockObj)
                {
                    if (!rentedSet.Add(b))
                        Interlocked.Increment(ref violations); // 同一引用被同时持有 = 重复租借
                }
                lock (lockObj)
                    rentedSet.Remove(b);
                pool.Return(b);
                if (i % 1000 == 0) Thread.SpinWait(rng.Next(50)); // 随机扰动
            }
        });

        sw.Stop();
        Console.WriteLine($"  16 线程 × 100K 次，耗时 {sw.ElapsedMilliseconds}ms，重复租借违规 = {violations}");
        Console.WriteLine($"  结论：{(violations == 0 ? "✅ 无重复租借、无异常" : "❌ 检测到同一 buffer 被并发持有")}");
    }

    // ── 四.4：Dispose 并发安全 — 租借归还进行中调用 Dispose，校验无崩溃 ──
    private static void DisposeConcurrencySafety()
    {
        Console.WriteLine("\n[四.4] Dispose 并发安全 — 租借归还中 Dispose");
        var pool = new PinnedBufferPool(maxPerBucket: 64);
        var cts = new CancellationTokenSource();
        Exception? workerError = null;

        var worker = Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var b = pool.Rent(Size);
                    pool.Return(b);
                }
            }
            catch (Exception ex) { workerError = ex; } // ObjectDisposedException 是合法的竞态结果
        });

        Thread.Sleep(200); // 让 worker 跑起来
        try
        {
            pool.Dispose(); // 在并发租借归还中 Dispose
            Console.WriteLine("  Dispose 调用完成，无崩溃");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Dispose 抛异常：{ex.GetType().Name}");
            return;
        }

        cts.Cancel();
        worker.Wait();
        // worker 捕获的 ObjectDisposedException 是合法竞态（Dispose 后 Rent），不算 bug
        bool workerClean = workerError is null || workerError is ObjectDisposedException;
        Console.WriteLine($"  worker 结束异常：{workerError?.GetType().Name ?? "无"}（ObjectDisposedException 合法）");
        Console.WriteLine($"  结论：{(workerClean ? "✅ Dispose 并发安全，无未处理崩溃异常" : "❌ worker 出现非法异常")}");
    }
}
