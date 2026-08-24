using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// 隔离对比：原版 AlignedMemoryManager（3 字段 + CAS 状态机）vs 指针位复用版（ptrWithFlags）。
/// 量化字段压缩的真实收益，判断是否值得承担 CAS 丢失/TOCTOU 风险。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class AlignedMemoryFieldLayoutBench : IDisposable
{
    private OriginalImpl _orig = null!;
    private PackedImpl _packed = null!;

    [Params(4096)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _orig = new OriginalImpl(Size, 4096);
        _packed = new PackedImpl(Size, 4096);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _orig?.Dispose();
        _packed?.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Original (CAS state)")]
    public byte Original_GetSpan()
    {
        var s = _orig.GetSpan();
        s[0] = 42;
        return s[0];
    }

    [Benchmark(Description = "Packed (ptrWithFlags)")]
    public byte Packed_GetSpan()
    {
        var s = _packed.GetSpan();
        s[0] = 42;
        return s[0];
    }

    // ── 原版结构：IntPtr _ptr + int _rentState（CAS 状态机）──
    internal sealed unsafe class OriginalImpl : MemoryManager<byte>
    {
        private IntPtr _ptr;
        private int _rentState;
        public int Size { get; }
        public OriginalImpl(int size, int alignment)
        {
            Size = size;
            _ptr = (IntPtr)NativeMemory.AlignedAlloc((nuint)size, (nuint)alignment);
        }
        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_ptr == IntPtr.Zero, "x");
            return new Span<byte>((void*)_ptr, Size);
        }
        public bool TryMarkRented() => Interlocked.CompareExchange(ref _rentState, 1, 0) == 0;
        public bool TryMarkReturned() => Interlocked.CompareExchange(ref _rentState, 0, 1) == 1;
        public void Dispose() => Dispose(true);
        protected override void Dispose(bool disposing)
        {
            var ptr = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
            if (ptr == IntPtr.Zero) return;
            NativeMemory.AlignedFree((void*)ptr);
        }
        public override MemoryHandle Pin(int e = 0) => default;
        public override void Unpin() { }
    }

    // ── 压缩版：IntPtr _ptrWithFlags（低2位存状态）──
    internal sealed unsafe class PackedImpl : MemoryManager<byte>
    {
        private const long RENTED_MASK = 2L;
        private const long FLAGS_MASK = 3L;
        private IntPtr _ptrWithFlags;
        private readonly int _size;
        public int Size => _size;
        public PackedImpl(int size, int alignment)
        {
            _size = size;
            void* ptr = NativeMemory.AlignedAlloc((nuint)size, (nuint)alignment);
            _ptrWithFlags = (IntPtr)ptr;
        }
        private void* UnsafePtr
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (void*)(_ptrWithFlags.ToInt64() & ~FLAGS_MASK);
        }
        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_ptrWithFlags == IntPtr.Zero, "x");
            return new Span<byte>(UnsafePtr, _size);
        }
        public void Dispose() => Dispose(true);
        protected override void Dispose(bool disposing)
        {
            IntPtr raw = Interlocked.Exchange(ref _ptrWithFlags, IntPtr.Zero);
            if (raw == IntPtr.Zero) return;
            NativeMemory.AlignedFree((void*)(raw.ToInt64() & ~FLAGS_MASK));
        }
        public override MemoryHandle Pin(int e = 0) => default;
        public override void Unpin() { }
    }
}
