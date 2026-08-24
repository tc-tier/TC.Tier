using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

// ============================================================================
// 第四大类：边界与极端场景 — 池满归还、冷启动、大内存块
// ============================================================================

/// <summary>
/// 四.1 池满时归还性能：桶内数量达 maxPerBucket 上限后持续归还的耗时与内存表现。
/// 验证软限制逻辑：超额归还应被正确丢弃（byte[]）/释放（aligned），无性能退化或泄漏。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class PoolFullReturnBench : IDisposable
{
    private PinnedBufferPool _pool = null!;
    private AlignedMemoryManager[] _held = null!;

    [Params(4096)]
    public int Size { get; set; }

    // maxPerBucket 故意设小，便于撑满
    [Params(8)]
    public int MaxPerBucket { get; set; }

    // 一次 Benchmark 调用归还的数量 = MaxPerBucket（全部超额被丢弃）
    [Params(8)]
    public int OverflowCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _pool = new PinnedBufferPool(maxPerBucket: MaxPerBucket);
        // 先把池撑满
        for (int i = 0; i < MaxPerBucket; i++)
            _pool.Return(_pool.Rent(Size));
        // 再租借一批持有，供 Benchmark 体归还（这批归还时桶已满 → 全丢弃）
        _held = new AlignedMemoryManager[OverflowCount];
        for (int i = 0; i < OverflowCount; i++)
            _held[i] = _pool.RentAligned(Size, 4096);
    }

    [IterationCleanup]
    public void IterationCleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _pool?.Dispose();
    }

    /// <summary>池满后归还 aligned：超额部分应被 Dispose 释放，无泄漏。</summary>
    [Benchmark(Description = "Return aligned (bucket full)")]
    public int ReturnAlignedWhenFull()
    {
        for (int i = 0; i < OverflowCount; i++)
            _pool.ReturnAligned(_held[i]);
        return OverflowCount;
    }
}

/// <summary>
/// 四.2 冷启动 / 池空分配性能：池为空时批量租借耗时，评估 PreAllocateAligned 的必要性。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
public class ColdStartBench : IDisposable
{
    private PinnedBufferPool _pool = null!;
    private byte[][] _rented = null!;

    [Params(4096, 65536)]
    public int Size { get; set; }

    [Params(64)]
    public int Batch { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _pool = new PinnedBufferPool(maxPerBucket: Batch);
        _rented = new byte[Batch][];
    }

    [IterationCleanup]
    public void IterationCleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _pool?.Dispose();
    }

    /// <summary>冷池批量租借（全部 miss → 全新分配）。</summary>
    [Benchmark(Description = "Cold rent N (all miss)")]
    public int ColdRentBatch()
    {
        for (int i = 0; i < Batch; i++)
            _rented[i] = _pool.Rent(Size);
        return Batch;
    }
}

/// <summary>
/// 四.3 大内存块性能表现：4KB/16KB/64KB/1MB 不同大小租借、归还、访问性能。
/// 大内存原生分配开销更高，池化收益通常更显著。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
public class LargeBlockBench : IDisposable
{
    private PinnedBufferPool _pool = null!;

    // 1MB = 1024*1024
    [Params(4096, 16384, 65536, 1048576)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _pool = new PinnedBufferPool(maxPerBucket: 16);
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

    /// <summary>池化对齐大块 Rent+Return（命中）。</summary>
    [Benchmark(Baseline = true, Description = "Pool aligned (hit)")]
    public byte PoolAligned()
    {
        var buf = _pool.RentAligned(Size, 4096);
        buf.GetSpan()[0] = 42;
        _pool.ReturnAligned(buf);
        return buf.GetSpan()[0];
    }

    /// <summary>无池基线：每次 new+Dispose 大块。</summary>
    [Benchmark(Description = "No-pool new+Dispose")]
    public byte NoPool()
    {
        using var m = new AlignedMemoryManager(Size, 4096);
        m.GetSpan()[0] = 42;
        return m.GetSpan()[0];
    }
}
