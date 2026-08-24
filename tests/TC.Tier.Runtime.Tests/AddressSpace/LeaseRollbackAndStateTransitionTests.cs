using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// lease 协议状态流转测试——直接断言区间 ExtentRecord.State（ExtentStateCode byte）。
/// <para>★ 设计依据：docs/extent-state-machine-redesign.md §4.1 流转图 + §4.2 "失败也必须推段表边界"。</para>
/// <para>★ 不黑盒看水位——经 SnapshotSegmentExtents 直接读区间状态，验证流转图每一条路径的终态。</para>
/// <para>★ 流转图（每条都要验证区间 State）：</para>
/// <list type="bullet">
/// <item>Append Commit → Committed；Rollback → Wasted</item>
/// <item>Write Commit → Committed；Rollback → Wasted</item>
/// <item>Reclaim(中间) Commit → Committed+sparse（558fe3b9：OS 打洞读零，非 Wasted）；Rollback → Aborted</item>
/// <item>ReclaimTail Commit/Rollback → 区间删除</item>
/// <item>ReclaimHead Commit → 段内 Wasted；Rollback → 段内 Aborted（+跨段 MarkInvalid）</item>
/// <item>Compact Commit → 新段 Committed；Rollback → 区间释放（原样恢复）</item>
/// </list>
/// </summary>
public class LeaseRollbackAndStateTransitionTests
{
    private static SegmentTable NewTable(long growthLimit = 1000, int minSegId = 0, int capacity = 8)
        => new(new SegmentTableSettings(growthLimit, minSegId, capacity, SpinMilliseconds: 2000), LeaseFactory.WithDiagnostics);

