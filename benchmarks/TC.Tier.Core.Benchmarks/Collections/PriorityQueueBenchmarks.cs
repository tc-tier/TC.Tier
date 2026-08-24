using BenchmarkDotNet.Attributes;
using TC.Tier.Core.Collections;

// ★ CA1001 抑制：BDN 基准实例由 harness 生命周期管理（全局/迭代期资源，无 Dispose 时机）
#pragma warning disable CA1001

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// 优先级队列三实现对比基准——Bucket(离散枚举) / SkipList(任意 long) / Async(无锁跳表 Route A)。
/// <para>★ 基线参照：.NET 内置 <c>PriorityQueue</c>（单线程非线程安全——抽象税上界参照）。</para>
/// <para>★ 实验版本 V2/V3 不入基准（实验——非生产，类上 Experimental 标记）。</para>
/// <para>★ 数据报告：<c>src/TC.Tier.Core/docs/perf/priority-queues-performance.md</c>。</para>
/// </summary>
[MemoryDiagnoser]
public class PriorityQueueBenchmarks
{
    private enum Prio8 : short { P0, P1, P2, P3, P4, P5, P6, P7 }

    private const int Backlog = 1024;          // 稳态积压（混合往返保持队列大小不变）

    private PriorityQueue<int, int> _builtin = null!;
    private BucketPriorityQueue<Prio8, int> _bucket = null!;
    private SkipListPriorityQueue<int> _skip = null!;
    private AsyncPriorityQueue<int> _async = null!;
    private int[] _prios = null!;              // 确定性优先级负载（循环取用）

    [GlobalSetup]
    public void Setup()
    {
        _prios = new int[4096];
        var rng = new Random(42);
        for (var i = 0; i < _prios.Length; i++)
            _prios[i] = rng.Next(8);           // 0..7——与 Bucket 8 桶同分布

        _builtin = new PriorityQueue<int, int>();
        _bucket = new BucketPriorityQueue<Prio8, int>();
        _skip = new SkipListPriorityQueue<int>();
        _async = new AsyncPriorityQueue<int>();

        // 稳态积压填充（混合基准的前提——队列大小恒定）
        for (var i = 0; i < Backlog; i++)
        {
            var p = _prios[i];
            _builtin.Enqueue(i, p);
            _bucket.Enqueue(i, (Prio8)p);
            _skip.Enqueue(i, p);
            _async.Enqueue(i, p);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bucket.Dispose();
        _skip.Dispose();
        _async.Dispose();
    }

    private int _idx;

    private int NextPrio() => _prios[(_idx = (_idx + 1) & 4095)];    // ═══════════ 单线程混合往返（Enqueue 1 + TryDequeue 1——稳态积压 1024）═══════════

    [Benchmark(Baseline = true, Description = "内置 PriorityQueue（非线程安全基线）")]
    public void BuiltinRoundTrip()
    {
        _builtin.Enqueue(_idx, NextPrio());
        _builtin.TryDequeue(out _, out _);
    }

    [Benchmark(Description = "Bucket 离散 8 桶")]
    public void BucketRoundTrip()
    {
        _bucket.Enqueue(_idx, (Prio8)NextPrio());
        _bucket.TryDequeue(out _);
    }

    [Benchmark(Description = "SkipList 任意 long")]
    public void SkipListRoundTrip()
    {
        _skip.Enqueue(_idx, NextPrio());
        _skip.TryDequeue(out _);
    }

    [Benchmark(Description = "Async 无锁跳表 Route A")]
    public void AsyncRoundTrip()
    {
        _async.Enqueue(_idx, NextPrio());
        _async.TryDequeue(out _);
    }

    // ═══════════ 并发混合往返（4 线程 × 50K 次）═══════════
    // ★ 不入 BDN：本机 BDN Parallel 组高度不确定——并发数据由独立计时探针产出（已落地）：
    //   PriorityQueueStressProbe（--pq-probe：吞吐矩阵 1/2/4/8T + 积压敏感性 + 并发正确性）
    //   PqWedgeRepro（--pq-wedge：活性死锁复现器——2026-08-17 标记-splice 竞态修复的回归绊线）
    //   负载定义（Enqueue 1 + TryDequeue 1，稳态积压 1024，均匀 0..7，4 线程 × 50K/线程）
    //   由探针 1:1 复刻，数据报告：src/TC.Tier.Core/docs/perf/priority-queues-performance.md §2/§4/§5。
}
