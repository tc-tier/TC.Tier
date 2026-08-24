using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

// ============================================================================
// 第一大类：核心微基准 — 单次租借延迟、GC 分配、清零开销
// 对照基线：无池原生分配（每次 new AlignedMemoryManager + Dispose）、ArrayPool.Shared
// BDN 默认输出 Mean/Error/StdDev + P50/P95/P99/P999；[MemoryDiagnoser] 输出 Gen0/1/2/Allocated。
// ============================================================================

/// <summary>
/// 一.1 单次租借+归还延迟 / 一.3 GC 分配量。
/// 三档对照：池化(byte[]) vs 池化(aligned) vs 无池原生(每次 new+Dispose)。
/// 全部命中池路径，验证稳态零分配。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class RentReturnLatencyBench : IDisposable
{
    private PinnedBufferPool _pool = null!;

    [Params(512, 4096, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _pool = new PinnedBufferPool(maxPerBucket: 64);
        // 预热：保证后续全命中
        _pool.Return(_pool.Rent(Size));
        var a = _pool.RentAligned(Size, 4096);
        _pool.ReturnAligned(a);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _pool?.Dispose();
    }

    /// <summary>池化 byte[] 路径（命中复用）。基线 = 1.00。</summary>
    [Benchmark(Baseline = true, Description = "Pool byte[] (hit)")]
    public byte Pool_ByteArray()
    {
        var buf = _pool.Rent(Size);
        buf[0] = 42;
        _pool.Return(buf);
        return buf[0];
    }

    /// <summary>池化对齐内存路径（命中复用）。DIO 场景的核心路径。</summary>
    [Benchmark(Description = "Pool aligned (hit)")]
    public byte Pool_Aligned()
    {
        var buf = _pool.RentAligned(Size, 4096);
        buf.GetSpan()[0] = 42;
        _pool.ReturnAligned(buf);
        return buf.GetSpan()[0];
    }

    /// <summary>无池基线：每次 new AlignedMemoryManager + Dispose（native alloc/free 开销地板）。</summary>
    [Benchmark(Description = "No-pool new+Dispose")]
    public byte NoPool_Aligned()
    {
        using var m = new AlignedMemoryManager(Size, 4096);
        m.GetSpan()[0] = 42;
        return m.GetSpan()[0];
    }
}

/// <summary>
/// 一.5 内存清零开销对比：zeroed=true vs zeroed=false 下的分配/复用耗时。
/// 量化跳过清零的收益，为业务层清零策略提供数据。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class ZeroingBench : IDisposable
{
    private PinnedBufferPool _pool = null!;

    [Params(4096, 65536)]
    public int Size { get; set; }

    [Params(false, true)]
    public bool Zero { get; set; }

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

    /// <summary>池化复用 + 可选清零（Rent 的 zeroMemory 参数）。</summary>
    [Benchmark(Description = "Pool Rent+Return")]
    public byte PoolWithZeroFlag()
    {
        var buf = _pool.Rent(Size, zeroMemory: Zero);
        _pool.Return(buf);
        return buf[0];
    }
}
