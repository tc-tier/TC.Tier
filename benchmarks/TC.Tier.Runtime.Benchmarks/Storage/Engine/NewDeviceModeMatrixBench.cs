using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Runtime.Benchmarks.Storage.Engine;

/// <summary>
/// ★ 新 LocalStorageDevice (Segment/SegmentSpace 重构后) Mode × BlockSize 完整矩阵。
/// <para>与 <see cref="DeviceModeMatrixBench"/>（老 ManagedLocalStorageDevice）对等对比——</para>
/// <para>同矩阵、同方法论、同环境，量化新版本相对老版本的性能差异。</para>
/// <para>维度：Mode(A/B/C/D) × BlockSize(4K/64K/256K/1M/4M) × {SeqWrite, SeqRead-warm}</para>
/// <para>★ Windows 冷读：用 DIO 只读句柄重开（绕 page cache）；Linux 仍可用 posix_fadvise。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks/ -- --filter "*NewDeviceModeMatrix*"</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[ExceptionDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
public partial class NewDeviceModeMatrixBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _dev = null!;
    private AlignedMemoryManager _buf = null!;
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

    private StorageEngine NewDevice()
    {
        var hints = ModeCfg;
        var devName = $"m{Mode}-{Guid.NewGuid():N}";
        // 新设备：baseDirectory + deviceName（子目录），preallocateFile=false 与老版本对齐
        var options = new StorageEngineOptions(devName, segmentGrowthLimit: 1L << 30).WithPreallocateFile(false).WithHints(hints);
        var dev = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();
        // 大段（1GB）避免跨段干扰——与老版本 segmentSize=1<<30 对等

        return dev;
    }

    [GlobalSetup]
    public void Setup()
    {
        _vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        _totalBytes = (long)TotalMB * 1024 * 1024;
        _buf = new AlignedMemoryManager(Math.Max(BlockSize, 4194304), 4096);
        _buf.GetSpan().Slice(0, BlockSize).Fill(0x42);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _blocks = (int)(_totalBytes / BlockSize);
        _dev?.Dispose();
        _vol?.Dispose();
        _vol = new BenchVolume();   // 每迭代全新卷
        _dev = NewDevice();
    }

    /// <summary>为读基准预填数据——用 Append 顺序写满（新设备推荐路径）。</summary>
    private void PreFillForRead()
    {
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
    }

    // ── 写：Append（推进游标，新设备主推路径）──

    [Benchmark(Description = "SeqAppend")]
    public void SeqAppend()
    {
        var span = _buf.GetSpan().Slice(0, BlockSize);
        for (int i = 0; i < _blocks; i++)
            _dev.Append(span);
    }

    // ── 读：热读（page cache 命中 / 设备 cache）──
    // ★ 读矩阵拆到 NewDeviceReadMatrixBench——旧实现 PreFillForRead 在计时体内，
    //   "读"数字实为 128MB 预填写 + 读混合（WT 模式被写延迟污染 40×）。
}
