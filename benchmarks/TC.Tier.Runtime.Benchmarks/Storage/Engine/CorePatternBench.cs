using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Runtime.Benchmarks.Storage.Engine;

/// <summary>
/// 核心使用模式基准——预分配 + 复写（页缓冲模型：Allocate 大区一次，址上 Write 复写）。
/// <para>★ 定位（用户裁定）：这是引擎的<b>推荐核心模式</b>——地址分配无 lease 协议（纯 CAS 推水位），
///   复写才付 lease 协议成本；Append 只是降低水位线管理复杂度的 WAL 顺序写便利路径。</para>
/// <para>维度：Payload(64B 记录 / 4KB 页 / 64KB 大块) × {一次性 Allocate、稳态复写、4T 并行复写、Append 对照}。</para>
/// <para>★ 稳态复写合法直接开跑：Allocate 占位即 Committed+sparse（可读零、可覆写）——无需先填。</para>
/// <para>运行：dotnet run -c Release -- --filter '*CorePattern*'（TC_BENCH_FS_SPEC 切介质）</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class CorePatternBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _engine = null!;
    private byte[] _payload = Array.Empty<byte>();

    /// <summary>复写粒度：64B 记录 / 4KB 页 / 64KB 大块。</summary>
    [Params(64, 4096, 65536)]
    public int PayloadSize { get; set; }

    /// <summary>每 invoke 操作数。</summary>
    [Params(50_000)]
    public int OperationsPerInvoke { get; set; }

    /// <summary>预分配区域大小（一次 Allocate 的量）。</summary>
    private const long RegionBytes = 256L * 1024 * 1024;

    private long _entries;
    private long _cursor;
    private LogicalAddress _base;

    [GlobalSetup]
    public void Setup()
    {
        _vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:）
        var options = new StorageEngineOptions("corepat", segmentGrowthLimit: 64L * 1024 * 1024)
            .WithPreallocateFile(false);
        // 关闭 CPU 采样节流：秒级满载微基准会被三档节流 park（设计内让路），测协议成本须排除
        options = options.WithOptimization(options.Optimization with { SampleInterval = TimeSpan.FromHours(1) });
        _engine = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();


        _payload = new byte[PayloadSize];
        for (int i = 0; i < PayloadSize; i++) _payload[i] = (byte)(i & 0xFF);

        Reallocate();
    }

    /// <summary>重新预分配一个干净区域（一次性 Allocate 基准 + 每迭代重置游标用）。</summary>
    private void Reallocate()
    {
        _base = _engine.Allocate(RegionBytes).Start;
        _entries = RegionBytes / PayloadSize;
        _cursor = 0;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _engine?.Dispose();
        _vol?.Dispose();
    }

    /// <summary>一次性 Allocate 256MB——页缓冲模型的"页预留"成本（每区域一次，摊到字节近零）。</summary>
    [Benchmark]
    public void Allocate_Region_Once() => Reallocate();

    /// <summary>稳态复写——预分配区内顺序游标覆写（跨段推进）。核心模式的每 op 成本。</summary>
    [Benchmark]
    public long Write_PreAllocated()
    {
        var last = 0L;
        var span = _payload.AsSpan();
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            var idx = _cursor = (_cursor + 1) % _entries;
            last = _engine.Write(_engine.CalculationAddress(_base, idx * PayloadSize), span).Offset;
        }
        return last;
    }

    /// <summary>并行复写——4 线程各写本区域不相交四分之一（lease 区间所有权应允许并行）。</summary>
    [Benchmark]
    public long Write_PreAllocated_4T()
    {
        const int Threads = 4;
        long quarter = _entries / Threads;
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            var lo = (long)t * quarter;
            tasks[t] = Task.Run(() =>
            {
                var span = _payload.AsSpan();
                for (int i = 0; i < OperationsPerInvoke / Threads; i++)
                {
                    var idx = lo + (i % quarter);
                    _engine.Write(_engine.CalculationAddress(_base, idx * PayloadSize), span);
                }
            });
        }
        Task.WaitAll(tasks);
        return _engine.CommittedTail.Offset;
    }

    /// <summary>Append 对照——同 payload 的 WAL 顺序写路径（含 lease 协议 + 双尾水位管理）。</summary>
    [Benchmark]
    public long Append_Compare()
    {
        var last = default(TC.Tier.Contracts.Storage.LogicalAddress);
        var span = _payload.AsSpan();
        for (int i = 0; i < OperationsPerInvoke / 10; i++)   // Append 推水位占空间，减量防区溢出
            last = _engine.Append(span);
        return last.Offset;
    }
}
