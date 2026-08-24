using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// ClockCache（V1 开放寻址）/ ClockCacheV2（组相联）vs ConcurrentLruCache（ConcurrentDictionary+LinkedList）性能对比基准。
/// <para>★ 用数据证明自研 CLOCK 算法优于 ConcurrentDictionary 方案多少；V2 与 V1 的差异（miss 悬崖）同台可见。</para>
/// <para>运行: dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks/ -- --filter "*ClockCacheBench*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class ClockCacheBench:IDisposable
{
    private long[] _hitKeys = null!;
    private long[] _missKeys = null!;
    private long _nextKey;
    private ClockCache<long, string> _clockCache = null!;
    private ClockCacheV2<long, string> _clockCacheV2 = null!;
    private ConcurrentLruCache<long, string> _concurrentLru = null!;

    /// <summary>缓存容量（2 的幂）。</summary>
    [Params(128, 1024)]
    public int Capacity { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _clockCache = new ClockCache<long, string>(Capacity);
        _clockCacheV2 = new ClockCacheV2<long, string>(Capacity);
        _concurrentLru = new ConcurrentLruCache<long, string>(Capacity);
        _hitKeys = new long[Capacity];
        for (int i = 0; i < Capacity; i++)
        {
            long key = (i + 1) * 7;
            _clockCache.Put(key, $"val-{key}");
            _clockCacheV2.Put(key, $"val-{key}");
            _concurrentLru.Put(key, $"val-{key}");
            _hitKeys[i] = key;
        }
        _missKeys = new long[100];
        for (int i = 0; i < 100; i++)
            _missKeys[i] = (Capacity + i + 1) * 13;
        _nextKey = (long)Capacity * 100 + 1;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _clockCache.Dispose();
        _clockCacheV2.Dispose();
        _concurrentLru.Dispose();
    }

    // === TryGet Hit 对比 ===

    [Benchmark(Description = "Clock_TryGet_Hit", Baseline = true)]
    public bool ClockTryGetHit()
    {
        bool found = false;
        foreach (var key in _hitKeys)
            found = _clockCache.TryGet(key, out _);
        return found;
    }

    [Benchmark(Description = "ConcurrentLru_TryGet_Hit")]
    public bool ConcurrentLruTryGetHit()
    {
        bool found = false;
        foreach (var key in _hitKeys)
            found = _concurrentLru.TryGet(key, out _);
        return found;
    }

    // === TryGet Miss 对比 ===

    [Benchmark(Description = "Clock_TryGet_Miss")]
    public bool ClockTryGetMiss()
    {
        bool found = false;
        foreach (var key in _missKeys)
            found = _clockCache.TryGet(key, out _);
        return found;
    }

    [Benchmark(Description = "ConcurrentLru_TryGet_Miss")]
    public bool ConcurrentLruTryGetMiss()
    {
        bool found = false;
        foreach (var key in _missKeys)
            found = _concurrentLru.TryGet(key, out _);
        return found;
    }

    // === Put Update 对比 ===

    [Benchmark(Description = "Clock_Put_Update")]
    public void ClockPutUpdate()
    {
        foreach (var key in _hitKeys)
            _clockCache.Put(key, "updated");
    }

    [Benchmark(Description = "ConcurrentLru_Put_Update")]
    public void ConcurrentLruPutUpdate()
    {
        foreach (var key in _hitKeys)
            _concurrentLru.Put(key, "updated");
    }

    // === Put Evict 对比 ===

    [Benchmark(Description = "Clock_Put_Evict")]
    public long ClockPutEvict()
    {
        long key = Interlocked.Increment(ref _nextKey);
        _clockCache.Put(key, $"new-{key}");
        return key;
    }

    [Benchmark(Description = "ConcurrentLru_Put_Evict")]
    public long ConcurrentLruPutEvict()
    {
        long key = Interlocked.Increment(ref _nextKey);
        _concurrentLru.Put(key, $"new-{key}");
        return key;
    }

    // === 混合负载对比 ===

    [Benchmark(Description = "Clock_Mixed_80_20")]
    public bool ClockMixed()
    {
        var rng = new Random(42);
        bool found = false;
        for (int i = 0; i < 1000; i++)
        {
            if (rng.Next(5) == 0)
                found = _clockCache.TryGet(_missKeys[rng.Next(_missKeys.Length)], out _);
            else
                found = _clockCache.TryGet(_hitKeys[rng.Next(_hitKeys.Length)], out _);
        }
        return found;
    }

    [Benchmark(Description = "ConcurrentLru_Mixed_80_20")]
    public bool ConcurrentLruMixed()
    {
        var rng = new Random(42);
        bool found = false;
        for (int i = 0; i < 1000; i++)
        {
            if (rng.Next(5) == 0)
                found = _concurrentLru.TryGet(_missKeys[rng.Next(_missKeys.Length)], out _);
            else
                found = _concurrentLru.TryGet(_hitKeys[rng.Next(_hitKeys.Length)], out _);
        }
        return found;
    }

    // === ClockCacheV2（组相联）对比 ===

    [Benchmark(Description = "ClockV2_TryGet_Hit")]
    public bool ClockV2TryGetHit()
    {
        bool found = false;
        foreach (var key in _hitKeys)
            found = _clockCacheV2.TryGet(key, out _);
        return found;
    }

    [Benchmark(Description = "ClockV2_TryGet_Miss")]
    public bool ClockV2TryGetMiss()
    {
        bool found = false;
        foreach (var key in _missKeys)
            found = _clockCacheV2.TryGet(key, out _);
        return found;
    }

    [Benchmark(Description = "ClockV2_Put_Update")]
    public void ClockV2PutUpdate()
    {
        foreach (var key in _hitKeys)
            _clockCacheV2.Put(key, "updated");
    }

    [Benchmark(Description = "ClockV2_Put_Evict")]
    public long ClockV2PutEvict()
    {
        long key = Interlocked.Increment(ref _nextKey);
        _clockCacheV2.Put(key, $"new-{key}");
        return key;
    }

    [Benchmark(Description = "ClockV2_Mixed_80_20")]
    public bool ClockV2Mixed()
    {
        var rng = new Random(42);
        bool found = false;
        for (int i = 0; i < 1000; i++)
        {
            if (rng.Next(5) == 0)
                found = _clockCacheV2.TryGet(_missKeys[rng.Next(_missKeys.Length)], out _);
            else
                found = _clockCacheV2.TryGet(_hitKeys[rng.Next(_hitKeys.Length)], out _);
        }
        return found;
    }

    public void Dispose()
    {
        _clockCache.Dispose();
        _clockCacheV2.Dispose();
        _concurrentLru.Dispose();
        GC.SuppressFinalize(this);
    }
}
