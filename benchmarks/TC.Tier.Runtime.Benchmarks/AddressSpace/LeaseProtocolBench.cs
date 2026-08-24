using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

// ★ CA1001 抑制：BDN 基准实例由 harness 生命周期管理（全局/迭代期资源，无 Dispose 时机）
#pragma warning disable CA1001

namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// lease 协议性能基准——纯内存 SegmentTable（handler=null），隔离 IO 开销，专注 lease 协议本身。
/// <para>★ 覆盖 5 种 lease 的创建+Commit 单次延迟 + 吞吐：</para>
/// <list type="bullet">
/// <item>Append（CAS 推尾热路径——最频繁）</item>
/// <item>Allocate（CAS 推尾 + 即时提交，无 lease 对象）</item>
/// <item>Write（覆写已提交区间——状态流转）</item>
/// <item>Reclaim（中间打洞→Wasted）</item>
/// <item>ReclaimHead（头部删段→ShrinkHead）</item>
/// <item>ReclaimTail（尾截断→ShrinkTail）</item>
/// </list>
/// <para>★ MemoryDiagnoser 测分配（lease 对象 + ExtentLease 数组）——池化前后对比的关键数据。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*LeaseProtocol*"</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class LeaseProtocolBench
{
    private SegmentTable _table = null!;
    private const int GrowthLimit = 128 * 1024 * 1024;   // 128MB 段——实际生产大小
    private const int AppendUnit = 4 * 1024;             // 4KB——典型记录大小
    private const int WriteUnit = 4 * 1024;              // 4KB 覆写

    /// <summary>lease 工厂模式——Default(new) vs Pooled(池化) 对比 lease 对象分配开销。</summary>
    [Params("Default", "Pooled")]
    public string FactoryMode { get; set; } = "Default";

    [GlobalSetup]
    public void Setup()
    {
        var factory = FactoryMode == "Pooled" ? LeaseFactory.Pooled : LeaseFactory.Default;
        _table = new SegmentTable(
            new SegmentTableSettings(GrowthLimit, 0, IndexCapacity: 64),
            factory);
    }

    [GlobalCleanup]
    public void Cleanup() => _table.Dispose();

    // ════════════════════════════════════════════════════════════
    //  Append——CAS 推尾热路径（最频繁的 lease 操作）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Append lease 创建 + Commit——测最频繁的热路径单次延迟 + 分配。
    /// 每次：AppendLease(4KB) + Commit（推 AllocatedTail + CommittedTail + CompleteAndMerge）。
    /// </summary>
    [Benchmark(Description = "AppendLease.Create+Commit", Baseline = true)]
    public long AppendLeaseCreateCommit()
    {
        using var lease = _table.AppendLease(AppendUnit);
        lease.Commit();
        return lease.Start.Offset;
    }

    /// <summary>
    /// AllocateLease（无 lease 对象，直接 CAS 推两个水位 + MarkWasted）——测纯 CAS 路径。
    /// 与 AppendLease 对比，差值就是 lease 对象 + ExtentLease 数组的开销。
    /// </summary>
    [Benchmark(Description = "AllocateLease.CAS")]
    public long AllocateLeaseCas()
    {
        var (start, _) = _table.AllocateLease(AppendUnit);
        return start.Offset;
    }

    // ════════════════════════════════════════════════════════════
    //  Write——覆写已提交区间（状态流转，不推水位）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Write lease 覆写——先预填一批已提交数据，每次 Benchmark 调用覆写一块。
    /// 测 CompleteAndMerge（区间合并）+ InsertUnsafe 拆分开销。
    /// </summary>
    private long _writeCursor;
    [Benchmark(Description = "WriteLease.Overwrite")]
    public long WriteLeaseOverwrite()
    {
        // 在已提交范围内轮转覆写（不推水位，纯状态流转）
        var start = new LogicalAddress(0, (int)(_writeCursor % (AppendUnit * 64)));
        using var lease = _table.WriteLease(start, WriteUnit);
        lease.Commit();
        _writeCursor += WriteUnit;
        return lease.Start.Offset;
    }

    // ════════════════════════════════════════════════════════════
    //  Reclaim——中间打洞（Wasted 空洞）
    // ════════════════════════════════════════════════════════════

    private long _reclaimCursor;
    [Benchmark(Description = "ReclaimLease.PunchHole")]
    public long ReclaimLeasePunchHole()
    {
        // 在已提交范围内轮转打洞（标 Wasted）
        var from = new LogicalAddress(0, (int)(_reclaimCursor % (AppendUnit * 64)));
        var to = new LogicalAddress(0, (int)(from.Offset + WriteUnit));
        using var lease = _table.ReclaimLease(from, to);
        lease.Commit();   // → Wasted
        // 立即用 Write 填回（让区间变回 Committed，支持轮转）
        _table.WriteLease(from, WriteUnit).Commit();
        _reclaimCursor += WriteUnit;
        return from.Offset;
    }

    // ════════════════════════════════════════════════════════════
    //  ReclaimTail——尾截断（退水位，Append 的逆）
    // ════════════════════════════════════════════════════════════

    [Benchmark(Description = "ReclaimTail+ReAppend")]
    public long ReclaimTailAndReAppend()
    {
        // Append 一块，立即 ReclaimTail 截回去——测 ShrinkTail（RetreatOffset + Retreat CAS）开销
        using var append = _table.AppendLease(AppendUnit);
        append.Commit();
        var tail = append.Start;   // 截断到 Append 之前
        _table.ReclaimTailLease(tail).Commit();
        return tail.Offset;
    }

    // ════════════════════════════════════════════════════════════
    //  诊断输出——每轮迭代后输出水位状态
    // ════════════════════════════════════════════════════════════

    [IterationSetup]
    public void IterationSetup()
    {
        // 每轮重置表（避免水位无限增长影响测量）
        _table.Dispose();
        var factory = FactoryMode == "Pooled" ? LeaseFactory.Pooled : LeaseFactory.Default;
        _table = new SegmentTable(
            new SegmentTableSettings(GrowthLimit, 0, IndexCapacity: 64),
            factory);
        // 为 Write/Reclaim 预热已提交空间
        for (var i = 0; i < 64; i++)
        {
            using var l = _table.AppendLease(AppendUnit);
            l.Commit();
        }
        _writeCursor = 0;
        _reclaimCursor = 0;
    }
}

