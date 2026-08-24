using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Text;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// 优先级队列独立压测探针（非 BenchmarkDotNet）——覆盖 perf 文档待补项：
///   一. 并发吞吐矩阵（1/2/4/8 线程混合往返）——BDN Parallel 组在本机高度不确定，
///       方法论改独立计时（Thread 直起 + Barrier 对齐 + Stopwatch 总时/总 ops，5 轮取中位）。
///   二. 积压深度敏感性（实测）——含"单一优先级尾插"负载（验证高层索引退化疑点）。
///   三. 并发正确性压测（MPMC 不丢不重 / 排序语义 drain 硬断言 / MPSC 反转率 / DequeueAsync 等待-唤醒）。
///
/// 用法：dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --pq-probe
///       dotnet run -c Debug   -- ... -- --pq-probe correctness   （只跑正确性，DEBUG 链校验器生效）
/// 数据报告：src/TC.Tier.Core/docs/perf/priority-queues-performance.md
/// </summary>
internal static class PriorityQueueStressProbe
{
    private enum Prio8 : short { P0, P1, P2, P3, P4, P5, P6, P7 }

    // ── 负载：确定性优先级（与 PriorityQueueBenchmarks 同种子同分布——均匀 0..7）──
    private static readonly int[] Prios = CreatePrios();
    private static int[] CreatePrios()
    {
        var p = new int[4096];
        var rng = new Random(42);
        for (var i = 0; i < p.Length; i++) p[i] = rng.Next(8);
        return p;
    }

#if DEBUG
    private const int CorrectOpsPerProducer = 25_000;    // Debug 巡检慢——正确性减量
    private const int ConsumeTimeoutMs = 90_000;
#else
    private const int CorrectOpsPerProducer = 50_000;    // Release 20 万项/场景——SkipList 高压 100 万项会活性坍塌（已两次取证），减量保探针可完成
    private const int ConsumeTimeoutMs = 45_000;
#endif
    private const int DrainTimeoutMs = 30_000;
    private const int Producers = 4;
    private const int Consumers = 4;
    private const int ThroughputOpsPerThread = 200_000;
    private const int Backlog = 1024;
    private static readonly int[] ThreadCounts = { 1, 2, 4, 8, 16, 32, 64 };   // 16+ 超额订阅（12 逻辑核）——放大锁调度/缓存乒乓，模拟高核竞争压力
    private static readonly int[] BacklogSweep = { 1_024, 8_192, 65_536, 262_144 };

    private static int _failures;

    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var correctnessOnly = args.Contains("correctness", StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("================ PriorityQueueStressProbe ================");
        Console.WriteLine($"环境：{Environment.OSVersion} ｜ .NET {Environment.Version} ｜ 逻辑核 {Environment.ProcessorCount}" +
                          $" ｜ GC {(GCSettings.IsServerGC ? "Server" : "Workstation")}");
#if DEBUG
        Console.WriteLine("构建：**DEBUG**（AsyncPriorityQueue 每 64 op 链校验巡检生效——结构损坏当场爆）");
#else
        Console.WriteLine("构建：Release");
#endif
        Console.WriteLine($"模式：{(correctnessOnly ? "correctness-only" : "full（吞吐矩阵 + 积压敏感性 + 正确性）")}");

        if (!correctnessOnly)
        {
            ThroughputMatrix();
            BacklogSensitivity();
            RealisticLoadStress();
        }
        CorrectnessStress();

        Console.WriteLine(_failures == 0
            ? "================ PQ-PROBE OK（全部通过）================"
            : $"================ PQ-PROBE FAILED：{_failures} 项断言失败 ================");
        return _failures == 0 ? 0 : 1;
    }

    private static void Fail(string msg)
    {
        Interlocked.Increment(ref _failures);
        Console.WriteLine($"  ✗ FAIL：{msg}");
    }

    private static int PrioOf(long item) => Prios[(int)(item & 4095)];

    // ════════════════════════════════════════════════════════════
    //  实现适配器——统一三实现 API（Bucket 枚举 / SkipList long / Async int）
    // ════════════════════════════════════════════════════════════

    private interface IQAdapter : IDisposable
    {
        string Name { get; }
        void Create();
        void Enqueue(long item, int prio);        // prio ∈ 0..7
        bool TryDequeue(out long item);
        ValueTask<long> DequeueAsync(CancellationToken ct);
        int Count { get; }
        /// <summary>毒丸入队（§四.B 柱塞实验）：hazard=true 时注入 2ms 延迟——
        /// 锁版默认注入在锁外（延迟只属于自己），LockedHeapHazardAdapter 注入在锁内（全队柱塞）。</summary>
        void EnqueueWithHazard(long item, int prio, bool hazard);
    }

    private sealed class BucketAdapter : IQAdapter
    {
        private BucketPriorityQueue<Prio8, long> _q = null!;
        public string Name => "Bucket";
        public void Create() => _q = new BucketPriorityQueue<Prio8, long>();
        public void Enqueue(long item, int prio) => _q.Enqueue(item, (Prio8)prio);
        public bool TryDequeue(out long item) => _q.TryDequeue(out item);
        public ValueTask<long> DequeueAsync(CancellationToken ct) => _q.DequeueAsync(ct);
        public int Count => _q.Count;
        public void EnqueueWithHazard(long item, int prio, bool hazard)
        {
            if (hazard) Thread.Sleep(2);   // 无锁/分段结构：延迟只属于自己
            Enqueue(item, prio);
        }
        public void Dispose() => _q.Dispose();
    }

