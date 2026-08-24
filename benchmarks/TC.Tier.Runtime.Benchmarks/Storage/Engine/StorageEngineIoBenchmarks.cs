using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Runtime.Benchmarks.Storage.Engine;

/// <summary>
/// StorageEngine 端到端 IO 性能基准——NullEngine vs StorageEngineBase 对比。
/// <para>★ 这是第一次能测到端到端 IO 吞吐（之前只有地址分配器，无真实数据搬运）。</para>
/// <para>★ NullEngine = 纯地址分配（CAS + lease），无 IO——性能下界。</para>
/// <para>★ StorageEngineBase = 地址分配 + MemoryCopy——内存 IO 上界。</para>
/// <para>★ 差值 = IO 搬运的净开销（MemoryCopy 成本）。</para>
/// <para>★ 修复磁盘模式后，磁盘结果与此对比 = DirectIO/syscall 的额外开销。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
[HideColumns("Error", "StdDev", "Median")]
public class StorageEngineIoBenchmarks : IDisposable
{
    private BenchVolume? _nullVol;
    private BenchVolume? _memVol;
    private StorageEngine? _null;
    private StorageEngine? _mem;
    private byte[] _payload = Array.Empty<byte>();
    private LogicalAddress[] _readAddresses = Array.Empty<LogicalAddress>();
    private byte[] _readBuf = Array.Empty<byte>();
    private int _readIdx;
    private LogicalAddress _writeBase;

    /// <summary>单次 Append 的 payload 字节数。</summary>
    [Params(64)]
    public int PayloadSize { get; set; }

    /// <summary>每次 Benchmark 调用连续 Append 的次数（总字节 = Count × PayloadSize）。</summary>
    [Params(10000)]
    public int OperationsPerInvoke { get; set; }

