using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Runtime.Benchmarks.Storage.IO; using TC.Tier.Runtime.Benchmarks.Storage.Engine; using TC.Tier.Runtime.Benchmarks.Storage.AddressSpace; using TC.Tier.Runtime.Benchmarks.Storage.Compact;

namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// ★ C5 空间回收——三类回收操作的吞吐 + 延迟（填补零基准盲区）。
/// <para>覆盖：</para>
/// <list type="bullet">
/// <item><c>ReclaimHead</c>：删整段 + AddressMap.ShiftDown 开销</item>
/// <item><c>Reclaim</c>：PunchHole 字节级打洞 syscall 开销 + 回收后写放大</item>
/// <item><c>Compact</c>：全量搬迁 + MigrationMap 构建 + 旧段真删除</item>
/// <item><c>Compact_ThenAppend</c>：Compact 后继续 Append（Bug 5 修复回归保险）</item>
/// </list>
/// <para>★ Compact 在 commit 3d719d9 后完整可用（切分多段 + NormalizeLeaseStart + RealSize=segLimit）。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*SpaceReclaim*"</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[ExceptionDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
public partial class SpaceReclaimBench : IDisposable
{
    private BenchVolume _vol = null!;
    private StorageEngine _dev = null!;
    private AlignedMemoryManager _buf = null!;
    private LatencyHistogram _reclaimLatency = null!;

    /// <summary>回收区间大小梯度（Reclaim/Compact 用）。</summary>
    [Params(4096, 65536, 1048576, 268435456)]
    public int RangeSize { get; set; }

    /// <summary>段大小——小段产生多段便于 ReclaimHead/Compact 测试。</summary>
    private const int SegmentSize = 16 * 1024 * 1024;  // 16MB

    [GlobalSetup]
    public void Setup()
    {
        _vol = new BenchVolume();   // 介质 = TC_BENCH_FS_SPEC（缺省 memory:；真磁盘 local:///…）
        _buf = new AlignedMemoryManager(Math.Max(RangeSize, 4 * 1024 * 1024), 4096);
        _buf.GetSpan().Slice(0, Math.Min(RangeSize, 4 * 1024 * 1024)).Fill(0x42);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _dev?.Dispose();
        _vol?.Dispose();
        _vol = new BenchVolume();   // 每迭代全新卷
        var options = new StorageEngineOptions($"rc-{Guid.NewGuid():N}", segmentGrowthLimit: SegmentSize).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        _dev = (StorageEngine)options.Builder(_vol.Fs, logger: new NullLogger()).Start();

        _reclaimLatency = new LatencyHistogram(capacity: 1 << 16);
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

    /// <summary>预填 N 段（每段写满），用于 ReclaimHead/Compact 测试。</summary>
    private void PreFillSegments(int segmentCount)
    {
        int payload = Math.Min(RangeSize, SegmentSize);
        var span = _buf.GetSpan().Slice(0, payload);
        for (int s = 0; s < segmentCount; s++)
        {
            int blocksPerSeg = SegmentSize / payload;
            for (int i = 0; i < blocksPerSeg; i++)
                _dev.Append(span);
        }
        _dev.Flush();
    }

    /// <summary>ReclaimHead 删整段——测删段吞吐 + AddressMap.ShiftDown 开销。</summary>
    [Benchmark(Description = "ReclaimHead.Segment")]
    public long ReclaimHeadSegment()
    {
        const int segments = 8;
        PreFillSegments(segments);
        long reclaimed = 0;
        // 删前 N-1 段（留最后一段活跃，符合 ReclaimHead 语义）
        for (int i = 0; i < segments - 1; i++)
        {
            long t0 = LatencyHistogram.Start();
            _dev.ReclaimHead(new LogicalAddress(1, 0));
            _reclaimLatency.Measure(t0);
            reclaimed += SegmentSize;
        }
        return reclaimed;
    }

    /// <summary>Reclaim PunchHole 字节级打洞——测 syscall 开销 + 区间大小影响。</summary>
    [Benchmark(Description = "Reclaim.PunchHole")]
    public long ReclaimPunchHole()
    {
        // 写一段足够大的数据
        int payload = Math.Min(RangeSize, 16 * 1024 * 1024);
        var span = _buf.GetSpan().Slice(0, payload);
        // 写 16 个 RangeSize 区间（共 16×RangeSize 字节）
        int regions = Math.Max(1, 16 * 1024 * 1024 / payload);
        var startAddrs = new LogicalAddress[regions];
        for (int i = 0; i < regions; i++)
        {
            startAddrs[i] = _dev.Append(span);
        }
        _dev.Flush();

        // 对每个区间做 Reclaim 打洞（from, to 都是 LogicalAddress；to = from + payload 字节）
        long reclaimed = 0;
        for (int i = 0; i < regions; i++)
        {
            var from = startAddrs[i];
            // 构造 to 地址：同段内 offset + payload（假设 payload 不跨段——这是 RangeSize ≤ 16MB 段的设定）
            var to = new LogicalAddress(from.SegId, from.Offset + payload);
            long t0 = LatencyHistogram.Start();
            _dev.Reclaim(from, to);
            _reclaimLatency.Measure(t0);
            reclaimed += payload;
        }
        return reclaimed;
    }

    /// <summary>Compact 全量搬迁——测搬迁吞吐 + MigrationMap 构建 + 旧段删除。</summary>
    [Benchmark(Description = "Compact.Migration")]
    public async Task<long> CompactMigration()
    {
        const int segments = 4;
        PreFillSegments(segments);
        long t0 = LatencyHistogram.Start();
        var result = await _dev.StartCompact().WaitAsync();
        _reclaimLatency.Measure(t0);
        return result.MigrationMap?.Count ?? 0;
    }

    /// <summary>
    /// Compact 后继续 Append（Bug 5 修复回归保险）。
    /// <para>★ Compact 后新活跃段应可写、写后可读（验证 NormalizeLeaseStart + RealSize=segLimit）。</para>
    /// </summary>
    [Benchmark(Description = "Compact_ThenAppend.Continue")]
    public async Task<long> CompactThenAppendContinue()
    {
        const int segments = 4;
        PreFillSegments(segments);
        await _dev.StartCompact().WaitAsync();
        // 同步 helper（async 方法禁 ref struct——C# 12）
        return AppendAfterCompact();
    }

    /// <summary>Compact 后继续 Append——若 Bug 5 未修，这里会抛异常或 Read 返回 0。</summary>
    private long AppendAfterCompact()
    {
        int payload = Math.Min(RangeSize, 64 * 1024);
        var span = _buf.GetSpan().Slice(0, payload);
        long written = 0;
        for (int i = 0; i < 32; i++)
        {
            long t0 = LatencyHistogram.Start();
            var addr = _dev.Append(span);
            _reclaimLatency.Measure(t0);
            // 读回校验（Bug 5 修复后应能读到）
            var dst = _buf.GetSpan().Slice(payload, payload);
            int n = _dev.Read(addr, dst);
            if (n != payload)
                throw new InvalidOperationException($"Compact 后 Append 读不到：n={n} expected={payload}");
            written += payload;
        }
        return written;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        Console.WriteLine($"[SpaceReclaim] RangeSize={RangeSize / 1024}KB → {_reclaimLatency.Summary()}");
    }
}
