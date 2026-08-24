using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// OverflowPool 性能基准（对齐 PoolAccessBench 范式）。
/// 测量 TryAdd+TryGet 往返、单线程吞吐、并发吞吐、overflow 场景。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class OverflowPoolBench : IDisposable
{
    [Params(64, 256, 1024)]
    public int PoolSize { get; set; }

    [Params(1000)]
    public int ItemCount { get; set; }

    private OverflowPool<int> _pool = null!;

    [GlobalSetup]
    public void Setup() => _pool = new OverflowPool<int>(PoolSize);

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _pool?.Dispose();
    }

    /// <summary>单次 TryAdd+TryGet 往返延迟（池有空闲项，无 overflow）。</summary>
    [Benchmark(Description = "TryAdd+TryGet roundtrip (hit)", Baseline = true)]
    public int Roundtrip_Hit()
    {
        _pool.TryAdd(1);
        _pool.TryGet(out var item);
        return item;
    }

    /// <summary>TryGet 空池未命中延迟。</summary>
    [Benchmark(Description = "TryGet empty (miss)")]
    public bool TryGet_Empty()
    {
        // 先排空
        while (_pool.TryGet(out _)) { }
        return _pool.TryGet(out _);
    }

    /// <summary>单线程顺序吞吐：反复填充+排空（池内往返）。</summary>
    [Benchmark(Description = "Serial fill+drain throughput")]
    public int SerialFillDrain()
    {
        int sum = 0;
        for (int i = 0; i < ItemCount; i++)
        {
            _pool.TryAdd(i);
            if (_pool.TryGet(out var v)) sum += v;
        }
        return sum;
    }

    /// <summary>overflow 场景：持续 TryAdd 超容量项（测 disposer 回收开销）。</summary>
    [Benchmark(Description = "Overflow (TryAdd over cap)")]
    public int OverflowOverCap()
    {
        // 先填满
        while (_pool.TryAdd(0)) { }
        int overflows = 0;
        for (int i = 0; i < ItemCount; i++)
        {
            if (!_pool.TryAdd(i)) overflows++;
        }
        return overflows;
    }

    /// <summary>并发生产-消费吞吐（多线程 TryAdd+TryGet 混合）。</summary>
    [Benchmark(Description = "Concurrent producer-consumer")]
    public long ConcurrentProducerConsumer()
    {
        const int threadCount = 4;
        int perThread = ItemCount / threadCount;
        var tasks = new Task[threadCount];
        long totalHits = 0;
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    _pool.TryAdd(i);
                    if (_pool.TryGet(out _))
                        Interlocked.Increment(ref totalHits);
                }
            });
        }
        Task.WaitAll(tasks);
        return totalHits;
    }
}