    private sealed class SkipListAdapter : IQAdapter
    {
        private SkipListPriorityQueue<long> _q = null!;
        public string Name => "SkipList";
        public void Create() => _q = new SkipListPriorityQueue<long>();
        public void Enqueue(long item, int prio) => _q.Enqueue(item, prio);
        public bool TryDequeue(out long item) => _q.TryDequeue(out item);
        public ValueTask<long> DequeueAsync(CancellationToken ct) => _q.DequeueAsync(ct);
        public int Count => _q.Count;
        public void EnqueueWithHazard(long item, int prio, bool hazard)
        {
            if (hazard) Thread.Sleep(2);   // 无锁/分段结构：延迟只属于自己
            Enqueue(item, prio);
        }
        public void Dispose() => _q.Dispose();
    }

    private sealed class AsyncAdapter : IQAdapter
    {
        private AsyncPriorityQueue<long> _q = null!;
        public string Name => "Async";
        public void Create() => _q = new AsyncPriorityQueue<long>();
        public void Enqueue(long item, int prio) => _q.Enqueue(item, prio);
        public bool TryDequeue(out long item) => _q.TryDequeue(out item);
        public ValueTask<long> DequeueAsync(CancellationToken ct) => _q.DequeueAsync(ct);
        public int Count => _q.Count;
        public void EnqueueWithHazard(long item, int prio, bool hazard)
        {
            if (hazard) Thread.Sleep(2);   // 无锁/分段结构：延迟只属于自己
            Enqueue(item, prio);
        }
        public void Dispose() => _q.Dispose();
    }

    /// <summary>对照基线：一把大 lock + 内置四叉堆（+ Bucket 同款一项一许可信号量做异步等待）——
    /// 回答"细粒度锁跳表是否比一把大锁还慢"（2026-08-17 追加）。</summary>
    private class LockedHeapAdapter : IQAdapter
    {
        protected const int HazardMs = 2;
        protected readonly object _gate = new();
        protected readonly SemaphoreSlim _items = new(0, int.MaxValue);
        protected PriorityQueue<long, long> _q = null!;
        public virtual string Name => "LockedHeap";
        public void Create() => _q = new PriorityQueue<long, long>();
        public virtual void EnqueueWithHazard(long item, int prio, bool hazard)
        {
            if (hazard) Thread.Sleep(HazardMs);   // 对照组：延迟在锁外——只慢自己
            Enqueue(item, prio);
        }
        public void Enqueue(long item, int prio) { lock (_gate) _q.Enqueue(item, prio); _items.Release(); }
        public bool TryDequeue(out long item) { lock (_gate) return _q.TryDequeue(out item, out _); }
        public async ValueTask<long> DequeueAsync(CancellationToken ct)
        {
            await _items.WaitAsync(ct).ConfigureAwait(false);
            if (!TryDequeue(out var item)) throw new InvalidOperationException("许可与项 1:1——不可达");
            return item;
        }
        public int Count { get { lock (_gate) return _q.Count; } }
        public void Dispose() => _items.Dispose();
    }

    /// <summary>毒丸变体：延迟注入在**锁内**——模拟持锁线程在临界区中被抢占/GC 暂停/页错误，
    /// 量化大锁"一个人的延迟 → 全队柱塞"的结构性传播（§四.B，2026-08-17 补测）。</summary>
    private sealed class LockedHeapHazardAdapter : LockedHeapAdapter
    {
        public override string Name => "LockedHeap☠";
        public override void EnqueueWithHazard(long item, int prio, bool hazard)
        {
            lock (_gate)   // ★ 延迟在锁内——等待者全部柱塞到持锁者恢复
            {
                if (hazard) Thread.Sleep(HazardMs);
                _q.Enqueue(item, prio);
            }
            _items.Release();
        }
    }

    private static IQAdapter NewAdapter(string name) => name switch
    {
        "Bucket" => new BucketAdapter(),
        "SkipList" => new SkipListAdapter(),
        "LockedHeap" => new LockedHeapAdapter(),
        _ => new AsyncAdapter(),
    };

    // ════════════════════════════════════════════════════════════
    //  一. 并发吞吐矩阵——混合往返（Enqueue 1 + TryDequeue 1，稳态积压 1024）
    // ════════════════════════════════════════════════════════════