/// <summary>
/// lease 协议并发吞吐基准——多线程并发 Append 测吞吐量（ops/sec）。
/// <para>★ 验证 CAS 双尾水位在并发下的扩展性（核心数越多吞吐是否线性增长）。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*LeaseConcurrentThroughput*"</para>
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 4)]
public class LeaseConcurrentThroughputBench
{
    private SegmentTable _table = null!;
    private const int GrowthLimit = 256 * 1024 * 1024;
    private const int UnitLen = 4 * 1024;

    [Params(1, 2, 4, 8)]
    public int Threads { get; set; }

    /// <summary>每线程的操作数（总操作 = Threads × PerThread）。</summary>
    private const int PerThread = 10_000;

    [IterationSetup]
    public void Setup()
    {
        _table = new SegmentTable(
            new SegmentTableSettings(GrowthLimit, 0, IndexCapacity: 256, SpinMilliseconds: 60_000),
            LeaseFactory.Pooled);
    }

    [IterationCleanup]
    public void Teardown() => _table.Dispose();

    [Benchmark(Description = "Append.Concurrent.Throughput")]
    public long ConcurrentAppendThroughput()
    {
        long total = 0;
        Parallel.For(0, Threads, _ =>
        {
            for (var i = 0; i < PerThread; i++)
            {
                using var lease = _table.AppendLease(UnitLen);
                lease.Commit();
                Interlocked.Increment(ref total);
            }
        });
        return total;
    }
}
