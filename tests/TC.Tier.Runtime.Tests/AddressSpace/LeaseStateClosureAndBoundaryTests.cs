using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// lease 状态闭环 + 占用规则矩阵 + 边界值测试。
/// <para>★ 设计依据：</para>
/// <para>  §7.2 占用规则矩阵：Committed/Wasted 所有 kind 可占；Aborted 只 Compact 能占；在途态排他。</para>
/// <para>  §4.3 状态全集：Committed 可读；Wasted/Aborted/在途不可读。</para>
/// <para>  §3.1 中间态排他：lease 占住期间区间谁都不能占（包括同 kind）。</para>
/// </summary>
public class LeaseStateClosureAndBoundaryTests
{
    private static SegmentTable NewTable(long growthLimit = 1000, int minSegId = 0, int capacity = 8)
        => new(new SegmentTableSettings(growthLimit, minSegId, capacity, SpinMilliseconds: 2000), LeaseFactory.WithDiagnostics);

    /// <summary>尝试占 [start,end)——成功返回 true，被占/不可占返回 false（AcquireExtent 超时返回 false）。</summary>
    private static bool TryAcquire(SegmentTable table, LogicalAddress start, long length)
    {
        try
        {
            using var lease = table.WriteLease(start, length);
            return lease.State == LeaseState.Active;   // 占住成功 = Active
        }
        catch (TimeoutException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    // ════════════════════════════════════════════════════════════
    //  §7.2 占用规则矩阵——谁能占谁
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Occupiable_CommittedCanBeAcquiredByWrite()
    {
        // Committed → Write 可占（覆写）
        using var table = NewTable();
        table.AppendLease(200).Commit();
        Assert.True(TryAcquire(table, new LogicalAddress(0, 50), 100));
    }

    [Fact]
    public void Occupiable_WastedCanBeAcquiredByWrite()
    {
        // Wasted → Write 可占（填洞）——流转图 §7.2
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 150)).Commit();   // → Wasted
        Assert.True(TryAcquire(table, new LogicalAddress(0, 50), 100));
    }

    [Fact]
    public void Occupiable_InFlightIsExclusive_BlocksOtherLease()
    {
        // §3.1 中间态排他：lease 占住期间，同区间不能再占（包括同 kind）
        using var table = NewTable();
        table.AppendLease(200).Commit();
        // 第一个 lease 占住 [50,150)，不 Commit
        var lease1 = table.WriteLease(new LogicalAddress(0, 50), 100);
        try
        {
            // 第二个 lease 占同样区间——应失败（在途态排他）
            Assert.False(TryAcquire(table, new LogicalAddress(0, 50), 100));
            // 不同区间仍可占
            Assert.True(TryAcquire(table, new LogicalAddress(0, 150), 50));
        }
        finally { lease1.Dispose(); }
    }

    [Fact]
    public void Occupiable_PartialOverlap_BlocksLease()
    {
        // 部分重叠也排他：lease 占 [50,150)，[0,100) 与之重叠 → 不能占
        using var table = NewTable();
        table.AppendLease(200).Commit();
        var lease1 = table.WriteLease(new LogicalAddress(0, 50), 100);   // [50,150)
        try
        {
            Assert.False(TryAcquire(table, new LogicalAddress(0, 0), 100));    // [0,100) 与 [50,150) 重叠
        }
        finally { lease1.Dispose(); }
    }

    [Fact]
    public void Occupiable_AfterLeaseCommit_CanReacquire()
    {
        // lease Commit 后区间变 Committed，可再次被占
        using var table = NewTable();
        table.AppendLease(200).Commit();
        var lease1 = table.WriteLease(new LogicalAddress(0, 50), 100);
        lease1.Commit();
        // Commit 后再占——成功
        Assert.True(TryAcquire(table, new LogicalAddress(0, 50), 100));
    }

