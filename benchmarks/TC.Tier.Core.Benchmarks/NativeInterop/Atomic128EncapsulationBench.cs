using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;
using TC.Tier.Core.NativeInterop;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;

namespace TC.Tier.Core.Benchmarks.NativeInterop;

/// <summary>
/// ★ Atomic128&lt;T&gt; 封装层开销——<b>单 op 纳秒级</b>（对齐 Atomic128Bench 测法）。
/// <para>每个 [Benchmark] 方法体 = <b>1 次 CAS</b>（无循环、无 Task、无争用——值单调递增 CAS 恒成功），
///   BenchmarkDotNet 自动聚合到 ns/op，测的是纯硬件 CAS 延迟（cmpxchg16b ~5-8ns 级）。</para>
/// <para>★ 回答"封装层有没有在硬件原子级加开销"：封装 CAS 应 ≈ 直接 CAS（AggressiveInlining 消除
///   CasEnabled/IsDisposed/GetRefUnsafe）。对照 64b CAS（硬件基准）+ lock（软件锁基准）。</para>
/// <para>⚠️ 这是<b>无争用单 op 延迟</b>，不是多线程吞吐（后者另见 AtomicContentionBench）。
///   CAS 在高争用下不如 lock 是另一维度，与本延迟测量无关。</para>
/// <para>运行: dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --filter '*Atomic128Encapsulation*'</para>
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser(false)]
public class Atomic128EncapsulationBench : IDisposable
{
    // ── 64-bit 基准（Interlocked，硬件 1-2ns 级）──
    private long _volatile64;

    // ── 直接路径：AlignedMemoryManager 对齐背板 + 裸 NativeAtomic128 ──
    private AlignedMemoryManager? _directMem;
    private ref NativeInt128 Direct128 => ref _directMem!.GetRefUnsafe<NativeInt128>(0);

    // ── 封装路径：Atomic128<NativeInt128>（重构后 TailWatermarkSlot 等价路径）──
    private Atomic128<NativeInt128>? _encap;

    // ── lock 对照 ──
    private NativeInt128 _lockedField;
    private readonly object _lock = new();

    [GlobalSetup]
    public void Setup()
    {
        _directMem = new AlignedMemoryManager(64, 16, zeroed: true);
        _encap = new Atomic128<NativeInt128>();
        _volatile64 = 0;
        _lockedField = new NativeInt128(0, 0);
    }

    [GlobalCleanup]
    public void Cleanup() => _directMem?.Dispose();

    // ── 64b CAS 基准（Interlocked.CompareExchange，硬件最快）──
    [Benchmark(Description = "64b CAS 基准 (Interlocked)", Baseline = true)]
    public long Cas64()
    {
        var v = Volatile.Read(ref _volatile64);
        return Interlocked.CompareExchange(ref _volatile64, v + 1, v);
    }

    // ── 直接 NativeAtomic128（重构前等价路径，单次裸调）──
    [Benchmark(Description = "直接 NativeAtomic128")]
    public bool DirectCas()
    {
        var old = Direct128;
        return NativeAtomic128.CompareExchange(ref Direct128, old, new NativeInt128(old.Lo + 1, old.Hi));
    }

    // ── Atomic128<T> 封装（重构后路径，单次）──
    [Benchmark(Description = "Atomic128<T> 封装")]
    public bool EncapCas()
    {
        var old = _encap!.Read();
        return _encap.TryCompareExchange(old, new NativeInt128(old.Lo + 1, old.Hi));
    }

    // ── Atomic128<T> Unsafe 快路径（跳过 CasEnabled/IsDisposed，目标 ≈ 直接 11ns）──
    [Benchmark(Description = "Atomic128<T> Unsafe 快路径")]
    public bool EncapUnsafeCas()
    {
        var old = _encap!.ReadUnsafe();
        return _encap.TryCompareExchangeUnsafe(old, new NativeInt128(old.Lo + 1, old.Hi));
    }

    // ── lock 对照（Monitor，无争用单次）──
    [Benchmark(Description = "lock (Monitor)")]
    public bool LockCas()
    {
        lock (_lock)
        {
            var old = _lockedField;
            _lockedField = new NativeInt128(old.Lo + 1, old.Hi);
            return true;
        }
    }

    public void Dispose()
    {
        _directMem?.Dispose();
        GC.SuppressFinalize(this);
    }
}