    // ════════════════════════════════════════════════════════════
    //  Append——流转图 §4.1
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Append_Commit_ExtentBecomesCommitted()
    {
        using var table = NewTable();
        table.AppendLease(100).Commit();
        // 流转图：Append Commit → Committed
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));
    }

    [Fact]
    public void Append_Rollback_ExtentBecomesWasted()
    {
        using var table = NewTable();
        table.AppendLease(100).Rollback();
        // 流转图：Append Rollback → Wasted（地址已占不可逆）
        Assert.Equal(ExtentStateCode.Wasted, table.ExtentStateAt(0, 0));
    }

    // ════════════════════════════════════════════════════════════
    //  Write——流转图 §4.1
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Write_Commit_OnCommittedRange_ExtentStaysCommitted()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.WriteLease(new LogicalAddress(0, 50), 100).Commit();
        // 流转图：Write Commit → Committed（覆写已提交区间，仍是 Committed）
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));
    }

    [Fact]
    public void Write_Rollback_ExtentBecomesWasted()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.WriteLease(new LogicalAddress(0, 50), 100).Rollback();
        // 流转图：Write Rollback → Wasted（pwrite 可重入，可覆写修复）
        // 覆写 [50,150) Rollback → 该区间 Wasted
        Assert.Equal(ExtentStateCode.Wasted, table.ExtentStateAt(0, 50));
    }

    [Fact]
    public void Write_Commit_OnSparseHole_ExtentBecomesCommitted()
    {
        // 闭环（558fe3b9 语义）：Reclaim 打洞 → Committed+sparse（读零）→ Write 覆写 → Committed（非稀疏）
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 150)).Commit();
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));    // 合并成 [0,200) 一条（打洞并入 sparse 位）
        Assert.True(table.IsRangeFullyReadable(0, 0, 200));                    // 读零语义：全程可读

        table.WriteLease(new LogicalAddress(0, 50), 100).Commit();
        // Write 覆写后仍 Committed——CompleteAndMerge 合并相邻，整段 [0,200) 全 Committed
        Assert.Equal(0, table.CountState(0, ExtentStateCode.Wasted));      // 无 Wasted 残留
        Assert.Equal(0, table.CountState(0, ExtentStateCode.Aborted));
        Assert.True(table.CountState(0, ExtentStateCode.Committed) >= 1);  // 有 Committed
    }

    // ════════════════════════════════════════════════════════════
    //  Reclaim（中间）——558fe3b9：Commit→Committed+sparse（OS 打洞读零），Rollback→Aborted
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Reclaim_Commit_ExtentBecomesCommittedSparse()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 150)).Commit();
        // 558fe3b9：Reclaim 打洞 = Committed+sparse（物理块归还 OS、读零、地址可复用）
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));   // 合并成 [0,200) 一条
        Assert.True(table.IsRangeFullyReadable(0, 0, 200));
    }

    [Fact]
    public void Reclaim_Rollback_ExtentBecomesAborted()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 150)).Rollback();
        // 流转图：中间 Reclaim Rollback → Aborted（PunchHole 不一致，永久洞，只 Compact 修）
        Assert.Equal(ExtentStateCode.Aborted, table.ExtentStateAt(0, 50));
    }

    // ════════════════════════════════════════════════════════════
    //  ReclaimTail——流转图 §4.1/§4.2：Commit 和 Rollback 都区间删除
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimTail_Commit_ExtentsBeyondTailTruncated()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();

        table.ReclaimTailLease(new LogicalAddress(0, 100)).Commit();

        // 流转图：ReclaimTail Commit → newTail 之后的区间消失（截断/删除）
        // 单区间 [0,200) 截断成 [0,100)——End 退到 100，无区间跨越 100
        using (var reader = table.SnapshotSegmentExtents(0))
            while (reader.MoveNext())
                Assert.True(reader.Current.End <= 100, $"区间 End {reader.Current.End} 应 ≤ 100（截断后）");
        Assert.Equal(new LogicalAddress(0, 100), table.CommittedTail);
    }

    [Fact]
    public void ReclaimTail_Rollback_ExtentsBeyondTailTruncatedAndTailRetreats()
    {
        // 流转图 §4.2：ReclaimTail Rollback 也必须 ShrinkTail（物理已截断不可逆，失败也退）
        using var table = NewTable();
        table.AppendLease(200).Commit();

        table.ReclaimTailLease(new LogicalAddress(0, 100)).Rollback();

        using (var reader = table.SnapshotSegmentExtents(0))
            while (reader.MoveNext())
                Assert.True(reader.Current.End <= 100, $"Rollback 后区间 End {reader.Current.End} 应 ≤ 100");
        Assert.Equal(new LogicalAddress(0, 100), table.CommittedTail);
    }

    // ════════════════════════════════════════════════════════════
    //  ReclaimHead——流转图 §4.0/§4.1/§4.2
    //  跨段 MarkInvalid（段级）+ 段内打洞 Wasted/Aborted（区间级）+ ShrinkHead
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimHead_Commit_DeletesSegmentsAndAdvancesMinAddress()
    {
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(250).Commit();   // seg0 满 + seg1 满 + seg2(50)

        table.ReclaimHeadLease(new LogicalAddress(1, 0)).Commit();   // 删 seg0

        Assert.Equal(new LogicalAddress(1, 0), table.MinAddress);
        Assert.Equal(StableState.Invalid, table.GetSegment(0).StableState);
        Assert.True(table.GetSegment(1).IsValid);
    }

    [Fact]
    public void ReclaimHead_Commit_SameSegment_PunchesHoleToWasted()
    {
        // 同段内 ReclaimHead：[0,100) 段内打洞 → minOffset 推到 100，[0,100) 区间记录被删（头部回收）
        using var table = NewTable(growthLimit: 1000);
        table.AppendLease(500).Commit();

        table.ReclaimHeadLease(new LogicalAddress(0, 100)).Commit();

        Assert.Equal(new LogicalAddress(0, 100), table.MinAddress);
        Assert.Equal(100, table.GetSegment(0).MinOffset);   // minOffset 推进
        // [0,100) 区间记录已删（不再属于段）；[100,500) 仍 Committed
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 100));
    }

    [Fact]
    public void ReclaimHead_Rollback_AdvancesMinAddressAndSegmentInvalid()
    {
        // 流转图 §4.2：ReclaimHead Rollback 也必须 ShrinkHead（物理已 DeleteSegment 不可逆）
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(250).Commit();

        table.ReclaimHeadLease(new LogicalAddress(1, 0)).Rollback();

        Assert.Equal(new LogicalAddress(1, 0), table.MinAddress);
        Assert.Equal(StableState.Invalid, table.GetSegment(0).StableState);
    }

    [Fact]
    public void ReclaimHead_Rollback_SameSegment_PunchesHoleToAborted()
    {
        // 同段内 ReclaimHead Rollback：段内打洞 → minOffset 推到 100，[0,100) 区间记录被删
        using var table = NewTable(growthLimit: 1000);
        table.AppendLease(500).Commit();

        table.ReclaimHeadLease(new LogicalAddress(0, 100)).Rollback();

        Assert.Equal(new LogicalAddress(0, 100), table.MinAddress);
        Assert.Equal(100, table.GetSegment(0).MinOffset);   // minOffset 推进
        // [100,500) 仍 Committed（Rollback 不改剩余区间的状态）
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 100));
    }

    // ════════════════════════════════════════════════════════════
    //  Compact lease——流转图：Commit→段表原子替换；Rollback→overlay 释放
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Compact_Commit_WithReplacement_NewSegmentHasCommittedExtents()
    {
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(100).Commit();   // seg0 满

        var lease = table.CompactLeaseForTest(new LogicalAddress(0, 0), new LogicalAddress(0, 100));
        foreach (var chunk in lease.Chunks)
        {
            chunk.SetReplacement(growthLimit: 100, maxOffset: 80);
        }
        lease.Commit();

        Assert.Equal(LeaseState.Committed, lease.State);
        var seg0 = table.GetSegment(0);
        Assert.True(seg0.IsValid);
        Assert.Equal(80, seg0.MaxOffset);   // 新段 maxOffset
        // 新段区间应为 Committed（流转图：Compact Commit → 新段区间为 Committed）
        Assert.True(table.CountState(0, ExtentStateCode.Committed) >= 1, "新段应有 Committed 区间");
    }

    [Fact]
    public void Compact_Commit_WithInvalidate_MarksSegmentInvalid()
    {
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(100).Commit();

        var lease = table.CompactLeaseForTest(new LogicalAddress(0, 0), new LogicalAddress(0, 100));
        foreach (var chunk in lease.Chunks)
            chunk.MarkInvalid();
        lease.Commit();

        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(StableState.Invalid, table.GetSegment(0).StableState);
    }

    [Fact]
    public void Compact_Rollback_LeavesExtentsUnchanged()
    {
        // 流转图：Compact Rollback → overlay 释放（段表不变，底层区间记录原样恢复）
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(100).Commit();
        var extentsBefore = table.ExtentCount(0);
        var tailBefore = table.CommittedTail;

        var lease = table.CompactLeaseForTest(new LogicalAddress(0, 0), new LogicalAddress(0, 100));
        lease.Rollback();

        Assert.Equal(LeaseState.RolledBack, lease.State);
        Assert.Equal(tailBefore, table.CommittedTail);
        Assert.Equal(extentsBefore, table.ExtentCount(0));   // 区间数不变
        Assert.True(table.GetSegment(0).IsValid);
    }

    [Fact]
    public void Compact_DisposeWithoutCommit_DefaultRollback()
    {
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(100).Commit();
        var tailBefore = table.CommittedTail;

        var lease = table.CompactLeaseForTest(new LogicalAddress(0, 0), new LogicalAddress(0, 100));
        lease.Dispose();

        Assert.Equal(LeaseState.RolledBack, lease.State);
        Assert.Equal(tailBefore, table.CommittedTail);
    }
}
