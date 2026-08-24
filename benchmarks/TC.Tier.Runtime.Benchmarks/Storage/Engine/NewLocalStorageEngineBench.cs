using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Runtime.Benchmarks.Storage.Engine;

/// <summary>
/// 新 StorageEngineBase (LogicalAddress API) 吞吐基准——Append/Write/Read。
/// <para>与 ManagedLocalStorageEngineBench 对照验证。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class NewLocalStorageEngineBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _device = null!;

    [Params(512, 4096)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        var options = new StorageEngineOptions("bench-dev", segmentGrowthLimit: 16 * 1024 * 1024).WithPreallocateFile(false);
        _device = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();

    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _device?.Dispose();
        _vol?.Dispose();
    }

    [Benchmark(Description = "Append (sync)")]
    public LogicalAddress Append()
    {
        byte[] src = new byte[PayloadSize];
        return _device.Append(src);
    }

    [Benchmark(Description = "Append+Read (roundtrip)")]
    public int AppendAndRead()
    {
        byte[] src = new byte[PayloadSize];
        new Random(42).NextBytes(src);
        var addr = _device.Append(src);
        byte[] dst = new byte[PayloadSize];
        return _device.Read(addr, dst);
    }

    [Benchmark(Description = "Write+Read (overwrite)")]
    public int WriteAndRead()
    {
        byte[] src = new byte[PayloadSize];
        new Random(42).NextBytes(src);
        // Write 契约：目标 ≤ CommittedTail——先 Allocate 占位（推 Committed，稀疏）再覆写
        var (start, _) = _device.Allocate(PayloadSize);
        _device.Write(start, src);
        byte[] dst = new byte[PayloadSize];
        return _device.Read(start, dst);
    }
}
