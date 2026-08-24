using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.NativeInterop;
// AlignedMemoryManager 虽在 System.Buffers 命名空间，但属于 TC.Tier 内部类（InternalsVisibleTo）
using AlignedMemoryManager = TC.Tier.Core.Primitives.AlignedMemoryManager;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;

namespace TC.Tier.Core.Benchmarks.NativeInterop;

/// <summary>
/// ★ 128-bit CAS raw 性能基准——三档对齐场景。
/// <para>cmpxchg16b 要求 16B 对齐：非对齐走分片锁兜底（旧版是全局锁）。
/// 本基准对比三种存储位置，暴露对齐对 native 快路径的决定性影响：</para>
/// <para>1. <b>Aligned（native 内存）</b>：AlignedMemoryManager(16,16) 分配，对齐检查恒真，走 cmpxchg16b 硬件 CAS。
///    这是生产水位字段应处的状态（走 AlignedMemoryManager 对齐分配）。</para>
/// <para>2. <b>Unaligned（托管堆字段）</b>：普通 struct 字段，~50% 概率非对齐，走分片锁兜底。
///    这暴露了"把 128b CAS 字段放托管堆"的代价——非对齐兜底。</para>
/// <para>3. <b>64b CAS</b>：Interlocked.CompareExchange(ref long) 硬件基准。</para>
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser(false)]
public class Atomic128Bench:IDisposable
{
    // ── 64-bit 基准 ──
    private long _volatile64;

    // ── 128-bit aligned（native 内存，走 cmpxchg16b 快路径）──
    private AlignedMemoryManager? _aligned;
    private ref NativeInt128 Aligned128 => ref _aligned!.GetRefUnsafe<NativeInt128>(0);

    // ── 128-bit unaligned（托管堆字段，~50% 走分片锁兜底）──
    private NativeInt128 _heapVal128;
    private readonly object _lock = new();

    [GlobalSetup]
    public void Setup()
    {
        _aligned = new AlignedMemoryManager(64, 16, zeroed: true);
        _heapVal128 = new NativeInt128(0, 0);
        _volatile64 = 0;
    }

    [GlobalCleanup]
    public void Cleanup() => _aligned?.Dispose();

    // ── 64-bit CAS 基准 ──
    [Benchmark(Description = "64b CAS (Interlocked)")]
    public long Cas64()
    {
        long v = Volatile.Read(ref _volatile64);
        long n = v + 1;
        return Interlocked.CompareExchange(ref _volatile64, n, v);
    }

    // ── 128b CAS 对齐：走 cmpxchg16b 硬件快路径（生产水位字段目标态）──
    [Benchmark(Description = "128b CAS (native, aligned)")]
    public bool Cas128Aligned()
    {
        var old = Aligned128;
        var next = new NativeInt128(old.Lo + 1, old.Hi);
        return NativeAtomic128.CompareExchange(ref Aligned128, old, next);
    }

    // ── 128b CAS 非对齐：托管堆字段，~50% 走分片锁兜底（反面教材）──
    [Benchmark(Description = "128b CAS (native, heap unaligned)")]
    public bool Cas128Heap()
    {
        var old = _heapVal128;
        var next = new NativeInt128(old.Lo + 1, old.Hi);
        return NativeAtomic128.CompareExchange(ref _heapVal128, old, next);
    }

    // ── 128b 软件锁基准（对照）──
    [Benchmark(Description = "128b sw (lock)")]
    public bool Cas128Soft()
    {
        lock (_lock)
        {
            var old = _heapVal128;
            _heapVal128 = new NativeInt128(old.Lo + 1, old.Hi);
            return true;
        }
    }

    public void Dispose()
    {
        ((IDisposable)_aligned!).Dispose();
        GC.SuppressFinalize(this);
    }
}
