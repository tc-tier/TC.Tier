using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

// ============================================================================
// 一.4 内存访问与切片开销 / 二.4 对齐正确性校验
// ============================================================================

/// <summary>
/// 一.4 切片访问开销：GetSpan(off, len) 单次调用 vs GetSpan().Slice(off, len) 链式。
/// 验证封装的切片方法无额外开销（理论上单次构造 Span 比链式少一次）。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class SpanAccessBench : IDisposable
{
    private AlignedMemoryManager _mgr = null!;

    [Params(4096, 65536)]
    public int Size { get; set; }

    // 切片起点/长度：取中段，避免边界特化
    private int Offset => Size / 4;
    private int Length => Size / 2;

    [GlobalSetup]
    public void Setup() => _mgr = new AlignedMemoryManager(Size, 4096);

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _mgr?.Dispose();
    }

    /// <summary>封装方法 GetSpan(offset, length)：单次 Span 构造。</summary>
    [Benchmark(Baseline = true, Description = "GetSpan(off,len)")]
    public byte ViaGetSpanOffsetLength()
    {
        var s = _mgr.GetSpan(Offset, Length);
        s[0] = 42;
        return s[0];
    }

    /// <summary>原链式写法 GetSpan().Slice(off, len)：两次 Span 构造。</summary>
    [Benchmark(Description = "GetSpan().Slice")]
    public byte ViaSlice()
    {
        var s = _mgr.GetSpan().Slice(Offset, Length);
        s[0] = 42;
        return s[0];
    }

    /// <summary>内部热路径 GetSpanUnsafe(off, len)：无边界检查。</summary>
    [Benchmark(Description = "GetSpanUnsafe(off,len)")]
    public byte ViaUnsafe()
    {
        var s = _mgr.GetSpanUnsafe(Offset, Length);
        s[0] = 42;
        return s[0];
    }
}

/// <summary>
/// 一.4 强类型引用读写：GetRef&lt;T&gt; 零拷贝 vs MemoryMarshal.Read&lt;T&gt;。
/// T = long（8 字节），在 buffer 内反复读写。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class GetRefBench : IDisposable
{
    private AlignedMemoryManager _mgr = null!;
    private int _offset;

    [Params(4096)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _mgr = new AlignedMemoryManager(Size, 4096);
        _offset = Size / 2;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _mgr?.Dispose();
    }

    /// <summary>GetRef&lt;long&gt;：返回 ref，直接读写，零拷贝。</summary>
    [Benchmark(Baseline = true, Description = "GetRef<long> r/w")]
    public long ViaGetRef()
    {
        ref long r = ref _mgr.GetRef<long>(_offset);
        r = 0x0102030405060708;
        return r;
    }

    /// <summary>MemoryMarshal.Read&lt;long&gt; / Write：从 Span 读、写，无 ref。</summary>
    [Benchmark(Description = "MemoryMarshal r/w")]
    public long ViaMemoryMarshal()
    {
        var s = _mgr.GetSpan(_offset, 8);
        MemoryMarshal.Write(s, 0x0102030405060708);
        return MemoryMarshal.Read<long>(s);
    }
}
