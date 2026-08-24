using System.Buffers;
using System.Diagnostics;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// 瓶颈隔离实验：逐步剥离可能的竞争源，定位 ScalabilityBench 剩余瓶颈。
/// 6 核 12 线程机器（i5-12400）。
///
/// 对照组设计（相同 N 线程 × 1M 次 rent-return，仅变量不同）：
///   A. 完整 PinnedBufferPool（基准）
///   B. 纯 thread-local 模拟：每线程一个独立 Stack<byte[]>（永不跨线程，零共享）
///      —— 剥离 Global 回退栈 + 计数器 + 分桶查找。若 B 近线性而 A 不线性，瓶颈在池的共享结构。
///   C. 纯内存访问：每线程对固定 byte[] 做 [0]=42 往返（无池、无分配）
///      —— 剥离一切池逻辑。若 C 也不线性，瓶颈在内存子系统（cache/带宽）而非池。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
public class ScalabilityBottleneckBench : IDisposable
{
    [Params(1, 4, 6, 12)]
    public int N { get; set; }

    private PinnedBufferPool _pool = null!;
    private byte[][] _isolatedBufs = null!; // 每线程预分配的固定 buffer（C 用）
    private Stack<byte[]>[] _threadStacks = null!; // 每线程独立栈（B 用）

    // 固定工作量
    private const int Iter = 1_000_000;
    private const int Size = 4096;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new PinnedBufferPool(maxPerBucket: 256);
        _isolatedBufs = new byte[N][];
        _threadStacks = new Stack<byte[]>[N];
        for (int i = 0; i < N; i++)
        {
            _isolatedBufs[i] = GC.AllocateUninitializedArray<byte>(Size, pinned: true);
            _threadStacks[i] = new Stack<byte[]>();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _pool?.Dispose();
    }

    // A. 完整池（基准）
    [Benchmark(Baseline = true, Description = "A. Full pool")]
    public void FullPool()
    {
        RunWorkers(tid =>
        {
            var pool = _pool;
            // 预热本线程 thread-local
            pool.Return(pool.Rent(Size));
            for (int i = 0; i < Iter; i++)
            {
                var b = pool.Rent(Size);
                b[0] = 42;
                pool.Return(b);
            }
        });
    }

    // B. 纯 thread-local（每线程独立 Stack，零共享）
    [Benchmark(Description = "B. Pure thread-local stack")]
    public void PureThreadLocal()
    {
        RunWorkers(tid =>
        {
            var stack = new Stack<byte[]>(); // 完全私有，无任何跨线程
            // 预填一个 buffer
            stack.Push(GC.AllocateUninitializedArray<byte>(Size, pinned: true));
            for (int i = 0; i < Iter; i++)
            {
                var b = stack.Pop();
                b[0] = 42;
                stack.Push(b);
            }
        });
    }

    // C. 纯内存访问（无池、无栈、无分配，固定 buffer）
    [Benchmark(Description = "C. Raw memory access")]
    public void RawMemory()
    {
        RunWorkers(tid =>
        {
            var buf = _isolatedBufs[tid]; // 预分配固定 buffer
            for (int i = 0; i < Iter; i++)
            {
                buf[0] = 42;
            }
        });
    }

    // 手动 N 线程 + barrier 同步起跑
    private void RunWorkers(Action<int> body)
    {
        var ready = new CountdownEvent(N);
        var go = new ManualResetEventSlim(false);
        var threads = new Thread[N];
        for (int t = 0; t < N; t++)
        {
            int tid = t;
            threads[t] = new Thread(() =>
            {
                ready.Signal();
                go.Wait();
                body(tid);
            }) { IsBackground = true };
            threads[t].Start();
        }
        ready.Wait();
        var sw = Stopwatch.StartNew();
        go.Set();
        foreach (var th in threads) th.Join();
        sw.Stop();
        // BDN 会把本方法耗时记为 Mean；sw 不直接用，仅保证 join 同步
        GC.KeepAlive(sw);
    }
}
