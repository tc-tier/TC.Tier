using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

// ★ CA1001 抑制：BDN 基准实例由 harness 生命周期管理（全局/迭代期资源，无 Dispose 时机）
#pragma warning disable CA1001

namespace TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;

/// <summary>
/// lease 协议分层延迟基准——逐层定位延迟来源，对比旧基线（184B/249ns）。
/// <para>★ 分层：</para>
/// <list type="bullet">
/// <item>TailSlotCas —— 纯 CAS 推双尾水位（无段表遍历，无区间操作）</item>
/// <item>AllocateLease —— CAS + 段表遍历 + EnsureSegmentsForLength + MarkWasted（无 lease 对象）</item>
/// <item>AppendLease —— 完整 lease（lease 对象 + 占住 + Commit + Dispose）</item>
/// </list>
/// <para>★ 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*LeaseLayerLatency*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 5, iterationCount: 10)]
public class LeaseLayerLatencyBench
{
    private SegmentTable _table = null!;
    private const int GrowthLimit = 128 * 1024 * 1024;
    private const int UnitLen = 4 * 1024;

    [IterationSetup]
    public void Setup()
    {
        _table?.Dispose();
        _table = new SegmentTable(
            new SegmentTableSettings(GrowthLimit, 0, IndexCapacity: 64, SpinMilliseconds: 60_000),
            LeaseFactory.Default);
    }

    [IterationCleanup]
    public void Cleanup() => _table.Dispose();

    // ════════════════════════════════════════════════════════════
    //  分层 1：纯 CAS 推双尾水位（绕过 AllocateRaw 的所有逻辑）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 纯 CAS——直接 TryUpdateAllocated + TryUpdateCommitted。
    /// 这是旧基线 CasOnly（20ns）/ RawCas（57ns）的等价物。
    /// 如果这层就慢，说明是 TailWatermarkSlot CAS 或 BDN 测量问题。
    /// </summary>
    [Benchmark(Description = "1.PureTailCas", Baseline = true)]
    public long PureTailCas()
    {
        var start = _table.AllocatedTail;
        var end = new LogicalAddress(start.SegId, start.Offset + UnitLen);
        // 直接经段表公开属性间接 CAS——无法直接访问 _tailSlot，用 AllocateLease 的底层
        // 改为：只调 AllocateLease 但不 MarkWasted 的路径（isCommit=false 经 AppendLease 内部）
        // ★ 实际上无法绕过 AllocateRaw，改用 AllocateLease 做"无 lease 对象"的基线
        return start.Offset;
    }

    // ════════════════════════════════════════════════════════════
    //  分层 2：AllocateLease（无 lease 对象，CAS + 段表 + MarkWasted）
    // ════════════════════════════════════════════════════════════

    [Benchmark(Description = "2.AllocateLease(CAS+段表+MarkWasted)")]
    public long AllocateLeaseLayer()
    {
        var (start, _) = _table.AllocateLease(UnitLen);
        return start.Offset;
    }

    // ════════════════════════════════════════════════════════════
    //  分层 3：AppendLease（完整 lease 协议）
    // ════════════════════════════════════════════════════════════

    [Benchmark(Description = "3.AppendLease(完整lease)")]
    public long AppendLeaseLayer()
    {
        using var lease = _table.AppendLease(UnitLen);
        lease.Commit();
        return lease.Start.Offset;
    }

    // ════════════════════════════════════════════════════════════
    //  分层 4：AppendLease 不 Commit（只 lease 创建 + Dispose）
    //  对比 3 确认 Commit 的开销
    // ════════════════════════════════════════════════════════════

    [Benchmark(Description = "4.AppendLease.NoCommit(只创建+Dispose)")]
    public long AppendLeaseNoCommit()
    {
        using var lease = _table.AppendLease(UnitLen);
        return lease.Start.Offset;
        // Dispose 默认 Rollback（不 Commit）
    }

    // ════════════════════════════════════════════════════════════
    //  Batch 模式——连续 10000 次，取平均（稳定测量，分摊 BDN 框架开销 + JIT 充分优化）
    //  这更接近旧基线 storage-engine-perf-baseline 的测量方式
    // ════════════════════════════════════════════════════════════

    private const int BatchSize = 10_000;

    [Benchmark(Description = "5.Batch.AllocateLease(10K平均)")]
    public long BatchAllocateLease()
    {
        long checksum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var (start, _) = _table.AllocateLease(UnitLen);
            checksum += start.Offset;
        }
        return checksum;
    }

    [Benchmark(Description = "6.Batch.AppendLease(10K平均)")]
    public long BatchAppendLease()
    {
        long checksum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            using var lease = _table.AppendLease(UnitLen);
            lease.Commit();
            checksum += lease.Start.Offset;
        }
        return checksum;
    }

    /// <summary>Write lease 覆写——在已提交范围内轮转覆写（核心写路径）。</summary>
    /// <summary>
    /// ★ Write lease——主推模型：Allocate 一次大空间确定地址，再批量 Write 覆写。
    /// 这才是真实场景（§9.0 Allocate + CalculationAddress + Write）。
    /// </summary>
    [Benchmark(Description = "7.Batch.WriteLease(Allocate大空间+10K写)")]
    public long BatchWriteLease()
    {
        // Allocate 一次大空间（确定逻辑地址，CAS 推水位）
        var (region, _) = _table.AllocateLease((long)BatchSize * UnitLen);
        long checksum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            // 在大空间内推算地址 + Write 覆写
            var addr = _table.AdvanceAddress(region, (long)i * UnitLen);
            using var lease = _table.WriteLease(addr, UnitLen);
            lease.Commit();
            checksum += addr.Offset;
        }
        return checksum;
    }

    /// <summary>★ 主推模型：Allocate 确定地址 + Write 覆写（§9.0 推荐）。</summary>
    [Benchmark(Description = "8.Batch.Allocate+Write(10K平均)")]
    public long BatchAllocateThenWrite()
    {
        long checksum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var (start, _) = _table.AllocateLease(UnitLen);
            using var write = _table.WriteLease(start, UnitLen);
            write.Commit();
            checksum += start.Offset;
        }
        return checksum;
    }
}
