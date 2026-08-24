using System.IO.Hashing;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// S1a/S1b 验证：NativeMemoryExtensions + ValueTaskIOExtensions 性能微基准。
/// 用于验证 AsMemory/AsSpan 扩展方法零开销，以及 DiscardInt 快速路径。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class NativeMemoryExtensionsBench
{
    private IntPtr _ptr;
    private byte[] _managed;

    [Params(512, 4096, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _ptr = Marshal.AllocHGlobal(Size);
        _managed = new byte[Size];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Marshal.FreeHGlobal(_ptr);
    }

    /// <summary>AsSpan 扩展方法（同步路径）——零分配 Span 包装。</summary>
    [Benchmark(Description = "AsSpan (extension)")]
    public int AsSpanBench()
    {
        var span = _ptr.AsSpan(Size);
        return span.Length;
    }

    /// <summary>AsMemory 扩展方法（异步路径）——轻量 UnmanagedMemoryManager。</summary>
    [Benchmark(Description = "AsMemory (extension)")]
    public int AsMemoryBench()
    {
        var mem = _ptr.AsMemory(Size);
        return mem.Length;
    }

    /// <summary>AsMemory + .Span 获取（旧模式的替代）——对比 AsSpan 直取。</summary>
    [Benchmark(Description = "AsMemory().Span")]
    public int AsMemoryThenSpanBench()
    {
        var span = _ptr.AsMemory(Size).Span;
        return span.Length;
    }

    /// <summary>Span 写入吞吐（native memory via AsSpan）。</summary>
    [Benchmark(Description = "Write via AsSpan")]
    public byte WriteViaAsSpan()
    {
        var span = _ptr.AsSpan(Size);
        span[0] = 42;
        return span[0];
    }

    /// <summary>Span 写入吞吐（managed array baseline 对照）。</summary>
    [Benchmark(Description = "Write via managed array")]
    public byte WriteViaManaged()
    {
        _managed[0] = 42;
        return _managed[0];
    }
}

/// <summary>
/// S1b 验证：DiscardInt 快速路径 vs 慢路径性能。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class DiscardIntBench
{
    private ValueTask<int> _syncCompleted;

    [GlobalSetup]
    public void Setup()
    {
        _syncCompleted = new ValueTask<int>(42);
    }

    /// <summary>同步完成的 ValueTask DiscardInt 快速路径。</summary>
    [Benchmark(Description = "DiscardInt (sync fast path)")]
    public ValueTask DiscardIntSync()
    {
        return _syncCompleted.DiscardInt();
    }
}

/// <summary>
/// CRC 计算开销微基准 — 量化 StreamBlockBlob 的 CRC64 per-chunk 开销。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class CrcMicroBench
{
    private byte[] _data64k = null!;
    private byte[] _data512k = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data64k = GC.AllocateArray<byte>(65536, pinned: true);
        _data512k = GC.AllocateArray<byte>(524288, pinned: true);
        new Random(42).NextBytes(_data64k);
        new Random(42).NextBytes(_data512k);
    }

    [Benchmark(Description = "CRC64 64K")]
    public void Crc64_64K()
    {
        var c = new Crc64();
        c.Append(_data64k);
        c.GetCurrentHash();
    }

    [Benchmark(Description = "CRC64 512K")]
    public void Crc64_512K()
    {
        var c = new Crc64();
        c.Append(_data512k);
        c.GetCurrentHash();
    }

    [Benchmark(Description = "CRC32 64K")]
    public void Crc32_64K()
    {
        var c = new Crc32();
        c.Append(_data64k);
        c.GetCurrentHash();
    }
}