    // ════════════════════════════════════════════════════════════
    //  §4.3 可读性——状态机服务读者
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Readable_CommittedRange_IsFullyReadable()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        Assert.True(table.IsRangeFullyReadable(0, 0, 200));
    }

    [Fact]
    public void Readable_WastedRange_NotReadable()
    {
        // 558fe3b9：Wasted 只来自失败写（Reclaim 打洞是 Committed+sparse 可读零）
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.AppendLease(100).Rollback();   // 失败写 → Wasted
        Assert.False(table.IsRangeFullyReadable(0, 0, 300));   // 含 Wasted 垃圾
    }

    [Fact]
    public void Readable_AbortedRange_NotReadable()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 150)).Rollback();   // → Aborted
        Assert.False(table.IsRangeFullyReadable(0, 0, 200));
    }

    [Fact]
    public void Readable_InFlightRange_NotReadable()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        var lease = table.WriteLease(new LogicalAddress(0, 50), 100);   // 占住，在途
        try
        {
            Assert.False(table.IsRangeFullyReadable(0, 0, 200));
        }
        finally { lease.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    //  边界值——零长度、空区间、单/多段、精确填满
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void AppendLease_ZeroLength_ReturnsEmptyLease()
    {
        using var table = NewTable();
        var lease = table.AppendLease(0);
        Assert.Equal(0, lease.Length);
        Assert.Equal(0, lease.ChunkCount);
        lease.Dispose();
    }

    [Fact]
    public void WriteLease_ZeroLength_ReturnsEmptyLease()
    {
        using var table = NewTable();
        table.AppendLease(100).Commit();
        var lease = table.WriteLease(new LogicalAddress(0, 50), 0);
        Assert.Equal(0, lease.Length);
        Assert.Equal(0, lease.ChunkCount);
        lease.Dispose();
    }

    [Fact]
    public void ReclaimLease_SameFromTo_ThrowsOrEmpty()
    {
        using var table = NewTable();
        table.AppendLease(100).Commit();
        // from == to：ValidateRange 抛 ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 50)));
    }

    [Fact]
    public void AppendLease_ExactFillSegment_BoundaryCorrect()
    {
        // 精确填满一段：AppendLease(100) growthLimit=100 → 段满
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(100);
        Assert.Equal(new LogicalAddress(0, 0), lease.Start);
        Assert.Equal(1, lease.ChunkCount);   // 单段，不跨
        lease.Commit();
        // ★ 段满后 CommittedTail 停驻段末边界 (0,100)——区间统一（旧哨兵形态为 (1,0)）
        Assert.Equal(new LogicalAddress(0, 100), table.CommittedTail);
        Assert.True(table.GetSegment(0).IsFull);
    }

    [Fact]
    public void AppendLease_CrossSegmentBoundary_OneExtraByte()
    {
        // 跨段 1 字节：100+1 跨到 seg1
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(101);
        Assert.Equal(2, lease.ChunkCount);   // seg0(100) + seg1(1)
    }

    [Fact]
    public void ReclaimHead_SameSegment_OffsetOnly_NoCrossSegment()
    {
        // 同段 ReclaimHead：只推 offset，不跨段删
        using var table = NewTable(growthLimit: 1000);
        table.AppendLease(500).Commit();
        var segCountBefore = table.SegCount;

        table.ReclaimHeadLease(new LogicalAddress(0, 100)).Commit();

        Assert.Equal(new LogicalAddress(0, 100), table.MinAddress);
        Assert.Equal(segCountBefore, table.SegCount);   // 段数不变（同段内）
    }

    [Fact]
    public void ReclaimTail_ToSegmentBoundary_CrossesSegment()
    {
        // ReclaimTail 到段边界：跨段截断
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(250).Commit();   // 跨 3 段，CommittedTail=(2,50)

        table.ReclaimTailLease(new LogicalAddress(1, 0)).Commit();   // 截到 (1,0)

        Assert.Equal(new LogicalAddress(1, 0), table.CommittedTail);
    }

    // ════════════════════════════════════════════════════════════
    //  闭环——完整生命周期（Append→Write→Reclaim→再 Write 填洞）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Lifecycle_AppendWriteReclaimRewrite_AllStatesCorrect()
    {
        using var table = NewTable();
        // 1. Append 提交
        table.AppendLease(300).Commit();
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));

        // 2. Write 覆写中间
        table.WriteLease(new LogicalAddress(0, 100), 50).Commit();
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));

        // 3. Reclaim 中间打洞 → Committed+sparse（558fe3b9：读零，仍可读）
        table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 100)).Commit();
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));   // 合并成一条（打洞并入 sparse）
        Assert.True(table.IsRangeFullyReadable(0, 0, 300));   // 读零语义：全程可读

        // 4. Write 填洞 → Committed（非稀疏）
        table.WriteLease(new LogicalAddress(0, 50), 50).Commit();
        // 全可读
        Assert.True(table.IsRangeFullyReadable(0, 0, 300));
    }
}
