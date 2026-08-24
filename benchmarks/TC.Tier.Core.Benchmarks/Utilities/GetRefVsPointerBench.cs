using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// 精确对比：AlignedMemoryManager.GetRef&lt;T&gt;（带边界检查）vs GetRefUnsafe&lt;T&gt;（无检查）vs 裸指针。
/// 回答"GetRef 性能会慢吗"。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class GetRefVsPointerBench : IDisposable
{
    private AlignedMemoryManager _mgr = null!;
    private unsafe byte* _rawPtr;

    [Params(4096)]
    public int Size { get; set; }

    private int Offset => 128;

    [GlobalSetup]
    public unsafe void Setup()
    {
        _mgr = new AlignedMemoryManager(Size, 4096);
        _rawPtr = (byte*)_mgr.Ptr;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _mgr?.Dispose();
    }

    [Benchmark(Baseline = true, Description = "GetRef<T> (checked)")]
    public long GetRefChecked()
    {
        ref long r = ref _mgr.GetRef<long>(Offset);
        r = 0x0102030405060708;
        return r;
    }

    [Benchmark(Description = "GetRefUnsafe<T>")]
    public long GetRefUnsafeBench()
    {
        ref long r = ref _mgr.GetRefUnsafe<long>(Offset);
        r = 0x0102030405060708;
        return r;
    }

    [Benchmark(Description = "Raw pointer")]
    public unsafe long RawPointer()
    {
        ref long r = ref *((long*)((byte*)_rawPtr + Offset));   // ★ CS9192：裸指针直算（AddByteOffset 重载歧义绕行）
        r = 0x0102030405060708;
        return r;
    }
}
