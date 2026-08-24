using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Runtime.Benchmarks.Storage.Engine;

/// <summary>
/// ★ 新 LocalStorageDevice 并发 I/O 基准 — 多线程并发 Append/Read，量化新设备并发扩展性。
/// <para>与 <see cref="DeviceIoParallelBench"/>（老 ManagedLocalStorageDevice）对等，矩阵一致：</para>
/// <para>Mode(A/B/C/D) × BlockSize(4K/64K/256K/1M) × Threads(1/2/4/8/16)</para>
/// <para>覆盖能力 C1（随机写）/C2（随机读）的并发吞吐 + 内存分配。</para>
/// <para>★ buffer 策略：每线程独立 AlignedMemoryManager 数组（按 tid 索引），避免并发覆盖 + DIO 对齐冲突。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*NewDeviceIoParallel*"</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[ExceptionDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3, invocationCount: 32)]
public partial class NewDeviceIoParallelBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _dev = null!;
    private AlignedMemoryManager[] _threadBufs = null!;
    private LogicalAddress[] _sampleAddrs = null!;

    /// <summary>IO 模式：0=A(Buf+None), 1=B(Buf+WT), 2=C(DIO+None), 3=D(DIO+WT)</summary>
    [Params(0, 1, 2, 3)]
    public int Mode { get; set; }

    [Params(4096, 65536, 262144, 1048576)]
    public int BlockSize { get; set; }

    /// <summary>线程数——按逻辑核数对齐（本机 12 逻辑核/6 物理核）：1/6/12/24 测并发扩展性拐点。</summary>
    [Params(1, 6, 12, 24)]
    public int Threads { get; set; }

    private const int SizeMB = 256;
    private const long TotalBytes = (long)SizeMB * 1024 * 1024;
    private const int OpsPerThread = 1024;

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
        // 每线程独立对齐 buffer（防并发覆盖 + DIO 对齐）
        _threadBufs = new AlignedMemoryManager[Math.Max(Threads, 1)];
        for (int i = 0; i < _threadBufs.Length; i++)
        {
            _threadBufs[i] = new AlignedMemoryManager(BlockSize, 4096);
            _threadBufs[i].GetSpan().Slice(0, BlockSize).Fill((byte)(0x40 + i));
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _dev?.Dispose();
        _vol?.Dispose();
        _vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        var hints = ModeCfg;
        var options = new StorageEngineOptions($"par-{Guid.NewGuid():N}", segmentGrowthLimit: 1L << 30).WithPreallocateFile(false).WithHints(hints);
        var dev = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();

        _dev = dev;

        // 预填 256MB（用 Append），供后续并发读
        var span = _threadBufs[0].GetSpan().Slice(0, BlockSize);
        int totalBlocks = (int)(TotalBytes / BlockSize);
        for (int i = 0; i < totalBlocks; i++)
            _dev.Append(span);
        _dev.Flush();

        // 采样地址（每线程从这些地址里 round-robin 读）
        int sampleCount = Math.Max(OpsPerThread * Threads, 256);
        _sampleAddrs = new LogicalAddress[sampleCount];
        int maxBlocks = (int)(TotalBytes / BlockSize);
        var rng = new Random(42);
        for (int i = 0; i < sampleCount; i++)
        {
            int blk = rng.Next(0, maxBlocks);
            _sampleAddrs[i] = new LogicalAddress(0, (long)blk * BlockSize);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dev?.Dispose();
        _vol?.Dispose();
        if (_threadBufs != null)
            foreach (var b in _threadBufs) b?.Dispose();
    }

    /// <summary>并发随机读——每线程独立 buffer，从采样地址 round-robin 读。</summary>
    [Benchmark(Description = "RandRead(parallel)")]
    public long RandReadParallel()
    {
        long total = 0;
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int tid = t;
            tasks[tid] = Task.Run(() =>
            {
                var buf = _threadBufs[tid].GetSpan().Slice(0, BlockSize);
                int idx = tid;
                for (int i = 0; i < OpsPerThread; i++)
                {
                    total += _dev.Read(_sampleAddrs[idx % _sampleAddrs.Length], buf);
                    idx += Threads;
                }
            });
        }
        Task.WaitAll(tasks);
        return total;
    }

    /// <summary>并发顺序 Append——多线程同时 Append（CAS 租借主路径，C4 地址私有性的并发压测）。</summary>
    [Benchmark(Description = "SeqAppend(parallel)")]
    public long SeqAppendParallel()
    {
        long total = 0;
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int tid = t;
            tasks[tid] = Task.Run(() =>
            {
                var buf = _threadBufs[tid].GetSpan().Slice(0, BlockSize);
                long local = 0;
                for (int i = 0; i < OpsPerThread; i++)
                {
                    var addr = _dev.Append(buf);
                    local += BlockSize;
                }
                Interlocked.Add(ref total, local);
            });
        }
        Task.WaitAll(tasks);
        return total;
    }

    /// <summary>并发随机覆写——每线程往不同地址覆写（C1 随机写的并发版，验证 LockWord 不撕裂）。</summary>
    [Benchmark(Description = "RandWrite(parallel)")]
    public long RandWriteParallel()
    {
        long total = 0;
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int tid = t;
            tasks[tid] = Task.Run(() =>
            {
                var buf = _threadBufs[tid].GetSpan().Slice(0, BlockSize);
                long local = 0;
                int idx = tid;
                for (int i = 0; i < OpsPerThread; i++)
                {
                    _dev.Write(_sampleAddrs[idx % _sampleAddrs.Length], buf);
                    local += BlockSize;
                    idx += Threads;
                }
                Interlocked.Add(ref total, local);
            });
        }
        Task.WaitAll(tasks);
        return total;
    }
}
