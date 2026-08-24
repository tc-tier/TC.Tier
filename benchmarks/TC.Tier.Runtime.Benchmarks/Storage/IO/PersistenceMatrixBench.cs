using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Runtime.Benchmarks.Storage.IO; using TC.Tier.Runtime.Benchmarks.Storage.Engine; using TC.Tier.Runtime.Benchmarks.Storage.AddressSpace; using TC.Tier.Runtime.Benchmarks.Storage.Compact;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// ★ S0 落盘矩阵——Device 层持久化语义的核心维度（用户明确要求）。
/// <para>量化 page cache(开/关) × 落盘强度(不落盘/每写落盘/周期 Flush) 的 2×3=6 组合，
/// 以及周期 Flush 在 4 个频率梯度下"崩溃窗口 vs 吞吐"的折中曲线。</para>
///
/// <para><b>6 组合矩阵</b>（PersistenceMode × DirectIoMode × Flush 策略）：</para>
/// <list type="table">
/// <item><term>P1</term><description>page cache 开，不落盘（Buffered, None）——对应现有 Mode A</description></item>
/// <item><term>P2</term><description>page cache 开，每写落盘（Buffered, WriteThrough）——对应现有 Mode B</description></item>
/// <item><term>P3</term><description>page cache 开，周期 Flush（Buffered, None + 每 N MB Flush）</description></item>
/// <item><term>P4</term><description>page cache 关(DIO)，不落盘（Enabled, None）——对应现有 Mode C</description></item>
/// <item><term>P5</term><description>page cache 关(DIO)，每写落盘（Enabled, WriteThrough）——对应现有 Mode D</description></item>
/// <item><term>P6</term><description>page cache 关(DIO)，周期 Flush（Enabled, None + 每 N MB Flush）</description></item>
/// </list>
///
/// <para>★ 关键洞察：P3/P6 是 group commit，生产真正用的模式（解封性能 + 可控崩溃窗口），
///   现有 methodology 完全没量化。本基准补这个盲区。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*PersistenceMatrix*"</para>
/// <para>单组合快速验证：dotnet run ... -f "*PersistenceMatrix*" Combo=P3 FlushEveryKB=10240</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[ExceptionDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
public partial class PersistenceMatrixBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _dev = null!;
    private AlignedMemoryManager _buf = null!;
    private long _totalBytes;
    private int _blocks;
    private LatencyHistogram _latency = null!;

    /// <summary>组合：1-6 对应 P1-P6（见类注释）。</summary>
    [Params(1, 2, 3, 4, 5, 6)]
    public int Combo { get; set; }

    /// <summary>
    /// 周期 Flush 的频率梯度（仅 P3/P6 用；其他组合忽略）。
    /// 单位 KB：1024=每 1MB flush，10240=每 10MB，102400=每 100MB；特殊值 0=按时间(每 1s)。
    /// </summary>
    [Params(1024, 10240, 102400, 0)]
    public int FlushEveryKB { get; set; }

    /// <summary>块大小——固定 64K（吞吐足够大，又不至于让 DIO 退化）。其他块大小已有 NewDeviceModeMatrixBench 覆盖。</summary>
    private const int BlockSize = 64 * 1024;

    private const int TotalMB = 128;

    private FileOpenHints ComboCfg => Combo switch
    {
        1 => FileOpenHints.None,
        2 => FileOpenHints.WriteThrough,
        3 => FileOpenHints.None,        // + 周期 Flush
        4 => FileOpenHints.NoBuffering,
        5 => FileOpenHints.NoBuffering | FileOpenHints.WriteThrough,
        6 => FileOpenHints.NoBuffering,         // + 周期 Flush
        _ => throw new ArgumentOutOfRangeException()
    };

    /// <summary>P3/P6 = Combo 3/6 才启用周期 Flush。</summary>
    private bool UsePeriodicFlush => Combo == 3 || Combo == 6;

    private StorageEngine NewDevice()
    {
        var hints = ComboCfg;
        var devName = $"p{Combo}-f{FlushEveryKB}-{Guid.NewGuid():N}";
        var options = new StorageEngineOptions(devName, segmentGrowthLimit: 1L << 30).WithPreallocateFile(false).WithHints(hints);
        var dev = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();

        return dev;
    }

    [GlobalSetup]
    public void Setup()
    {
        _vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        _totalBytes = (long)TotalMB * 1024 * 1024;
        _blocks = (int)(_totalBytes / BlockSize);
        _buf = new AlignedMemoryManager(Math.Max(BlockSize, 4194304), 4096);
        _buf.GetSpan().Slice(0, BlockSize).Fill(0x42);
        _latency = new LatencyHistogram(capacity: 1 << 17);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _dev?.Dispose();
        _vol?.Dispose();
        _vol = new BenchVolume();   // 每迭代全新卷
        _dev = NewDevice();
        _latency = new LatencyHistogram(capacity: 1 << 17);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dev?.Dispose();
        _vol?.Dispose();
        _buf?.Dispose();
    }

    /// <summary>
    /// SeqAppend 配合指定的落盘策略。每次写测延迟（捕获 group commit 下 Flush 尖峰）。
    /// </summary>
    [Benchmark(Description = "SeqAppend")]
    public void SeqAppend()
    {
        var span = _buf.GetSpan().Slice(0, BlockSize);
        bool periodic = UsePeriodicFlush;
        long flushEveryBytes = FlushEveryKB > 0 ? (long)FlushEveryKB * 1024 : 0;
        long lastFlushAt = 0;
        long lastFlushTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long oneSecondTicks = System.Diagnostics.Stopwatch.Frequency; // 1s

        for (int i = 0; i < _blocks; i++)
        {
            long t0 = LatencyHistogram.Start();
            _dev.Append(span);
            _latency.Measure(t0);

            if (periodic)
            {
                long written = (long)(i + 1) * BlockSize;
                bool shouldFlush = false;
                if (flushEveryBytes > 0)
                    shouldFlush = (written - lastFlushAt) >= flushEveryBytes;
                else // 按时间（每 1s）
                    shouldFlush = (System.Diagnostics.Stopwatch.GetTimestamp() - lastFlushTicks) >= oneSecondTicks;

                if (shouldFlush)
                {
                    long tf = LatencyHistogram.Start();
                    _dev.Flush();
                    _latency.Measure(tf);  // Flush 延迟也算进分布——这是 group commit 的尖峰来源
                    lastFlushAt = written;
                    lastFlushTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
        }

        // 收尾 Flush（保证周期 Flush 模式的数据真落盘，公平对比）
        if (periodic) _dev.Flush();
    }

    /// <summary>迭代结束后打印延迟分位——BDN 把 Console 写到 artifact 里，便于报告引用。</summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        Console.WriteLine($"[PersistenceMatrix] Combo=P{Combo} FlushEveryKB={FlushEveryKB} → {_latency.Summary()}");
    }
}
