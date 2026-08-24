using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// 隔离实验：精确测量 ConcurrentDictionary.GetOrAdd 在 Rent 热路径中的开销占比。
/// 目的：验证"分桶查找开销大"这一假设，而非靠猜。
///
/// 实验设计：把 Rent(37ns) 拆成几段独立测量
///   A. 纯字典 GetOrAdd（命中已存在 key，无创建）—— 热路径每次都走
///   B. 纯字典 GetOrAdd（首次创建 key + Bucket）—— 冷启动
///   C. 纯字典 TryGetValue —— Return 路径每次都走
///   D. 对比：ThreadLocal.Value 访问（ArrayPool 风格的替代方案）
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class BucketLookupIsolationBench
{
    // 模拟 PinnedBufferPool 内部结构
    private ConcurrentDictionary<int, DummyBucket> _dict = null!;

    // 固定一个热 key（命中路径）
    private const int HotKey = 4096;

    [Params(4096)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dict = new ConcurrentDictionary<int, DummyBucket>();
        // 预填热 key，后续 GetOrAdd 全是命中（不创建）
        _dict.TryAdd(HotKey, new DummyBucket());
    }

    // ── A: GetOrAdd 命中热 key（Rent 路径每次都走）──
    [Benchmark(Baseline = true, Description = "GetOrAdd (hit)")]
    public DummyBucket GetOrAdd_Hit() => _dict.GetOrAdd(HotKey, _ => new DummyBucket());

    // ── C: TryGetValue 命中（Return 路径每次都走）──
    [Benchmark(Description = "TryGetValue (hit)")]
    public bool TryGetValue_Hit() => _dict.TryGetValue(HotKey, out _);

    // ── D: 对比 ArrayPool 风格——直接数组索引（power-of-2 分桶，无字典）
    // 模拟：size → level (log2)，数组直接索引
    private static readonly DummyBucket[] _arrayBuckets = new DummyBucket[32];
    [Benchmark(Description = "Array index (power-of-2)")]
    public DummyBucket ArrayIndex()
    {
        int level = 12; // log2(4096) - 1，模拟 power-of-2 分桶
        return _arrayBuckets[level] ??= new DummyBucket();
    }

    // ── E: 多线程 GetOrAdd 竞争（8 线程同 hit 同一 key，测锁竞争退化）──
    [Benchmark(Description = "GetOrAdd 8-thread contention")]
    public void GetOrAdd_Contention()
    {
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 100_000; i++)
                _dict.GetOrAdd(HotKey, _ => new DummyBucket());
        });
    }

    // 占位类型，模拟 Bucket（非空对象，避免字典内 null 优化）
    public sealed class DummyBucket { public int Count; }
}
