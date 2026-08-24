using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Runtime.Benchmarks.Storage.IO; using TC.Tier.Runtime.Benchmarks.Storage.Engine; using TC.Tier.Runtime.Benchmarks.Storage.AddressSpace; using TC.Tier.Runtime.Benchmarks.Storage.Compact;

namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// ★ C3 物理空间分配——段创建开销独立基准（填补盲区）。
/// <para>现有 <c>SegmentTableOverheadBench</c> 只测段表查询 ns 级，未测建段 worker 本身的开销。</para>
/// <para>覆盖：</para>
/// <list type="bullet">
/// <item>单段建段延迟（NotCreated→Active 状态机 + preallocate syscalls，p50/p99/max）</item>
/// <item>连续建 N 段的吞吐（worker 是否成瓶颈）</item>
/// <item><c>Allocate(long)</c> 预留水位推进的 CAS 开销（跨段进位）</item>
/// </list>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*SegmentCreation*"</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[ExceptionDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
public partial class SegmentCreationBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _dev = null!;
    private LatencyHistogram _createLatency = null!;

    /// <summary>段大小梯度——测建段延迟随段大小的变化（preallocate 开销主导）。</summary>
    [Params(1024 * 1024, 16 * 1024 * 1024, 256 * 1024 * 1024)]
    public int SegmentSize { get; set; }

    /// <summary>是否预分配真实磁盘空间（true=SetFileValidData 真分配；false=稀疏文件）。</summary>
    [Params(true, false)]
    public bool Preallocate { get; set; }

    /// <summary>建段数量（连续建 N 段测吞吐）。</summary>
    private const int SegmentCount = 8;

    [GlobalSetup]
    public void Setup()
    {
        _vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        _createLatency = new LatencyHistogram(capacity: 1 << 16);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _dev?.Dispose();
        _vol?.Dispose();
        _vol = new BenchVolume();   // 每迭代全新卷
        var options = new StorageEngineOptions($"sc-{Guid.NewGuid():N}", segmentGrowthLimit: SegmentSize).WithPreallocateFile(Preallocate);
        _dev = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();
        // 小段——快速触发跨段（建段主路径就是 segmentGrowthLimit 决定的）

        _createLatency = new LatencyHistogram(capacity: 1 << 16);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dev?.Dispose();
        _vol?.Dispose();
    }

    /// <summary>
    /// 连续建 SegmentCount 个段——通过 Append 小数据触发跨段，每跨一段记一次建段延迟。
    /// <para>★ 每次 Append 1 字节，强制快速跨段（每段第 1 个字节就推到下一段边界）。</para>
    /// <para>★ Append 完每段后 worker 建段，记录建段完成时间。</para>
    /// </summary>
    [Benchmark(Description = "SegmentCreation.Sequential")]
    public long SegmentCreationSequential()
    {
        // 用稍小于段大小的 payload，确保每段填满后下个 Append 触发新段
        int payloadLen = Math.Max(1, SegmentSize - 1);
        var buf = new AlignedMemoryManager(Math.Max(payloadLen, 4096), 4096);
        try
        {
            var span = buf.GetSpan().Slice(0, payloadLen);
            span.Fill(0x42);
            long totalBytes = 0;
            for (int i = 0; i < SegmentCount; i++)
            {
                long t0 = LatencyHistogram.Start();
                var addr = _dev.Append(span);
                _createLatency.Measure(t0);  // Append-to-new-segment 延迟（含 worker 建段 + preallocate）
                totalBytes += payloadLen;
            }
            // ★ 不调 _dev.Flush()——建段基准不需要强制落盘，且当前 Flush() 在多段场景有 IndexOutOfRange bug（已记录）
            return totalBytes;
        }
        finally { buf.Dispose(); }
    }

    /// <summary>
    /// Allocate(long) 预留水位推进——测 CAS 在跨段进位时的开销。
    /// <para>★ Allocate ≡ Append − pwrite：纯 CAS 推进 _tail，不写数据。</para>
    /// </summary>
    [Benchmark(Description = "Allocate.Watermark")]
    public long AllocateWatermark()
    {
        int allocLen = Math.Max(1, SegmentSize - 1);
        long totalAdvanced = 0;
        for (int i = 0; i < SegmentCount; i++)
        {
            long t0 = LatencyHistogram.Start();
            var addr = _dev.Allocate(allocLen);
            _createLatency.Measure(t0);
            totalAdvanced += allocLen;
        }
        return totalAdvanced;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        Console.WriteLine($"[SegmentCreation] SegSize={SegmentSize / 1024}KB Prealloc={Preallocate} → {_createLatency.Summary()}");
    }
}
