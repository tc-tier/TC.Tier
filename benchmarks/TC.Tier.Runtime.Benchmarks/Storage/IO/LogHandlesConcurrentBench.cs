using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

// ============================================================================
// _logHandles 并发伸缩性证伪 benchmark
//
// 背景：ManagedLocalStorageDevice._logHandles / _readHandleCache 用 ConcurrentDictionary。
// PinnedBufferPool 报告记录 ConcurrentDictionary.GetOrAdd 退化 248×（单线程 3.5ns → 8 线程 871ns）。
// 但 _logHandles 的热路径是 TryGetValue（lock-free 读），GetOrAdd 只在首次开段时发生。
// 本 benchmark 用数据回答：ConcurrentDictionary 在 _logHandles 的真实访问模式下是否够用。
//
// 决策门槛（参照 PinnedBufferPool 报告的伸缩比目标 ≥0.7 为合格）：
//   - TryGetValue 8 线程伸缩比 ≥ 0.7 → 不换，ConcurrentDictionary 够用
//   - 伸缩比 < 0.5 → 显著退化，自研替换（参照 PinnedBufferPool thread-local + 全局栈范式）
// ============================================================================

/// <summary>
/// _logHandles 热路径证伪：ConcurrentDictionary.TryGetValue（命中）的并发伸缩性。
/// 这是对应 GetOrAddHandle 的快路径（line 182），每次 IO 都走。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
public class LogHandlesTryGetBench : IDisposable
{
    private ConcurrentDictionary<int, object> _dict = null!;
    private ManualResetEventSlim _startGate = null!;
    private CountdownEvent _readyGate = null!;

    [Params(4, 64, 256)]
    public int SegmentCount { get; set; }

    [Params(1, 2, 4, 8, 12)]
    public int DegreeOfParallelism { get; set; }

    [Params(5_000_000)]
    public int IterationsPerThread { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        // 预建 SegmentCount 个段，TryGetValue 全命中（真实生产稳态）
        _dict = new ConcurrentDictionary<int, object>();
        for (int i = 0; i < SegmentCount; i++)
            _dict[i] = new object();
        _startGate = new ManualResetEventSlim(false);
        _readyGate = new CountdownEvent(DegreeOfParallelism);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _startGate.Dispose();
        _readyGate.Dispose();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _startGate?.Dispose();
        _readyGate?.Dispose();
    }

    [Benchmark(Description = "CD.TryGetValue hit")]
    public void Run()
    {
        _startGate.Reset();
        _readyGate.Reset(_readyGate.InitialCount);

        var threads = new Thread[DegreeOfParallelism];
        for (int t = 0; t < DegreeOfParallelism; t++)
        {
            int tid = t;
            threads[tid] = new Thread(() =>
            {
                // 预热：触发本线程对字典的 cache warm-up
                for (int i = 0; i < 16; i++)
                    _dict.TryGetValue(i % SegmentCount, out _);

                _readyGate.Signal();
                _startGate.Wait();

                // 计时循环：稳态全命中 TryGetValue
                // 段号取模轮询，模拟真实多段 IO 访问
                for (int i = 0; i < IterationsPerThread; i++)
                {
                    _dict.TryGetValue(i % SegmentCount, out var h);
                }
            }) { IsBackground = true };
            threads[tid].Start();
        }

        _readyGate.Wait();
        _startGate.Set();
        foreach (var th in threads) th.Join();
    }
}

/// <summary>
/// _logHandles 慢路径证伪：ConcurrentDictionary.GetOrAdd（首次开段）的并发伸缩性。
/// 对应 GetOrAddHandle 的慢路径（line 183），每个段首次 open 时发生。
/// 这里所有线程对同一段并发 GetOrAdd（最坏情况：首次同时 open 同段）。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
public class LogHandlesGetOrAddBench : IDisposable
{
    private ConcurrentDictionary<int, object> _dict = null!;
    private ManualResetEventSlim _startGate = null!;
    private CountdownEvent _readyGate = null!;

    // 段数 = IterationsPerThread（每次迭代都是新段，强制 GetOrAdd 真正写入）
    [Params(10000)]
    public int TotalSegments { get; set; }

    [Params(1, 2, 4, 8, 12)]
    public int DegreeOfParallelism { get; set; }

    private int _segmentsPerThread;

    [IterationSetup]
    public void IterationSetup()
    {
        _dict = new ConcurrentDictionary<int, object>();
        _startGate = new ManualResetEventSlim(false);
        _readyGate = new CountdownEvent(DegreeOfParallelism);
        _segmentsPerThread = TotalSegments / DegreeOfParallelism;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _startGate.Dispose();
        _readyGate.Dispose();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _startGate?.Dispose();
        _readyGate?.Dispose();
    }

    [Benchmark(Description = "CD.GetOrAdd (new seg)")]
    public void Run()
    {
        _startGate.Reset();
        _readyGate.Reset(_readyGate.InitialCount);

        var threads = new Thread[DegreeOfParallelism];
        for (int t = 0; t < DegreeOfParallelism; t++)
        {
            int tid = t;
            threads[tid] = new Thread(() =>
            {
                _readyGate.Signal();
                _startGate.Wait();

                // 计时：每线程 open 不重叠的段区间（强制工厂执行 + 字典写入）
                // 段号 = tid * segmentsPerThread + i（避免段号冲突）
                int baseSeg = tid * _segmentsPerThread;
                for (int i = 0; i < _segmentsPerThread; i++)
                {
                    _dict.GetOrAdd(baseSeg + i, _ => new object());
                }
            }) { IsBackground = true };
            threads[tid].Start();
        }

        _readyGate.Wait();
        _startGate.Set();
        foreach (var th in threads) th.Join();
    }
}
