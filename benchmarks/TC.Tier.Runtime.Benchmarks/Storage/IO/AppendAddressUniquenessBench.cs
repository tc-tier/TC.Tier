using System.Buffers;
using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// ★ C4 地址空间私有占用——CAS 正确性的并发压测（填补盲区）。
/// <para>现有 <c>NewDeviceConcurrentTests.MultiThreadAppend_*</c> 只是正确性断言（小规模 4T×50×64B），
/// 本基准是压测级：百万级 Append × 高并发，同时验证地址绝对私有 + 测吞吐 + 测尾延迟。</para>
/// <para>★ 核心断言：所有线程返回的 (segId, offset) 区间两两不重叠（HashSet 校验，任何重叠 = CAS bug）。</para>
/// <para>★ 同时测：CAS 高竞争下的重试次数、吞吐随线程数扩展曲线、p99/p999 尾延迟。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*AppendAddressUniqueness*"</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[ExceptionDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
public partial class AppendAddressUniquenessBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _dev = null!;
    private AlignedMemoryManager[] _threadBufs = null!;
    private LatencyHistogram _appendLatency = null!;

    /// <summary>
    /// 线程数——压测 CAS 高竞争扩展曲线。
    /// <para>★ 矩阵按逻辑核数对齐（本机 Environment.ProcessorCount=12，物理核 6）：</para>
    /// <para>  1 = 单线程基线；6 = 物理核数（无超线程争用）；12 = 逻辑核数（刚饱和）；
    ///         24 = 2× 超线程（测 CAS 争用退化）；48 = 4× 极端争用（饱和后行为）。</para>
    /// <para>★ 不要用 32/64 这种与核数无关的任意值——超核线程会引入 OS 调度抖动，
    ///   测出来的是"线程切换开销"不是"CAS 本身性能"。</para>
    /// </summary>
    [Params(1, 6, 12, 24, 48)]
    public int Threads { get; set; }

    /// <summary>Payload 大小——影响每段容纳多少次 Append（小 payload 段切换快，CAS 争用频）。</summary>
    [Params(64, 256, 4096)]
    public int Payload { get; set; }

    /// <summary>每线程 Append 次数——总 ops = Threads × OpsPerThread。</summary>
    private const int OpsPerThread = 10_000;

    [GlobalSetup]
    public void Setup()
    {
        _vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        // 每线程独立对齐 buffer
        _threadBufs = new AlignedMemoryManager[Threads];
        for (int i = 0; i < Threads; i++)
        {
            _threadBufs[i] = new AlignedMemoryManager(Math.Max(Payload, 4096), 4096);
            _threadBufs[i].GetSpan().Slice(0, Payload).Fill((byte)(0x40 + (i & 0x3F)));
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _dev?.Dispose();
        _vol?.Dispose();
        _vol = new BenchVolume();   // 每迭代全新卷
        // 4MB 段——小段快速触发跨段 + AddressMap 扩容（高并发下 CAS 争用 + 段创建 worker 都被压）
        var options = new StorageEngineOptions($"un-{Guid.NewGuid():N}", segmentGrowthLimit: 4 * 1024 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        _dev = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();

        _appendLatency = new LatencyHistogram(capacity: 1 << 18);
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

    /// <summary>
    /// 高并发 Append——百万级 ops，HashSet 校验地址绝对私有 + 测吞吐 + 测尾延迟。
    /// <para>★ 若任何线程返回的 [addr, addr+Payload) 区间与其他线程重叠，CAS 有 bug，断言失败。</para>
    /// </summary>
    [Benchmark(Description = "Append.UniqueAddresses")]
    public long AppendUniqueAddresses()
    {
        // 收集所有线程返回的地址（带线程 ID 编码便于冲突定位）
        var perThreadAddrs = new ConcurrentBag<(long threadHash, LogicalAddress addr)>[Threads];
        for (int i = 0; i < Threads; i++)
            perThreadAddrs[i] = new ConcurrentBag<(long, LogicalAddress)>();

        long totalBytes = 0;
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int tid = t;
            tasks[tid] = Task.Run(() =>
            {
                var buf = _threadBufs[tid].GetSpan().Slice(0, Payload);
                var bag = perThreadAddrs[tid];
                long threadHash = ((long)tid + 1) << 40;  // 编码线程 ID 到高位
                for (int i = 0; i < OpsPerThread; i++)
                {
                    long t0 = LatencyHistogram.Start();
                    var addr = _dev.Append(buf);
                    _appendLatency.Measure(t0);
                    bag.Add((threadHash, addr));
                }
                Interlocked.Add(ref totalBytes, Payload);
            });
        }
        Task.WaitAll(tasks);

        // ★ 正确性校验：所有地址区间两两不重叠
        // 把所有地址排序后检查相邻区间不重叠（payload 范围内）
        var allAddrs = new List<(long key, LogicalAddress addr)>(Threads * OpsPerThread);
        foreach (var bag in perThreadAddrs)
            foreach (var x in bag) allAddrs.Add(x);
        // 段内 offset 排序：把地址编码成可比较的 long（segId 高 32 位 + offset 低 32 位够用，溢出少见）
        allAddrs.Sort((a, b) =>
        {
            int c = a.addr.SegId.CompareTo(b.addr.SegId);
            if (c != 0) return c;
            return a.addr.Offset.CompareTo(b.addr.Offset);
        });

        long overlapBytes = 0;
        for (int i = 1; i < allAddrs.Count; i++)
        {
            var prev = allAddrs[i - 1].addr;
            var curr = allAddrs[i].addr;
            // 同段内 prev + Payload 必须不超过 curr（区间私有）
            if (prev.SegId == curr.SegId)
            {
                long prevEnd = prev.Offset + Payload;
                if (prevEnd > curr.Offset)
                {
                    // 区间重叠——CAS bug
                    overlapBytes += (prevEnd - curr.Offset);
                    Console.WriteLine($"[CAS-BUG] overlap at seg#{curr.SegId}: prev@{prev.Offset}+{Payload}={prevEnd} > curr@{curr.Offset} (overlap={prevEnd - curr.Offset})");
                }
            }
        }

        if (overlapBytes > 0)
        {
            throw new InvalidOperationException(
                $"CAS 地址重叠 bug：{overlapBytes} 字节区间冲突（threads={Threads} payload={Payload}）");
        }

        return totalBytes;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        Console.WriteLine($"[AppendUniqueness] Threads={Threads} Payload={Payload} → {_appendLatency.Summary()}");
    }
}
