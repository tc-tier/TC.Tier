using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// 验证：MemoryManager&lt;byte&gt;.Memory 属性是否每次重新构造（开销）vs 缓存。
/// 以及 GetSpan() vs GetSpanUnsafe() 的真实差异。
/// 判断"缓存 Memory"和"内联 GetSpan"是否值得做。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class MemoryPropertyBench : IDisposable
{
    private TestMgr _mgr = null!;
    private Memory<byte> _cached; // 预先缓存，模拟优化后的效果

    [Params(4096)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _mgr = new TestMgr(Size);
        _cached = _mgr.Memory; // 预缓存一次
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _mgr?.Dispose();
    }

    // A. 每次访问 .Memory（当前 AlignedBuffer 热路径模式）
    [Benchmark(Baseline = true, Description = ".Memory (per-call)")]
    public int MemoryPerCall() => _mgr.Memory.Length;

    // B. 缓存后的 Memory 访问（优化目标）
    [Benchmark(Description = "cached Memory")]
    public int CachedMemory() => _cached.Length;

    // C. GetSpan()（带 disposed 检查）
    [Benchmark(Description = "GetSpan() checked")]
    public byte GetSpanChecked()
    {
        var s = _mgr.GetSpan();
        s[0] = 42;
        return s[0];
    }

    // D. GetSpanUnsafe()（无检查）
    [Benchmark(Description = "GetSpanUnsafe")]
    public byte GetSpanUnsafe()
    {
        var s = _mgr.GetSpanUnsafe();
        s[0] = 42;
        return s[0];
    }

    // 模拟 AlignedMemoryManager 的 MemoryManager<byte> 派生
    internal sealed unsafe class TestMgr(int size) : MemoryManager<byte>
    {
        private IntPtr _ptr = (IntPtr)NativeMemory.AlignedAlloc((nuint)size, 4096);
        public int Size { get; } = size;

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_ptr == IntPtr.Zero, "x");
            return new Span<byte>((void*)_ptr, Size);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Span<byte> GetSpanUnsafe() => new((void*)_ptr, Size);
        public override MemoryHandle Pin(int e = 0) => default;
        public override void Unpin() { }
        public void Dispose() => Dispose(true);
        protected override void Dispose(bool disposing)
        {
            var p = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
            if (p != IntPtr.Zero) NativeMemory.AlignedFree((void*)p);
        }
    }
}
