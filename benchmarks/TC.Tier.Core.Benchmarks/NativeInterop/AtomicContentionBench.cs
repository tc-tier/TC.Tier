using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.NativeInterop;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;
using AlignedMemoryManager = TC.Tier.Core.Primitives.AlignedMemoryManager;

namespace TC.Tier.Core.Benchmarks.NativeInterop;

/// <summary>
/// ★ 128-bit CAS 多线程争用基准——回答"native cmpxchg16b 相比 lock 的真实价值"。
/// <para>单线程下 native 和 lock 持平（P/Invoke stub 吃掉硬件优势）。
/// 但<b>多线程争用</b>下：cmpxchg16b 走 MESI 缓存一致性（并行退化平缓），
/// lock 走 monitor 串行化（核数越多越慢）。这是 native C 库存在的核心理由。</para>
/// <para>运行: dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks/ -- --filter "*AtomicContention*"</para>
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser(false)]
public class AtomicContentionBench:IDisposable
{
    [Params(1, 2, 4, 8)]
    public int Threads { get; set; }

    private const int OpsPerThread = 200_000;

    // 16B 对齐的 CAS 目标（native 内存）
    private AlignedMemoryManager? _alignedNative;
    private IntPtr _nativePtr;
    private unsafe ref NativeInt128 NativeTarget => ref Unsafe.AsRef<NativeInt128>(_nativePtr.ToPointer());

    // 16B 对齐的 lock 目标
    private AlignedMemoryManager? _alignedLock;
    private IntPtr _lockPtr;
    private unsafe ref NativeInt128 LockTarget => ref Unsafe.AsRef<NativeInt128>(_lockPtr.ToPointer());
    private readonly object _lockObj = new();

    [GlobalSetup]
    public unsafe void Setup()
    {
        _alignedNative = new AlignedMemoryManager(64, 16, zeroed: true);
        _nativePtr = (IntPtr)_alignedNative.Ptr;
        _alignedLock = new AlignedMemoryManager(64, 16, zeroed: true);
        _lockPtr = (IntPtr)_alignedLock.Ptr;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _alignedNative?.Dispose();
        _alignedLock?.Dispose();
    }

    /// <summary>native cmpxchg16b 多线程争用（CAS 循环推进计数器）。</summary>
    [Benchmark(Description = "native cmpxchg16b 争用")]
    public ulong NativeContention()
    {
        NativeTarget = new NativeInt128(0, 0);
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < OpsPerThread; i++)
                {
                    NativeInt128 cur;
                    do
                    {
                        cur = NativeTarget;
                    } while (!NativeAtomic128.CompareExchange(
                        ref NativeTarget, cur, new NativeInt128(cur.Lo + 1, cur.Hi)));
                }
            });
        }
        Task.WaitAll(tasks);
        return NativeTarget.Lo;
    }

    /// <summary>lock + monitor 多线程争用（对照）。</summary>
    [Benchmark(Description = "lock 争用")]
    public ulong LockContention()
    {
        LockTarget = new NativeInt128(0, 0);
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < OpsPerThread; i++)
                {
                    lock (_lockObj)
                    {
                        LockTarget = new NativeInt128(LockTarget.Lo + 1, LockTarget.Hi);
                    }
                }
            });
        }
        Task.WaitAll(tasks);
        return LockTarget.Lo;
    }

    public void Dispose()
    {
        _alignedNative?.Dispose();
        _alignedLock?.Dispose();
        GC.SuppressFinalize(this);
    }
}