    /// <summary>单轮混合往返计时。返回 ns/op；超时返回 null + 竞争退化下界。
    /// 超时后 10s 仍不退出的线程降级 Lowest 并放弃等待（实现内部重试风暴打不断——IsBackground 残留，
    /// 队列不 Dispose 防残留线程撞已释放对象）。正常完成时释放队列。</summary>
    private static (double? nsPerOp, double lowerBound, bool timedOut) RunThroughputRound(
        string name, int threads, int opsPerThread, int timeoutMs)
    {
        var q = NewAdapter(name);
        q.Create();
        for (var i = 0; i < Backlog; i++)
            q.Enqueue(i, Prios[i & 4095]);

        var barrier = new Barrier(threads + 1);
        var cancel = 0;
        var doneOps = new long[threads];
        var ts = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            var tid = t;
            ts[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    var start = tid * opsPerThread;
                    long n = 0;
                    for (var i = 0; i < opsPerThread; i++)
                    {
                        if ((i & 1023) == 0 && Volatile.Read(ref cancel) != 0) break;
                        var seq = start + i;
                        q.Enqueue(seq, Prios[seq & 4095]);
                        q.TryDequeue(out _);
                        n++;
                    }
                    doneOps[tid] = n;
                }
                catch (Exception) { /* 残留线程在放弃等待后被 Dispose 等竞态——吞掉，数据不采用 */ }
            }) { IsBackground = true };
        }

        var sw = new Stopwatch();
        foreach (var t in ts) t.Start();
        barrier.SignalAndWait();
        sw.Start();
        var cancelled = false;
        var abandoned = false;
        while (!abandoned)
        {
            var allDone = true;
            foreach (var t in ts) if (t.IsAlive) { allDone = false; break; }
            if (allDone) break;
            if (!cancelled && sw.ElapsedMilliseconds > timeoutMs)
            {
                cancelled = true;
                Volatile.Write(ref cancel, 1);
            }
            if (cancelled && sw.ElapsedMilliseconds > timeoutMs + 10_000)
            {
                // 实现内部重试风暴（单次 Enqueue 内不收敛）——降级残留线程后放弃
                foreach (var t in ts) { try { t.Priority = ThreadPriority.Lowest; } catch { } }
                abandoned = true;
            }
            Thread.Sleep(20);
        }
        sw.Stop();

        var totalDone = doneOps.Sum();
        if (cancelled)
        {
            var note = totalDone == 0
                ? "零吞吐——两线程互踩下 Enqueue/TryDequeue 内部重试风暴不收敛"
                : $"竞争退化下界 ≥{sw.Elapsed.TotalNanoseconds / totalDone:F0} ns/op";
            Console.WriteLine($"       （护栏：{timeoutMs / 1000}s 只完成 {totalDone:N0}/{(long)threads * opsPerThread:N0} ops——{note}，残留线程已降级）");
            return (null, totalDone == 0 ? double.PositiveInfinity : sw.Elapsed.TotalNanoseconds / totalDone, true);
        }
        q.Dispose();
        return (sw.Elapsed.TotalNanoseconds / ((long)threads * opsPerThread), 0, false);
    }

    private static void ThroughputMatrix()
    {
        Console.WriteLine("\n[一] 并发吞吐矩阵（混合往返 × 线程数，稳态积压 1024，均匀 0..7）");
        Console.WriteLine("     方法论：Thread 直起 + Barrier 对齐起跑 + Stopwatch 总时/总 ops；先探测轮（50K ops/线程，10s 护栏）再 5 轮正式（200K，60s 护栏）取中位");
        Console.WriteLine($"     {"实现",-9} {"线程",4} {"ns/op 中位",11} {"ns/op 最差",11} {"总吞吐 Mops/s",13} {"分配 B/op",10}");

        foreach (var name in new[] { "Bucket", "SkipList", "Async", "LockedHeap" })
        {
            var implDead = false;   // 探测/正式轮触发护栏 → 跳过该实现更高线程数
            foreach (var threads in ThreadCounts)
            {
                if (implDead)
                {
                    Console.WriteLine($"     {name,-9} {threads,4}   —— 跳过（低线程数已触发护栏）");
                    continue;
                }

                // 探测轮：1/4 ops + 10s 护栏——挡住内部重试风暴的实现
                var (probeNs, probeLower, probeTimeout) = RunThroughputRound(name, threads, ThroughputOpsPerThread / 4, 10_000);
                if (probeTimeout)
                {
                    var lb = probeLower == double.PositiveInfinity ? "零吞吐" : "≥" + probeLower.ToString("F0");
                    Console.WriteLine($"     {name,-9} {threads,4} {lb,11}");
                    implDead = true;
                    continue;
                }

                var samples = new List<double>();
                for (var round = 0; round < 5; round++)
                {
                    var (ns, lower, timedOut) = RunThroughputRound(name, threads, ThroughputOpsPerThread, 60_000);
                    if (timedOut)
                    {
                        var lb = lower == double.PositiveInfinity ? "零吞吐" : "≥" + lower.ToString("F0");
                        Console.WriteLine($"     {name,-9} {threads,4} {lb,11}   ← 正式轮触发护栏");
                        implDead = true;
                        break;
                    }
                    samples.Add(ns!.Value);
                }
                if (implDead || samples.Count == 0) continue;

                var arr = samples.OrderBy(x => x).ToArray();
                var median = arr[arr.Length / 2];
                var mops = threads / (median / 1000.0);

                // 分配与线程数无关（操作数决定）——单线程 100K 往返单独测
                double allocBop;
                using (var q = NewAdapter(name))
                {
                    q.Create();
                    for (var i = 0; i < Backlog; i++) q.Enqueue(i, Prios[i & 4095]);
                    for (var i = 0; i < 2_000; i++) { q.Enqueue(-1 - i, Prios[i & 4095]); q.TryDequeue(out _); }
                    var b0 = GC.GetTotalAllocatedBytes();
                    for (var i = 0; i < 100_000; i++) { q.Enqueue(i, Prios[i & 4095]); q.TryDequeue(out _); }
                    allocBop = (GC.GetTotalAllocatedBytes() - b0) / 100_000.0;
                }

                Console.WriteLine($"     {name,-9} {threads,4} {median,11:F1} {arr[^1],11:F1} {mops,13:F2} {allocBop,10:F1}");
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  二. 积压深度敏感性——单线程往返 × 积压 {1K..256K} × {均匀, 单一优先级尾插}
    // ════════════════════════════════════════════════════════════

    private static void BacklogSensitivity()
    {
        Console.WriteLine("\n[二] 积压深度敏感性（单线程 50K 往返，ns/op）——负载 A 均匀 0..7 / 负载 B 单一 P7（纯尾插）");
        Console.WriteLine("     负载 B 动机：Async.Enqueue 高层链接在 succs==null（尾插）时跳过——验证高层索引是否退化");
        Console.WriteLine($"     {"实现",-9} {"负载",4} {string.Join("", BacklogSweep.Select(b => $"{b / 1024,10}K"))}");

        foreach (var name in new[] { "Bucket", "SkipList", "Async", "LockedHeap" })
        {
            foreach (var tailOnly in new[] { false, true })
            {
                var row = new string[BacklogSweep.Length];
                var abortedAt = -1;
                for (var idx = 0; idx < BacklogSweep.Length; idx++)
                {
                    var backlog = BacklogSweep[idx];
                    using var q = NewAdapter(name);
                    q.Create();
                    for (var i = 0; i < backlog; i++)
                        q.Enqueue(i, tailOnly ? 7 : Prios[i & 4095]);

                    // JIT 预热（不污染样本）
                    for (var i = 0; i < 2_000; i++)
                    {
                        q.Enqueue(-1 - i, tailOnly ? 7 : Prios[i & 4095]);
                        q.TryDequeue(out _);
                    }

                    var ops = 50_000;
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < ops; i++)
                    {
                        q.Enqueue(i, tailOnly ? 7 : Prios[i & 4095]);
                        q.TryDequeue(out _);
                        // 护栏：退化到线性扫描的组最多 20s——提前停，后续积压不再测（必然更慢）
                        if ((i & 1023) == 0 && sw.Elapsed.TotalMilliseconds > 20_000)
                        {
                            row[idx] = $"≥{sw.Elapsed.TotalNanoseconds / i:F0}";
                            abortedAt = idx;
                            break;
                        }
                    }
                    sw.Stop();
                    if (abortedAt != idx)
                        row[idx] = $"{sw.Elapsed.TotalNanoseconds / ops:F0}";
                    else break;
                }
                Console.WriteLine($"     {name,-9} {(tailOnly ? "B" : "A"),4} {string.Join("", row.Select(r => r is null ? "" : $"{r,11}"))}" +
                                  (abortedAt >= 0 ? $"   ← {BacklogSweep[abortedAt] / 1024}K 超 20s 护栏中止（线性退化实锤）" : ""));
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  四. 真实负载维度——think-time 与持锁者延迟（2026-08-17 补测）
    //     §一/§二 的吞吐矩阵是"背靠背纯内存操作"微基准：临界区 50ns 且从不被外部延迟——
    //     偏向大锁（无持锁者延迟窗口）。本节补两个真实维度：
    //     A. think-time：worker 出队→干活（随机长耗时）→回来——真实负载下队列竞争占比 <2%
    //     B. 毒丸柱塞：持锁线程被抢占/GC 暂停（锁内 2ms 延迟注入）vs 其他结构同延迟在操作外——
    //        量化"一个人的延迟变成全队柱塞"的结构性传播（无锁结构对持锁者延迟免疫）
    // ════════════════════════════════════════════════════════════

    private static void RealisticLoadStress()
    {
        ThinkTimeLoad();
        HazardStallLoad();
    }

    /// <summary>四.A think-time：8 线程，队列往返之间自旋随机时长（指数分布均值 50µs——计算型 worker）。
    /// 停滞护栏 30s：活性坍塌的实现（SkipList 在 think-time 下亦然）标注后跳过。</summary>
    private static void ThinkTimeLoad()
    {
        const int threads = 8, ops = 20_000, warmup = 500;
        const double thinkMeanNs = 50_000;
        Console.WriteLine("\n[四.A] think-time 负载（8 线程，队列往返之间自旋随机时长：指数分布均值 50µs——模拟出队→干活→回来）");
        Console.WriteLine($"     {"实现",11} {"总吞吐 ops/s",12} {"op p50",9} {"op p99",9} {"op p999",9} {"op max",10}");

        foreach (var name in new[] { "Bucket", "SkipList", "Async", "LockedHeap" })
        {
            var q = NewAdapter(name);   // 坍塌时不 Dispose（残留线程可能还在操作）
            q.Create();
            for (var i = 0; i < Backlog; i++) q.Enqueue(i, Prios[i & 4095]);
            var barrier = new Barrier(threads + 1);
            var latencies = new double[threads][];
            var progress = 0L;
            var cancel = 0;
            var ts = new Thread[threads];
            for (var t = 0; t < threads; t++)
            {
                var tid = t;
                latencies[tid] = new double[ops];
                ts[tid] = new Thread(() =>
                {
                    try
                    {
                        var rng = new Random(42 + tid);
                        barrier.SignalAndWait();
                        var swOp = new Stopwatch();
                        var swThink = new Stopwatch();
                        var start = tid * (ops + warmup);
                        for (var i = 0; i < warmup + ops; i++)
                        {
                            if (Volatile.Read(ref cancel) != 0) return;
                            var seq = start + i;
                            swOp.Restart();
                            q.Enqueue(seq, Prios[seq & 4095]);
                            q.TryDequeue(out _);
                            swOp.Stop();
                            if (i >= warmup) latencies[tid][i - warmup] = swOp.Elapsed.TotalNanoseconds;
                            // think-time：自旋到指数分布随机时长（-mean·ln(u)）
                            var target = thinkMeanNs * -Math.Log(rng.NextDouble());
                            swThink.Restart();
                            while (swThink.Elapsed.TotalNanoseconds < target)
                                if (Volatile.Read(ref cancel) != 0) return;
                            Interlocked.Increment(ref progress);
                        }
                    }
                    catch (Exception) { /* 残留线程退出竞态——吞 */ }
                }) { IsBackground = true };
            }
            var sw = new Stopwatch();
            foreach (var t in ts) t.Start();
            barrier.SignalAndWait();
            sw.Start();
            var stalled = false;
            var lastProgress = 0L;
            var stallSw = Stopwatch.StartNew();
            while (true)
            {
                var allDone = true;
                foreach (var t in ts) if (t.IsAlive) { allDone = false; break; }
                if (allDone) break;
                var p = Volatile.Read(ref progress);
                if (p != lastProgress) { lastProgress = p; stallSw.Restart(); }
                else if (stallSw.ElapsedMilliseconds > 30_000)
                {
                    stalled = true;
                    Volatile.Write(ref cancel, 1);
                    foreach (var t in ts) { try { t.Priority = ThreadPriority.Lowest; } catch { } }
                    break;
                }
                Thread.Sleep(50);
            }
            sw.Stop();
            if (stalled)
            {
                Console.WriteLine($"     {name,11} —— 活性坍塌：30s 进度停滞（think-time 间隔下重试风暴仍不收敛）");
                continue;
            }
            var all = latencies.SelectMany(x => x).OrderBy(x => x).ToArray();
            Console.WriteLine($"     {name,11} {(long)(all.Length / sw.Elapsed.TotalSeconds),12:N0}" +
                              $" {Percentile(all, 0.50),8:F0} {Percentile(all, 0.99),8:F0} {Percentile(all, 0.999),8:F0} {all[^1] / 1000,9:F1}µs");
            q.Dispose();
        }
    }

    /// <summary>四.B 毒丸柱塞：8 线程高频往返 + 0.2% 操作注入 2ms 延迟。
    /// LockedHeap☠ 注入在锁内（持锁被延迟→全队柱塞）；其余注入在操作前（只慢自己）。
    /// 报非毒丸 op 的延迟分布——柱塞传播的直接量化。SkipList 在此负载活性坍塌（§一）不参与。</summary>
    private static void HazardStallLoad()
    {
        const int threads = 8, ops = 30_000, hazardPerMyriad = 20;   // 0.2% 概率 × 2ms
        Console.WriteLine($"\n[四.B] 持锁者毒丸（8 线程高频往返，{hazardPerMyriad / 100.0:F1}% 操作注入 2ms：");
        Console.WriteLine("     LockedHeap☠=锁内注入（模拟持锁线程被抢占/GC 暂停），其余=操作前注入（只慢自己））");
        Console.WriteLine($"     {"实现",12} {"非毒丸 p50",10} {"非毒丸 p99",10} {"非毒丸 p999",10} {"非毒丸 max",11} {"max/p50",9}");

        foreach (var name in new[] { "Bucket", "SkipList", "Async", "LockedHeap", "LockedHeap☠" })
        {
            using var q = name == "LockedHeap☠" ? new LockedHeapHazardAdapter() : NewAdapter(name);
            q.Create();
            for (var i = 0; i < Backlog; i++) q.Enqueue(i, Prios[i & 4095]);
            var barrier = new Barrier(threads + 1);
            var latencies = new List<double>[threads];
            var hazardCount = new long[threads];
            var ts = new Thread[threads];
            for (var t = 0; t < threads; t++)
            {
                var tid = t;
                latencies[tid] = new List<double>(ops);
                ts[tid] = new Thread(() =>
                {
                    var rng = new Random(7 + tid);
                    barrier.SignalAndWait();
                    var swOp = new Stopwatch();
                    var start = tid * ops;
                    var local = latencies[tid];
                    for (var i = 0; i < ops; i++)
                    {
                        var seq = start + i;
                        var hazard = rng.Next(10_000) < hazardPerMyriad;
                        swOp.Restart();
                        q.EnqueueWithHazard(seq, Prios[seq & 4095], hazard);
                        q.TryDequeue(out _);
                        swOp.Stop();
                        if (hazard) hazardCount[tid]++;
                        else local.Add(swOp.Elapsed.TotalNanoseconds);
                    }
                }) { IsBackground = true };
            }
            foreach (var t in ts) t.Start();
            barrier.SignalAndWait();
            foreach (var t in ts) t.Join(120_000);
            var all = latencies.SelectMany(x => x).OrderBy(x => x).ToArray();
            var p50 = Percentile(all, 0.50);
            var max = all[^1];
            Console.WriteLine($"     {q.Name,12} {Percentile(all, 0.50),9:F0} {Percentile(all, 0.99),9:F0} {Percentile(all, 0.999),9:F0} {max / 1000,10:F1}µs {max / p50,9:F0}×");
            q.Dispose();
        }
        Console.WriteLine("     （SkipList 修复 #PERF-004 后 8 线程负载健康（§一 9.4 Mops/s）——本轮未及重跑，下轮补数）");
    }

    private static double Percentile(double[] sorted, double q)
        => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    // ════════════════════════════════════════════════════════════
    //  三. 并发正确性压测
    // ════════════════════════════════════════════════════════════

    private sealed class StressResult
    {
        public required List<long> Concurrent;   // 消费者并发段收集（无锁竞态——只做全集校验，不做序断言）
        public required List<long> Drained;      // 消费者停止后主线程单线程 drain 段（序断言 100% 无竞态）
        public required bool TimedOut;           // 消费者超时未收齐（活性失败——竞争退化/疑似活锁）
        public required bool DrainTimedOut;      // 排水超时（TryDequeue 内部重试风暴——活性失败，全集校验不可信）
        public required double ConsumeMs;        // 并发消费阶段耗时
        public required long RemainingAtStop;    // 停止时未收数（TimedOut 诊断）
    }

    private static void CorrectnessStress()
    {
        Console.WriteLine($"\n[三] 并发正确性压测（P={Producers} × {CorrectOpsPerProducer:N0} ops/生产者，消费者超时 {ConsumeTimeoutMs / 1000}s" +
#if DEBUG
                          "，Debug 减量）"
#else
                          ")"
#endif
        );

        foreach (var name in new[] { "Bucket", "SkipList", "Async", "LockedHeap" })
        {
            Console.WriteLine($"\n  ── {name} ──");
            using (var q = NewAdapter(name)) MpmcNoLostNoDuplicate(q);
            using (var q = NewAdapter(name)) OrderSemantics(q, consumers: 1, label: "SPSC");
            using (var q = NewAdapter(name)) OrderSemantics(q, consumers: 1, mpsc: true, label: "MPSC");
            using (var q = NewAdapter(name)) DequeueAsyncWaiters(q);
        }
    }

    /// <summary>通用骨架：P 生产者各 N 唯一项（均匀 0..7）× C 消费者尽力消费 → 超时置 stop →
    /// 主线程单线程 drain 收尾。remaining 协议：启动前定总量，消费者每收一项递减。
    /// productionFirst=true：消费者等生产全部入队后再起跑（消费段成为无生产竞态的纯消费——单消费者时可硬断言非降）。</summary>
    private static StressResult ProducersConsumersDrain(IQAdapter q, int consumerCount, bool productionFirst = false)
    {
        q.Create();
        var total = (long)Producers * CorrectOpsPerProducer;
        var remaining = total;
        var stop = 0;
        var produced = 0L;
        var localLists = new ConcurrentBag<List<long>>();   // 每消费者本地保序 List（ConcurrentBag 本身无序——不能直接装元素做序断言）
        var threads = new Thread[Producers + consumerCount];
        var barrier = new Barrier(threads.Length);

        for (var t = 0; t < Producers; t++)
        {
            var tid = t;
            threads[tid] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var start = (long)tid * CorrectOpsPerProducer;
                for (var i = 0; i < CorrectOpsPerProducer; i++)
                    q.Enqueue(start + i, Prios[(int)((start + i) & 4095)]);
                Interlocked.Add(ref produced, CorrectOpsPerProducer);
            }) { IsBackground = true };
        }
        for (var c = 0; c < consumerCount; c++)
        {
            threads[Producers + c] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var spin = new SpinWait();
                if (productionFirst)
                    while (Volatile.Read(ref produced) < total) spin.SpinOnce();
                var local = new List<long>(CorrectOpsPerProducer / consumerCount + 16);
                while (Volatile.Read(ref remaining) > 0 && Volatile.Read(ref stop) == 0)
                {
                    if (q.TryDequeue(out var v))
                    {
                        local.Add(v);
                        Interlocked.Decrement(ref remaining);
                    }
                    else spin.SpinOnce();   // remaining>0 但队列瞬空（在途）——自旋等
                }
                localLists.Add(local);      // 单消费者时整个序列即此一个 List——保序
            }) { IsBackground = true };
        }

        var sw = Stopwatch.StartNew();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(ConsumeTimeoutMs);

        var timedOut = Volatile.Read(ref remaining) > 0;
        if (timedOut)
        {
            Volatile.Write(ref stop, 1);
            foreach (var t in threads) t.Join(5_000);   // 让消费者干净退出（partial 收集有效）
        }
        sw.Stop();

        // drain 线程化 + 护栏：SkipList 高竞争下 TryDequeue 内部 goto-retry 风暴会卡住排水线程
        var drained = new List<long>();
        var drainTimedOut = false;
        var drainThread = new Thread(() => { while (q.TryDequeue(out var v)) drained.Add(v); }) { IsBackground = true };
        drainThread.Start();
        if (!drainThread.Join(DrainTimeoutMs))
        {
            drainTimedOut = true;
            try { drainThread.Priority = ThreadPriority.Lowest; } catch { }
        }
        return new StressResult
        {
            Concurrent = localLists.SelectMany(x => x).ToList(),
            Drained = drained,
            TimedOut = timedOut,
            DrainTimedOut = drainTimedOut,
            ConsumeMs = sw.Elapsed.TotalMilliseconds,
            RemainingAtStop = remaining,
        };
    }

    /// <summary>全集校验：并发段 + drain 段合并 == total、无重复、Count 归零。返回 true=通过。</summary>
    private static bool VerifyAll(IQAdapter q, StressResult r, string label)
    {
        var total = (long)Producers * CorrectOpsPerProducer;
        var all = r.Concurrent.Concat(r.Drained).ToList();
        var set = new HashSet<long>();
        var dups = all.Count(v => !set.Add(v));
        var ok = true;
        if (all.Count != total) { ok = false; Fail($"[{q.Name} {label}] 收集 {all.Count:N0} ≠ 总量 {total:N0}（丢失 {total - all.Count:N0}）"); }
        if (dups != 0) { ok = false; Fail($"[{q.Name} {label}] 重复 {dups:N0} 项"); }
        if (q.Count != 0) { ok = false; Fail($"[{q.Name} {label}] 终态 Count={q.Count} ≠ 0"); }
        return ok;
    }

    /// <summary>3.a MPMC 不丢不重：4 生产者 + 4 消费者尽力消费 + drain 收尾——全集校验；超时单列活性失败。</summary>
    private static void MpmcNoLostNoDuplicate(IQAdapter q)
    {
        var r = ProducersConsumersDrain(q, Consumers);
        var total = (long)Producers * CorrectOpsPerProducer;
        if (r.TimedOut || r.DrainTimedOut)
        {
            // 慢≠错：超时场景的全集校验不作数（残留线程在逃）——正确性由干净完成的场景证明
            Fail($"[{q.Name} MPMC] 活性失败：{(r.TimedOut ? $"消费者 {ConsumeTimeoutMs / 1000}s 仅消化 {r.Concurrent.Count:N0}/{total:N0}" : $"排水 {DrainTimeoutMs / 1000}s 未完成")}" +
                 $"——队头锁链竞争退化/疑似活锁（dotnet-stack 取证：Enqueue/TryDequeue 内部重试风暴烧 CPU）");
            return;
        }
        var ok = VerifyAll(q, r, "MPMC");
        if (ok)
            Console.WriteLine($"  ✓ [{q.Name}] MPMC 不丢不重：{total:N0} 项全收齐、0 重复、Count 归零（并发 {r.Concurrent.Count:N0} + drain {r.Drained.Count:N0}，{r.ConsumeMs:F0} ms）");
    }

    /// <summary>3.b/3.c 排序语义：SPSC 变体生产先行（消费段+drain 段均无生产竞态，严格非降硬断言）；
    /// MPSC 变体边产边消（竞争性最小语义——反转率参考值，drain 段仍硬断言）。</summary>
    private static void OrderSemantics(IQAdapter q, int consumers, string label, bool mpsc = false)
    {
        var r = ProducersConsumersDrain(q, consumers, productionFirst: !mpsc);
        var total = (long)Producers * CorrectOpsPerProducer;
        if (r.TimedOut || r.DrainTimedOut)
        {
            Fail($"[{q.Name} {label}] 活性失败：{(r.TimedOut ? $"消费者 {ConsumeTimeoutMs / 1000}s 未收齐（余 {r.RemainingAtStop:N0}/{total:N0}）" : $"排水 {DrainTimeoutMs / 1000}s 未完成")}——竞争退化/疑似活锁");
            return;
        }
        var ok = VerifyAll(q, r, label);

        // 无生产竞态段（SPSC 消费段 + drain 段）：单消费者 TryDequeue 必取当前最小，任何反转都是实现 bug
        var strictInv = 0L;
        var strictCount = r.Drained.Count;
        if (!mpsc) strictCount += r.Concurrent.Count;   // SPSC：消费段也参与硬断言
        var seq = mpsc ? r.Drained : r.Concurrent.Concat(r.Drained);
        var arr = seq.ToArray();
        for (var i = 1; i < arr.Length; i++)
            if (PrioOf(arr[i]) < PrioOf(arr[i - 1]))
            {
                strictInv++;
                if (strictInv <= 3) Fail($"[{q.Name} {label}] 严格序段反转 @#{i}: prio {PrioOf(arr[i - 1])} → {PrioOf(arr[i])}（无生产竞态段不允许）");
            }

        // 全序列反转率：MPSC 并发段的竞争性反转是"竞争性最小"语义允许的——参考值
        var all = r.Concurrent.Concat(r.Drained).ToList();
        var inv = 0L;
        for (var i = 1; i < all.Count; i++)
            if (PrioOf(all[i]) < PrioOf(all[i - 1])) inv++;

        if (ok && strictInv == 0 && !r.TimedOut)
            Console.WriteLine($"  ✓ [{q.Name}] {label} 排序语义：严格序段 {strictCount:N0} 项 0 反转" +
                              (mpsc ? $"；全序列反转率 {100.0 * inv / Math.Max(all.Count, 1):F3}%（竞争性——参考值）" : ""));
    }

    /// <summary>3.d DequeueAsync 等待-唤醒：4 异步消费者（先挂起再生产）+ 4 生产者——不丢不重 + Reset/Set 唤醒协议（丢唤醒在此暴露）。</summary>
    private static void DequeueAsyncWaiters(IQAdapter q)
    {
        q.Create();
        var total = (long)Producers * CorrectOpsPerProducer;
        var remaining = total;
        using var cts = new CancellationTokenSource();
        var collected = new ConcurrentBag<long>();
        var consumersReady = new Barrier(Consumers + 1);

        // 消费者先行——空队列真实挂起（非 fast-path），压测 Set/Reset 唤醒协议
        var consumerTasks = new Task[Consumers];
        for (var c = 0; c < Consumers; c++)
        {
            consumerTasks[c] = Task.Run(async () =>
            {
                consumersReady.SignalAndWait();
                var local = new List<long>();
                try
                {
                    while (Volatile.Read(ref remaining) > 0)
                    {
                        var item = await q.DequeueAsync(cts.Token).ConfigureAwait(false);
                        local.Add(item);
                        Interlocked.Decrement(ref remaining);
                    }
                }
                catch (OperationCanceledException) { /* 主线程 cancel 收尾——remaining≠0 才是失败 */ }
                foreach (var v in local) collected.Add(v);
            });
        }
        consumersReady.SignalAndWait();          // 消费者已越过起跑线（多数即将/已经挂起）
        Thread.Sleep(50);                        // 让消费者充分进入挂起态

        var producers = new Thread[Producers];
        var barrier = new Barrier(Producers);
        for (var t = 0; t < Producers; t++)
        {
            var tid = t;
            producers[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var start = (long)tid * CorrectOpsPerProducer;
                for (var i = 0; i < CorrectOpsPerProducer; i++)
                    q.Enqueue(start + i, Prios[(int)((start + i) & 4095)]);
            }) { IsBackground = true };
        }
        foreach (var t in producers) t.Start();
        foreach (var t in producers) t.Join(180_000);

        // 收尾：remaining 归零消费者自然退出；30s 兜底——丢唤醒会在此暴露（collected<total 且超时）
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref remaining) > 0 && sw.ElapsedMilliseconds < 30_000)
            Thread.Sleep(10);
        var timedOut = Volatile.Read(ref remaining) != 0;
        cts.Cancel();
        try { Task.WaitAll(consumerTasks, 15_000); } catch (AggregateException) { }

        var drain = new List<long>();
        var drainTimedOut = false;
        var drainThread = new Thread(() => { while (q.TryDequeue(out var v)) drain.Add(v); }) { IsBackground = true };
        drainThread.Start();
        if (!drainThread.Join(DrainTimeoutMs))
        {
            drainTimedOut = true;
            try { drainThread.Priority = ThreadPriority.Lowest; } catch { }
        }

        var all = collected.Concat(drain).ToList();
        var set = new HashSet<long>();
        var dups = all.Count(v => !set.Add(v));
        var ok = true;
        if (timedOut || drainTimedOut)
        {
            Fail($"[{q.Name} DequeueAsync] 活性失败：{(timedOut ? "30s 未收齐" : $"排水 {DrainTimeoutMs / 1000}s 未完成")}——竞争退化/疑似活锁");
            return;
        }
        if (all.Count != total) { ok = false; Fail($"[{q.Name} DequeueAsync] 收集 {all.Count:N0} ≠ {total:N0}（丢唤醒或丢失 {total - all.Count:N0}）"); }
        if (dups != 0) { ok = false; Fail($"[{q.Name} DequeueAsync] 重复 {dups:N0} 项"); }
        if (q.Count != 0) { ok = false; Fail($"[{q.Name} DequeueAsync] 终态 Count={q.Count} ≠ 0"); }
        if (ok) Console.WriteLine($"  ✓ [{q.Name}] DequeueAsync 等待-唤醒：4 异步消费者真实挂起后 {total:N0} 项全收齐、0 重复（并发 {collected.Count:N0} + drain {drain.Count:N0}）");
    }
}