    /// <summary>段大小（影响跨段频率：1MB 少跨段，64KB 频繁跨段）。</summary>
    [Params(1024 * 1024)]
    public long SegmentGrowthLimit { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）——与测试 TC_TEST_FS_SPEC 同构
        _nullVol = new BenchVolume();
        _memVol = new BenchVolume();

        _null = (StorageEngine)new StorageEngineOptions("null", segmentGrowthLimit: SegmentGrowthLimit).Builder(_nullVol.Fs).Start();


        var options = new StorageEngineOptions("mem", segmentGrowthLimit: SegmentGrowthLimit).WithPreallocateFile(false);
        // ★ 禁用 CPU 背压节流（采样间隔 1h = 永不产生第二个样本 → factor 恒 0）：秒级持续满载的
        //   微基准会被 CpuSampler 三档节流 park（设计内"高负载让路"），测的是节流而非协议成本
        //   （实测可把 Write 拉到 137 µs/op）
        options = options.WithOptimization(options.Optimization with { SampleInterval = TimeSpan.FromHours(1) });
        _mem = (StorageEngine)options.Builder(_memVol.Fs, logger: new NullLogger()).Start();


        _payload = new byte[PayloadSize];
        for (int i = 0; i < PayloadSize; i++) _payload[i] = (byte)(i & 0xFF);

        // 预填充一批地址供 Read benchmark 用（限制条数防内存爆炸）
        var prepCount = Math.Min(OperationsPerInvoke, 200);
        _readAddresses = new LogicalAddress[prepCount];
        _readBuf = new byte[PayloadSize];
        var prepPayload = _payload;
        for (int i = 0; i < prepCount; i++)
        {
            _readAddresses[i] = _mem.Append(prepPayload);
        }
        _readIdx = 0;

        // ★ 核心场景预置：一次性 Allocate 大区间供 Write 复写（地址分配无 lease，复写才有）
        _writeBase = _mem.Allocate((long)prepCount * PayloadSize).Start;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _null?.Dispose();
        _mem?.Dispose();
        _nullVol?.Dispose();
        _memVol?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Append 吞吐（核心指标）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>NullEngine Append——纯地址分配（CAS + lease），无 IO 搬运。性能下界。</summary>
    [Benchmark, BenchmarkCategory("Append")]
    public LogicalAddress Append_Null()
    {
        LogicalAddress addr = default;
        var span = _payload.AsSpan();
        for (int i = 0; i < OperationsPerInvoke; i++)
            addr = _null!.Append(span);
        return addr;
    }


    /// <summary>MemoryEngine Append——地址分配 + MemoryCopy。内存 IO 上界。</summary>
    [Benchmark, BenchmarkCategory("Append")]
    public LogicalAddress Append_Memory()
    {
        LogicalAddress addr = default;
        var span = _payload.AsSpan();
        for (int i = 0; i < OperationsPerInvoke; i++)
            addr = _mem!.Append(span);
        // ★ 事后头截断（释放段内存，防多组合累积）——不在循环内（避免干扰 Append 性能测量）
        ReclaimHeadIfNeeded(_mem!);
        return addr;
    }

    /// <summary>MemoryEngine AppendAsync——异步路径（内存无 syscall，验证 async 开销）。</summary>
    [Benchmark, BenchmarkCategory("Append")]
    public async Task<LogicalAddress> AppendAsync_Memory()
    {
        LogicalAddress addr = default;
        var mem = _payload.AsMemory();
        for (int i = 0; i < OperationsPerInvoke; i++)
            addr = await _mem!.AppendAsync(mem, CancellationToken.None);
        return addr;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Write 复写（核心场景——地址分配无 lease，复写才走 lease 协议）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>引擎级 Write 复写——预 Allocate 区间上 round-robin 覆写（lease 协议全开销）。</summary>
    [Benchmark, BenchmarkCategory("Write")]
    public LogicalAddress Write_Memory()
    {
        LogicalAddress n = default;
        var span = _payload.AsSpan();
        int count = _readAddresses.Length;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            _readIdx = (_readIdx + 1) % count;
            n = _mem!.Write(_mem!.CalculationAddress(_writeBase, (long)_readIdx * PayloadSize), span);
        }
        return n;
    }

    /// <summary>页缓冲模型整体（核心场景端到端）：Allocate 一页 + 逐 entry 复写填充。</summary>
    [Benchmark, BenchmarkCategory("Write")]
    public LogicalAddress PageBuffer_AllocateThenWrite()
    {
        LogicalAddress n = default;
        var span = _payload.AsSpan();
        const int EntriesPerPage = 64;   // 64 entry × 64B = 4KB 页
        for (int p = 0; p < OperationsPerInvoke / EntriesPerPage; p++)
        {
            var page = _mem!.Allocate(EntriesPerPage * PayloadSize).Start;   // 无 lease：纯 CAS
            for (int e = 0; e < EntriesPerPage; e++)
                n = _mem!.Write(_mem!.CalculationAddress(page, (long)e * PayloadSize), span);   // lease 协议
        }
        return n;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Read 吞吐
    // ═══════════════════════════════════════════════════════════════

    /// <summary>MemoryEngine Read——MemoryCopy 读。NullEngine 读返回零（无搬运），不对比。</summary>
    [Benchmark, BenchmarkCategory("Read")]
    public int Read_Memory()
    {
        int n = 0;
        var buf = _readBuf;
        int count = _readAddresses.Length;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            _readIdx = (_readIdx + 1) % count;
            n = _mem!.Read(_readAddresses[_readIdx], buf);
        }
        return n;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Allocate（预留空间，不写数据）——验证纯地址分配开销（无 MemoryCopy）
    // ═══════════════════════════════════════════════════════════════

    [Benchmark, BenchmarkCategory("Allocate")]
    public LogicalAddress Allocate_Null()
    {
        LogicalAddress addr = default;
        for (int i = 0; i < OperationsPerInvoke; i++)
            addr = _null!.Allocate(PayloadSize).Start;
        return addr;
    }

    [Benchmark, BenchmarkCategory("Allocate")]
    public LogicalAddress Allocate_Memory()
    {
        LogicalAddress addr = default;
        for (int i = 0; i < OperationsPerInvoke; i++)
            addr = _mem!.Allocate(PayloadSize).Start;
        return addr;
    }

    // ═══════════════════════════════════════════════════════════════
    //  并发 Append 吞吐（多线程 CAS 竞争）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>MemoryEngine 并发 Append——4 线程竞争 CAS，验证并发可扩展性。</summary>
    [Benchmark, BenchmarkCategory("Concurrent")]
    public long ConcurrentAppend_Memory_4Threads()
    {
        const int Threads = 4;
        var mem = _mem!;
        var payload = _payload;
        int perThread = OperationsPerInvoke / Threads;
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    mem.Append(payload);
            });
        }
        Task.WaitAll(tasks);
        // ★ 事后头截断释放内存（防多组合累积卡死）
        ReclaimHeadIfNeeded(mem);
        return mem.AllocatedTail.Offset;
    }

    /// <summary>MemoryEngine 并发 Append——16 线程，高竞争下的 CAS 吞吐。</summary>
    [Benchmark, BenchmarkCategory("Concurrent")]
    public long ConcurrentAppend_Memory_16Threads()
    {
        const int Threads = 16;
        var mem = _mem!;
        var payload = _payload;
        int perThread = OperationsPerInvoke / Threads;
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    mem.Append(payload);
            });
        }
        Task.WaitAll(tasks);
        ReclaimHeadIfNeeded(mem);
        return mem.AllocatedTail.Offset;
    }

    /// <summary>头截断丢弃旧段（释放 pinned byte[]）——防内存无限增长。</summary>
    private static void ReclaimHeadIfNeeded(StorageEngine mem)
    {
        // 保留最后 4 段，之前的全丢弃
        var tail = mem.AllocatedTail;
        var keepSegId = tail.SegId > 4 ? tail.SegId - 4 : 0;
        if (keepSegId > mem.MinAddress.SegId)
            mem.ReclaimHead(new LogicalAddress(keepSegId, 0));
    }

    // ═══════════════════════════════════════════════════════════════
    //  后台建段吞吐（lifecycle worker + 异步建段 + CPU 限流）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 建段吞吐——小段强制频繁建段，测 lifecycle worker 的建段速率 + 对 Append 延迟的影响。
    /// <para>★ 4KB 段 + 64B payload：每 64 次 Append 跨段建段。1000 ops = ~16 段。</para>
    /// <para>★ 测量：建段速率（段/秒）+ Append 在建段压力下的延迟（含 yield 等待）。</para>
    /// </summary>
    [Benchmark, BenchmarkCategory("SegmentCreation")]
    public LogicalAddress SegmentCreationThroughput_Memory()
    {
        // 独立引擎 + 小段（4KB）——强制频繁建段但不极端
        var options = new StorageEngineOptions("seg", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var vol = new BenchVolume();
        using var engine = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();

        LogicalAddress addr = default;
        var span = _payload.AsSpan();
        const int SegBenchOps = 1000;  // 减少 ops 避免建段过多卡死
        for (int i = 0; i < SegBenchOps; i++)
        {
            addr = engine.Append(span);
        }

        return addr;
    }

    /// <summary>
    /// 并发建段吞吐——4 线程同时 Append 小段，测 lifecycle worker 在并发建段压力下的表现。
    /// <para>★ 验证 _maxInFlight 限流 + CPU 自适应 + inFlight 退避是否正常工作。</para>
    /// </summary>
    [Benchmark, BenchmarkCategory("SegmentCreation")]
    public long ConcurrentSegmentCreation_Memory_4Threads()
    {
        var options = new StorageEngineOptions("segc", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var vol = new BenchVolume();
        using var engine = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


        const int Threads = 4;
        var payload = _payload;
        const int SegBenchOps = 1000;
        int perThread = SegBenchOps / Threads;
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    engine.Append(payload);
            });
        }
        Task.WaitAll(tasks);
        return engine.AllocatedTail.SegId;  // 建段数 = SegId + 1
    }
}
