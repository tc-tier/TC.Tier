using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Runtime.Benchmarks.Storage.Engine;

/// <summary>
/// 读矩阵（Mode × BlockSize）——页缓存热读 vs DIO 直读对照。
/// <para>★ 与 <see cref="NewDeviceModeMatrixBench"/>（写矩阵）分家：旧读实现把 128MB 预填写算进了
///   计时体，WT 模式"读"数字被写延迟污染 40×。本类预填全部进 <c>IterationSetup</c>（计时外），
///   Benchmark 体只有纯读循环。</para>
/// <para>★ 口径：Buffered 模式（0/1）预填后数据驻 page cache → 热读 = DRAM 速度；
///   DIO 模式（2/3）预填写直达盘、绕 cache → 读 = 设备直读。对照 = "页缓存热读 vs DIO 冷读"。</para>
/// <para>运行：dotnet run -c Release -- --filter '*NewDeviceReadMatrixBench*'（TC_BENCH_FS_SPEC 切介质）</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[ExceptionDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
public class NewDeviceReadMatrixBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _dev = null!;
    private AlignedMemoryManager _buf = null!;
    private AlignedMemoryManager _dst = null!;
    private long _totalBytes;
    private int _blocks;

    /// <summary>IO 模式：0=A(Buf+None), 1=B(Buf+WT), 2=C(DIO+None), 3=D(DIO+WT)</summary>
    [Params(0, 1, 2, 3)]
    public int Mode { get; set; }

    [Params(4096, 65536, 262144, 1048576, 4194304)]
    public int BlockSize { get; set; }

    private const int TotalMB = 128;

    private FileOpenHints ModeCfg => Mode switch
    {
        0 => FileOpenHints.None,
        1 => FileOpenHints.WriteThrough,
        2 => FileOpenHints.NoBuffering,
        3 => FileOpenHints.NoBuffering | FileOpenHints.WriteThrough,
        _ => throw new ArgumentOutOfRangeException()
    };

    [GlobalSetup]
    public void Setup()
    {
        _totalBytes = (long)TotalMB * 1024 * 1024;
        _blocks = (int)(_totalBytes / BlockSize);
        _buf = new AlignedMemoryManager(Math.Max(BlockSize, 4194304), 4096);
        _buf.GetSpan().Slice(0, BlockSize).Fill(0x42);
        _dst = new AlignedMemoryManager(BlockSize, 4096);   // DIO 读要求 buffer 对齐
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _dev?.Dispose();
        _vol?.Dispose();
        _vol = new BenchVolume();   // 每迭代全新卷 + 预填（全部计时外）
        var options = new StorageEngineOptions($"rd{Mode}-{Guid.NewGuid():N}", segmentGrowthLimit: 1L << 30)
            .WithPreallocateFile(false).WithHints(ModeCfg);
        _dev = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();

        var span = _buf.GetSpan().Slice(0, BlockSize);
        for (int i = 0; i < _blocks; i++)
            _dev.Append(span);
        _dev.Flush();
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dev?.Dispose();
        _vol?.Dispose();
        _buf?.Dispose();
        _dst?.Dispose();
    }

    /// <summary>顺序热读 128MB——纯读循环（预填已在 IterationSetup）。</summary>
    [Benchmark(Description = "SeqRead.warm")]
    public void SeqReadWarm()
    {
        var span = _dst.GetSpan().Slice(0, BlockSize);
        for (int i = 0; i < _blocks; i++)
        {
            var addr = new LogicalAddress(0, (long)i * BlockSize);
            _dev.Read(addr, span);
        }
    }
}
