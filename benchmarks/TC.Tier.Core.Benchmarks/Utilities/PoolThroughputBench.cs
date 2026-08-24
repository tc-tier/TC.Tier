using System.Buffers;
using System.Diagnostics;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

// ============================================================================
// 第三大类：并发与多线程性能 — 吞吐、伸缩性、高并发长尾、归还专项
// 目标伸缩比：8 线程 ≥ 6（规范三.1 合格参考）
// ============================================================================

/// <summary>
/// 三.1 并发伸缩性：1/2/4/8/16 线程下总吞吐，计算伸缩比（N 线程吞吐 / 单线程吞吐）。
///
/// 方法论（P0 修正）：用手动长生命周期的 <see cref="Thread"/>（非线程池线程），
/// 每个线程独立跑固定次数 Rent/Return，完全匹配 thread-local 设计前提。
/// 关键：
///   - 充分预热：GlobalSetup 先建分桶 + 每线程 warmup 迭代触发所有 ThreadLocal 实例化与
///     桶填充，确保计时阶段全命中、零初始化开销。
///   - barrier 同步起停：所有 worker 同时进入计时循环，消除起停噪声。
///   - 固定每线程工作量：N 线程总工作量 = N × IterationsPerThread，吞吐 = 总量 / 总墙钟时间。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
public class ScalabilityBench : IDisposable
{
    private PinnedBufferPool _pool = null!;

    [Params(4096)]
    public int Size { get; set; }

    [Params(1, 2, 4, 6, 8, 12)]
    public int DegreeOfParallelism { get; set; }

    // 每线程工作量（百万级，确保计时区远大于起停开销）
    [Params(1_000_000)]
    public int IterationsPerThread { get; set; }

    // 计时区同步：所有 worker 同时开始、同时结束
    private ManualResetEventSlim _startGate = null!;
    private CountdownEvent _readyGate = null!;

    // 每次 BDN 迭代重建池，保证干净状态（避免 Count 跨迭代累积触顶 maxPerBucket）
    [IterationSetup]
    public void IterationSetup()
    {
        // maxPerBucket 给足（≥ 线程数 × 每线程持有量），避免 thread-local 模式下
        // 的全桶 Count 近似值误触限流。真实生产也应给足容量。
        _pool = new PinnedBufferPool(maxPerBucket: 256);
        _startGate = new ManualResetEventSlim(false);
        _readyGate = new CountdownEvent(DegreeOfParallelism);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _pool.Dispose();
        _startGate.Dispose();
        _readyGate.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _pool?.Dispose();
        _startGate?.Dispose();
        _readyGate?.Dispose();
    }

    [Benchmark(Description = "Concurrent Rent/Return")]
    public void Run()
    {
        _startGate.Reset();
        _readyGate.Reset(_readyGate.InitialCount); // 重置 countdown 到初始线程数

        // 每个 worker：先预热（建立本线程的 thread-local 栈 + 填充命中 buffer），
        // 再就绪等待，最后跑计时循环。预热在 barrier 之前完成，不计入计时。
        var threads = new Thread[DegreeOfParallelism];
        for (int t = 0; t < DegreeOfParallelism; t++)
        {
            threads[t] = new Thread(() =>
            {
                // 预热本线程：触发 ThreadLocal.Value 实例化 + 把 buffer 留在自己栈里
                var warm = _pool.Rent(Size);
                _pool.Return(warm);
                // 再多 warmup 几轮，确保栈稳定命中（消除首次 GetOrAdd 等开销）
                for (int i = 0; i < 16; i++)
                {
                    var b = _pool.Rent(Size);
                    _pool.Return(b);
                }

                // 就绪 → 等统一起跑信号
                _readyGate.Signal();
                _startGate.Wait();

                // 计时循环（稳态全命中 thread-local 栈）
                for (int i = 0; i < IterationsPerThread; i++)
                {
                    var buf = _pool.Rent(Size);
                    buf[0] = 42;
                    _pool.Return(buf);
                }
            }) { IsBackground = true };
            threads[t].Start();
        }

        // 等所有 worker 预热完成并就绪后统一起跑。
        // BDN 的 Mean ≈ 起跑到全部 join 的墙钟（线程创建/预热在 barrier 前完成，不计入）。
        // 报告里按 ops/s = (N × IterationsPerThread) / Mean 换算吞吐与伸缩比。
        _readyGate.Wait();
        _startGate.Set();
        foreach (var th in threads) th.Join();
    }
}

/// <summary>
/// 一.2 单线程吞吐率（ops 概念）：固定时间窗内 Rent+Return 次数，对照无池基线。
/// 这里用"每 op 耗时"的倒数近似 ops/sec（BDN 直接给 Mean，报告里换算）。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class ThroughputBench : IDisposable
{
    private PinnedBufferPool _pool = null!;

    [Params(4096, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _pool = new PinnedBufferPool(maxPerBucket: 64);
        _pool.Return(_pool.Rent(Size));
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _pool?.Dispose();
    }

    /// <summary>池化 byte[]（命中）。报告里用 1/Mean 换算 ops/sec。</summary>
    [Benchmark(Baseline = true, Description = "Pool byte[] (ops)")]
    public byte PoolOps()
    {
        var buf = _pool.Rent(Size);
        _pool.Return(buf);
        return buf[0];
    }

    /// <summary>ArrayPool.Shared 对照（非 pinned，thread-static 分桶）。</summary>
    [Benchmark(Description = "ArrayPool.Shared (ops)")]
    public byte ArrayPoolOps()
    {
        var buf = ArrayPool<byte>.Shared.Rent(Size);
        ArrayPool<byte>.Shared.Return(buf);
        return buf[0];
    }

    /// <summary>无池基线：每次 new+Dispose。报告里换算 ops/sec 对照。</summary>
    [Benchmark(Description = "No-pool new+Dispose (ops)")]
    public byte NoPoolOps()
    {
        using var m = new AlignedMemoryManager(Size, 4096);
        return m.GetSpan()[0];
    }
}

/// <summary>
/// 三.3 归还路径专项：纯归还耗时（先批量租借持有，再逐个归还）。
/// 验证归还路径（thread-local push + Count 限流）无性能坑。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class ReturnOnlyBench : IDisposable
{
    private PinnedBufferPool _pool = null!;
    private byte[][] _held = null!;

    [Params(4096)]
    public int Size { get; set; }

    // 归还批量：单次 Benchmark 调用归还 N 个 buffer
    [Params(64)]
    public int Batch { get; set; }

    // 每次迭代前重新租借一批，供 Benchmark 体纯归还
    [IterationSetup]
    public void IterationSetup()
    {
        _pool = new PinnedBufferPool(maxPerBucket: Batch * 2);
        _held = new byte[Batch][];
        for (int i = 0; i < Batch; i++)
            _held[i] = _pool.Rent(Size);
    }

    [IterationCleanup]
    public void IterationCleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _pool?.Dispose();
    }

    [Benchmark(Description = "Return-only (N bufs)")]
    public int ReturnBatch()
    {
        for (int i = 0; i < Batch; i++)
            _pool.Return(_held[i]);
        return Batch;
    }
}
