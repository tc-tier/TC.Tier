using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace.Leases;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// SegmentTable lease 协议完整覆盖测试——验证 5 种 lease 的创建、三阶段（占住/锁外IO/提交）、
/// Commit 段表副作用、Rollback 终态、跨段 chunk 遍历、与 Append 的交互。
/// <para>★ 纯内存段表（handler=null），单线程，避开段锁（LockWord 时代）死锁/OnSegmentReclaim throw 路径，
///   聚焦验证 lease 协议本身的内存状态流转正确性。</para>
/// <para>★ 设计依据：docs/extent-state-machine-redesign.md §4.1 流转图 + §4.3 状态全集。</para>
/// </summary>
public class SegmentTableLeaseProtocolTests
{
    private static SegmentTable NewTable(long growthLimit = 1000, int minSegId = 0, int capacity = 8)
        => new(new SegmentTableSettings(growthLimit, minSegId, capacity, SpinMilliseconds: 2000), LeaseFactory.WithDiagnostics);

    // ════════════════════════════════════════════════════════════
    //  Append（已有入口，此处作为其他 lease 的前置 + 基线验证）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void AppendLease_Commit_AdvancesCommittedTail()
    {
        using var table = NewTable();
        using var lease = table.AppendLease(100);
        Assert.Equal(LeaseState.Active, lease.State);
        Assert.Equal(new LogicalAddress(0, 0), lease.Start);
        Assert.Equal(new LogicalAddress(0, 100), lease.End);
        lease.Commit();
        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(new LogicalAddress(0, 100), table.CommittedTail);
    }

    [Fact]
    public void AppendLease_Rollback_MarksWastedStillAdvancesCommittedTail()
    {
        // 设计文档 §4.1：Append Rollback → Wasted，但地址已占不可逆，仍推 CommittedTail
        using var table = NewTable();
        using var lease = table.AppendLease(100);
        lease.Rollback();
        Assert.Equal(LeaseState.RolledBack, lease.State);
        Assert.Equal(new LogicalAddress(0, 100), table.CommittedTail);
    }

    [Fact]
    public void AppendLease_MultiChunk_PerChunkCommitDoesNotAdvanceCommittedTail()
    {
        // 跨段 lease：growthLimit=100，AppendLease(250) 跨 seg0+seg1+seg2（≥2 chunk）
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(250);
        var committedBefore = table.CommittedTail;
        var leaseEnd = lease.End;

        var tails = new List<LogicalAddress>();
        var iter = lease.GetEnumerator();
        while (iter.MoveNext())
        {
            iter.CommitCurrent();          // 区间提交（模拟 CopyChunks 逐 chunk Write 后 Commit）
            tails.Add(table.CommittedTail);
        }

        Assert.True(tails.Count >= 2, "跨段 lease 应产生多 chunk");
        // ★ 前 N-1 chunk：区间提交只推段内 MaxOffset，不推全局 CommittedTail
        for (int i = 0; i < tails.Count - 1; i++)
            Assert.Equal(committedBefore, tails[i]);
        // ★ 最后 chunk 触发整体提交（OnAllCommitted）→ CommittedTail 推到 lease.End
        Assert.Equal(leaseEnd, tails[^1]);
        Assert.Equal(LeaseState.Committed, lease.State);
    }

    // ════════════════════════════════════════════════════════════
    //  Write lease——覆写已提交区间
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void WriteLease_Create_ReturnsActiveLease()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();   // 先制造已提交区间

        using var lease = table.WriteLease(new LogicalAddress(0, 50), 100);
        Assert.Equal(LeaseState.Active, lease.State);
        Assert.Equal(new LogicalAddress(0, 50), lease.Start);
    }

    [Fact]
    public void WriteLease_Commit_DoesNotAdvanceCommittedTail()
    {
        // 设计文档 §4.1：Write Commit → Committed，整体级无段表副作用（不推 CommittedTail）
        using var table = NewTable();
        table.AppendLease(200).Commit();
        var tailBefore = table.CommittedTail;

        using var lease = table.WriteLease(new LogicalAddress(0, 50), 100);
        lease.Commit();

        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(tailBefore, table.CommittedTail);   // 不前进
    }

    [Fact]
    public void WriteLease_Rollback_DoesNotAdvanceCommittedTail()
    {
        // 设计文档 §4.1：Write Rollback → Wasted（可重入修复），整体级无段表副作用
        using var table = NewTable();
        table.AppendLease(200).Commit();
        var tailBefore = table.CommittedTail;

        using var lease = table.WriteLease(new LogicalAddress(0, 50), 100);
        lease.Rollback();

        Assert.Equal(LeaseState.RolledBack, lease.State);
        Assert.Equal(tailBefore, table.CommittedTail);   // Rollback 不应前进水位
    }

    [Fact]
    public void WriteLease_BeyondCommittedTail_Throws()
    {
        using var table = NewTable();
        table.AppendLease(100).Commit();   // CommittedTail = (0,100)

        // 覆写 [50, 150) 超出 CommittedTail
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            table.WriteLease(new LogicalAddress(0, 50), 100));   // end=(0,150) > (0,100)
    }

    [Fact]
    public void WriteLease_NegativeLength_Throws()
    {
        using var table = NewTable();
        table.AppendLease(100).Commit();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            table.WriteLease(new LogicalAddress(0, 0), -1));
    }

    // ════════════════════════════════════════════════════════════
    //  Reclaim lease（中间回收）——Commit → Wasted，Rollback → Aborted
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimLease_Commit_MarksWastedNoTableChange()
    {
        // 设计文档 §4.1：中间 Reclaim Commit → Wasted（空洞，可被 Write 覆写）。
        //   段表不变、段水位不变。
        using var table = NewTable();
        table.AppendLease(200).Commit();
        var tailBefore = table.CommittedTail;

        using var lease = table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 150));
        lease.Commit();

        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(tailBefore, table.CommittedTail);   // 段表水位不变
    }

    [Fact]
    public void ReclaimLease_Rollback_NoTableChange()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        var tailBefore = table.CommittedTail;

        using var lease = table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 150));
        lease.Rollback();

        Assert.Equal(LeaseState.RolledBack, lease.State);
        Assert.Equal(tailBefore, table.CommittedTail);
    }

    [Fact]
    public void ReclaimLease_FromGreaterThanTo_Throws()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            table.ReclaimLease(new LogicalAddress(0, 150), new LogicalAddress(0, 50)));
    }

    // ════════════════════════════════════════════════════════════
    //  ReclaimHead lease——头部删段，ShrinkHead 推 MinAddress
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimHeadLease_Commit_AdvancesMinAddress()
    {
        // 设计文档 §4.0/§5：ReclaimHead = 跨段 MarkInvalid + ShrinkHead 推 MinAddress
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(250).Commit();   // 跨段：seg0(0-100) + seg1(0-50)，CommittedTail=(1,50)

        var to = new LogicalAddress(1, 0);   // 删 seg0 整段，MinAddress 推到 (1,0)
        using var lease = table.ReclaimHeadLease(to);
        lease.Commit();

        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(to, table.MinAddress);
    }

    [Fact]
    public void ReclaimHeadLease_SameSegment_AdvancesMinAddressOffset()
    {
        // 段内头部截断：MinAddress 在同段内推进 offset
        using var table = NewTable(growthLimit: 1000);
        table.AppendLease(500).Commit();

        var to = new LogicalAddress(0, 100);
        using var lease = table.ReclaimHeadLease(to);
        lease.Commit();

        Assert.Equal(to, table.MinAddress);
    }

    [Fact]
    public void ReclaimHeadLease_AtOrBelowMinAddress_Throws()
    {
        using var table = NewTable();
        table.AppendLease(100).Commit();
        // to == MinAddress → 空操作（ArgumentException）
        Assert.Throws<ArgumentException>(() =>
            table.ReclaimHeadLease(new LogicalAddress(0, 0)));
    }

    // ════════════════════════════════════════════════════════════
    //  ReclaimTail lease——尾部截断，ShrinkTail 退双尾水位
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimTailLease_Commit_RetreatsCommittedTail()
    {
        // 设计文档 §4.0/§5：ReclaimTail = 尾段区间删除 + ShrinkTail 退双尾水位
        using var table = NewTable();
        table.AppendLease(200).Commit();   // CommittedTail = (0,200)

        var newTail = new LogicalAddress(0, 100);
        using var lease = table.ReclaimTailLease(newTail);
        lease.Commit();

        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(newTail, table.CommittedTail);
    }

    [Fact]
    public void ReclaimTailLease_BeyondCommittedTail_Throws()
    {
        using var table = NewTable();
        table.AppendLease(100).Commit();   // CommittedTail = (0,100)

        // newTail >= CommittedTail → 尾部不能前进
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            table.ReclaimTailLease(new LogicalAddress(0, 100)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            table.ReclaimTailLease(new LogicalAddress(0, 150)));
    }

    // ════════════════════════════════════════════════════════════
    //  跨段 lease——chunk 遍历 + 逐 chunk 提交
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void AppendLease_CrossSegment_ChunkEnumeration()
    {
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(250);   // 跨段：seg0(0-100)+seg1(0-100)+seg2(0-50)
        Assert.Equal(3, lease.ChunkCount);

        var iter = lease.GetEnumerator();
        Assert.True(iter.MoveNext());
        Assert.Equal(0, iter.Current.SegId);
        Assert.True(iter.MoveNext());
        Assert.Equal(1, iter.Current.SegId);
        Assert.True(iter.MoveNext());
        Assert.Equal(2, iter.Current.SegId);
        Assert.False(iter.MoveNext());
    }

    [Fact]
    public void AppendLease_CrossSegment_PartialCommit_LeaseStaysActive()
    {
        // 多 chunk lease：部分 chunk 提交后 lease 仍 Active，全部提交才 Committed
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(250);   // 3 chunk
        var iter = lease.GetEnumerator();

        iter.MoveNext();
        iter.CommitCurrent();   // 第 0 chunk
        Assert.Equal(LeaseState.Active, lease.State);   // 还有 chunk 未提交

        iter.MoveNext();
        iter.CommitCurrent();   // 第 1 chunk
        Assert.Equal(LeaseState.Active, lease.State);   // 还差一个

        iter.MoveNext();
        iter.CommitCurrent();   // 第 2 chunk
        Assert.Equal(LeaseState.Committed, lease.State);
    }

    [Fact]
    public void WriteLease_CrossSegment_CommitDoesNotAdvanceTail()
    {
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(250).Commit();   // CommittedTail=(2,50)
        var tailBefore = table.CommittedTail;

        using var lease = table.WriteLease(new LogicalAddress(0, 0), 250);   // 跨 3 段
        Assert.Equal(3, lease.ChunkCount);
        lease.Commit();

        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(tailBefore, table.CommittedTail);   // Write 不推水位
    }

    // ════════════════════════════════════════════════════════════
    //  Dispose 自动判断（Active→Rollback，已提交→NoOp）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void AppendLease_DisposeWithoutCommit_DefaultRollback()
    {
        using var table = NewTable();
        LeaseBase lease;
        using (lease = table.AppendLease(100)) { }
        // Dispose 默认 Rollback（安全兜底）
        Assert.Equal(LeaseState.RolledBack, lease.State);
    }

    [Fact]
    public void AppendLease_DisposeAfterCommit_IsNoOp()
    {
        using var table = NewTable();
        var lease = table.AppendLease(100);
        lease.Commit();
        lease.Dispose();
        Assert.Equal(LeaseState.Committed, lease.State);
    }

    // ════════════════════════════════════════════════════════════
    //  幂等性——重复 Commit/Rollback 是 NoOp
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void AppendLease_CommitTwice_SecondIsNoOp()
    {
        using var table = NewTable();
        using var lease = table.AppendLease(100);
        lease.Commit();
        lease.Commit();
        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(new LogicalAddress(0, 100), table.CommittedTail);
    }

    [Fact]
    public void AppendLease_RollbackAfterCommit_IsNoOp()
    {
        using var table = NewTable();
        using var lease = table.AppendLease(100);
        lease.Commit();
        lease.Rollback();
        Assert.Equal(LeaseState.Committed, lease.State);   // 不变
    }

    [Fact]
    public void WriteLease_RollbackAfterCommit_IsNoOp()
    {
        using var table = NewTable();
        table.AppendLease(200).Commit();
        var tailBefore = table.CommittedTail;

        using var lease = table.WriteLease(new LogicalAddress(0, 50), 100);
        lease.Commit();
        lease.Rollback();   // NoOp
        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(tailBefore, table.CommittedTail);
    }

    // ════════════════════════════════════════════════════════════
    //  诊断——GetActiveLeases / ForceRelease
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void GetActiveLeases_TracksActiveLease()
    {
        using var table = NewTable();
        var lease = table.AppendLease(100);
        var active = table.GetActiveLeases().ToList();
        Assert.Single(active);
        Assert.Equal(lease.Id, active[0].Id);
        Assert.Equal(LeaseState.Active, active[0].LeaseState);
        lease.Dispose();
    }

    [Fact]
    public void GetActiveLeases_ExcludesCommittedLease()
    {
        using var table = NewTable();
        var lease = table.AppendLease(100);
        lease.Commit();
        // 已提交的 lease 仍注册直到 Dispose，但 LeaseState != Active
        var active = table.GetActiveLeases().ToList();
        Assert.Empty(active);   // GetActiveLeases 只看 Active 态
        lease.Dispose();
    }

    [Fact]
    public void ForceRelease_RollsBackActiveLease()
    {
        using var table = NewTable();
        var lease = table.AppendLease(100);
        Assert.True(table.ForceRelease(lease.Id));
        Assert.Equal(LeaseState.RolledBack, lease.State);
        lease.Dispose();
    }

    [Fact]
    public void ForceRelease_UnknownLeaseId_ReturnsFalse()
    {
        using var table = NewTable();
        Assert.False(table.ForceRelease(Guid.NewGuid()));
    }

    // ════════════════════════════════════════════════════════════
    //  Write 覆写 Wasted 区间（状态机流转闭环验证）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void WriteLease_CanOverwriteReclaimedHole()
    {
        // 设计文档闭环：Append → Reclaim(→Wasted 空洞) → Write 覆写空洞
        using var table = NewTable();
        table.AppendLease(200).Commit();

        // Reclaim 中间区间 → Wasted
        table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 150)).Commit();

        // Write 覆写刚回收的空洞（CanAcquireUnsafe 允许占 Wasted）
        using var lease = table.WriteLease(new LogicalAddress(0, 50), 100);
        lease.Commit();
        Assert.Equal(LeaseState.Committed, lease.State);
    }

    [Fact]
    public void WriteLease_CanOverwriteAppendRollbackWasted()
    {
        // 设计文档闭环：Append Rollback → Wasted → Write 覆写
        using var table = NewTable();
        table.AppendLease(100).Rollback();   // → Wasted

        // Allocate 再往前推一段（绕过 Wasted 区间）
        table.AppendLease(100).Commit();   // 现在 (0,0)-(0,100) 是 Wasted，(0,100)-(0,200) 是 Committed

        // Write 覆写 Wasted 区间
        using var lease = table.WriteLease(new LogicalAddress(0, 0), 100);
        lease.Commit();
        Assert.Equal(LeaseState.Committed, lease.State);
    }

    // ════════════════════════════════════════════════════════════
    //  Segment 构造校验——growthLimit/maxOffset/minOffset 非法值必须立即抛
    //  （静默建出 Invalid 段会导致下游 EnsureSegmentsForLength 死循环，根因难定位）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Segment_ZeroGrowthLimit_Throws()
    {
        // 回归：参数错位 bug——growthLimit=0 会建出 IsValid=false 段，EnsureSegmentsForLength 死循环
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Segment(segId: 0, maxOffset: 0, minOffset: 0, growthLimit: 0));
    }

    [Fact]
    public void Segment_NegativeGrowthLimit_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Segment(segId: 0, maxOffset: 0, minOffset: 0, growthLimit: -1));
    }

    [Fact]
    public void Segment_NegativeMaxOffset_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Segment(segId: 0, maxOffset: -1, minOffset: 0, growthLimit: 1000));
    }

    [Fact]
    public void Segment_NegativeMinOffset_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Segment(segId: 0, maxOffset: 0, minOffset: -1, growthLimit: 1000));
    }

    [Fact]
    public void Segment_HollowSentinel_BypassesValidation()
    {
        // Hollow 哨兵用 -1 全字段构造，校验必须放过（segId < 0）
        var hollow = Segment.Hollow;
        Assert.Equal(-1, hollow.SegId);
        Assert.Equal(StableState.Invalid, hollow.StableState);
        Assert.False(hollow.IsValid);
    }

    // ════════════════════════════════════════════════════════════
    //  CompactChunk 互斥枚举 + Commit 完整性绊线（设计文档 §5）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void CompactLease_CommitWithoutFillingChunks_Throws()
    {
        // 漏填 SetReplacement/MarkInvalid 的半填 lease 必须被 Commit 入口拒绝（fail-fast 绊线），
        //   且校验在 CAS 之前——失败后 lease 仍 Active，走 Rollback 收尾。
        using var table = NewTable();
        table.AppendLease(200).Commit();

        var lease = table.CompactLeaseForTest(new LogicalAddress(0, 0), new LogicalAddress(0, 200));
        Assert.Throws<InvalidOperationException>(() => lease.Commit());
        Assert.Equal(LeaseState.Active, lease.State);   // 校验先于 CAS——状态未动
        lease.Dispose();
    }

    [Fact]
    public void CompactChunk_RepeatOrCrossSet_Throws()
    {
        // 互斥终态：重复/交叉调用 SetReplacement/MarkInvalid 必须抛（非法态从类型上消灭）。
        using var table = NewTable();
        table.AppendLease(200).Commit();

        var lease = table.CompactLeaseForTest(new LogicalAddress(0, 0), new LogicalAddress(0, 200));
        var chunk = lease.Chunks[0];
        chunk.SetReplacement(1000, 200);
        Assert.Throws<InvalidOperationException>(() => chunk.SetReplacement(1000, 200));   // 重复
        Assert.Throws<InvalidOperationException>(() => chunk.MarkInvalid());                // 交叉

        lease.Dispose();
    }

    [Fact]
    public void Segment_ValidConstruction_Succeeds()
    {
        var seg = new Segment(segId: 0, maxOffset: 100, minOffset: 0, growthLimit: 1000);
        Assert.True(seg.IsValid);
        Assert.Equal(1000, seg.GrowthLimit);
        Assert.Equal(100, seg.MaxOffset);
    }

    // ════════════════════════════════════════════════════════════
    //  doneMask 仲裁回归（docs/design/lease-protocol-unified-design.md §2）
    //  ——四路径（部分提交/部分回滚/整体提交/整体回滚）exactly-once + 双触发
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void AppendLease_CrossSegment_MiddleChunkCommitsFirst_EarlyRelease()
    {
        // 乱序中间段：chunk 1（段 1）先提交——该段区间立即归还（extent 转 Committed），
        // lease 仍 Active；0、2 随后提交，最后一个触发整体收敛推尾。
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(250);   // 3 chunk：seg0[0,100) seg1[0,100) seg2[0,50)
        var committedBefore = table.CommittedTail;

        lease.OnChunkCommit(1);   // 中间段先提交
        Assert.Equal(LeaseState.Active, lease.State);
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(1, 0));   // 段 1 区间已归还

        lease.OnChunkCommit(0);
        Assert.Equal(LeaseState.Active, lease.State);   // 还差最后一个
        Assert.Equal(committedBefore, table.CommittedTail);   // 全局尾未推（precise-prefix）

        lease.OnChunkCommit(2);
        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(lease.End, table.CommittedTail);   // 最后增量者触发收敛
    }

    [Fact]
    public void AppendLease_PartialThenWholeCommit_SkipsTerminalChunks()
    {
        // A1 混合序封洞回归：部分 chunk 提交后整体 Commit——已终态 chunk 必须跳过（不重放）。
        //   可观测断言：被跳过 chunk 的 extent 状态不被二次流转（Committed 保持）、尾一次推到位。
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(250);   // 3 chunk

        lease.OnChunkCommit(0);
        lease.OnChunkCommit(1);
        Assert.Equal(LeaseState.Active, lease.State);

        lease.Commit();   // 整体提交——只扫尾 chunk 2
        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(lease.End, table.CommittedTail);
        // 三个段的区间全部 Committed（无 Wasted——整体提交没有把已提交 chunk 当回滚重放）
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(1, 0));
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(2, 0));
    }

    [Fact]
    public void AppendLease_MixedCommitAndRollback_AutoFinalizes()
    {
        // 混合方向：commit 0、rollback 2、rollback 1——回滚者补满 mask → Finalized 自动收敛，
        // 尾推到 lease.End 越 Wasted 空洞（Append 地址不可逆），不再等 Dispose。
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(250);   // 3 chunk

        lease.OnChunkCommit(0);
        Assert.Equal(LeaseState.Active, lease.State);   // mask 未满

        lease.OnChunkRollback(2);
        Assert.Equal(LeaseState.Active, lease.State);   // 仍缺 chunk 1

        lease.OnChunkRollback(1);   // 补满 mask → Finalized
        Assert.Equal(LeaseState.Finalized, lease.State);
        Assert.Equal(lease.End, table.CommittedTail);   // 收敛推尾（越 Wasted 空洞）

        lease.Dispose();   // 终态后 Dispose no-op
        Assert.Equal(LeaseState.Finalized, lease.State);
    }

    [Fact]
    public void AppendLease_SingleChunkRollback_AutoFinalizes()
    {
        // 单 chunk lease 的 chunk 级回滚：mask 单位即满 → 立即 Finalized（旧行为要等 Dispose 整体回滚）。
        using var table = NewTable();
        using var lease = table.AppendLease(100);

        lease.OnChunkRollback(0);
        Assert.Equal(LeaseState.Finalized, lease.State);
        Assert.Equal(new LogicalAddress(0, 100), table.CommittedTail);   // AppendRollback 仍推尾
    }

    [Fact]
    public void AppendLease_WholeCommitAfterMixedTerminal_SkipsCommittedAndRolledBack()
    {
        // 混合终态后整体 Commit：只扫尾未终态 chunk；已回滚的 chunk 不被"反提交"。
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(250);   // 3 chunk

        lease.OnChunkCommit(0);
        lease.OnChunkRollback(1);   // chunk 1 终态为回滚（Wasted）
        Assert.Equal(LeaseState.Active, lease.State);   // chunk 2 未终态

        lease.Commit();   // 整体提交——只扫尾 chunk 2；0（已提交）、1（已回滚）跳过
        Assert.Equal(LeaseState.Committed, lease.State);   // 整体路径自带标签
        Assert.Equal(lease.End, table.CommittedTail);
        // chunk 1 保持 Wasted（整体提交没有把已回滚 chunk 重放成 Committed）
        Assert.Equal(ExtentStateCode.Wasted, table.ExtentStateAt(1, 0));
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(0, 0));
        Assert.Equal(ExtentStateCode.Committed, table.ExtentStateAt(2, 0));
    }

    [Fact]
    public void AppendLease_FinalizeTwice_IsIdempotent()
    {
        // 双触发幂等：全 chunk 提交收敛后再整体 Commit——CAS 失败 no-op，无二次副作用。
        using var table = NewTable(growthLimit: 100);
        using var lease = table.AppendLease(250);

        lease.OnChunkCommit(0);
        lease.OnChunkCommit(1);
        lease.OnChunkCommit(2);
        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(lease.End, table.CommittedTail);

        lease.Commit();   // 二次整体提交——no-op
        Assert.Equal(LeaseState.Committed, lease.State);
        Assert.Equal(lease.End, table.CommittedTail);   // 尾不变（Finalize 幂等）
    }

    [Fact]
    public void ReclaimTail_MixedChunkTerminal_ReleasesHoldEarly()
    {
        // ReclaimTail 混合终态：最后 chunk 回滚补满 mask → Finalized → hold 提前释放（不再挡 Allocate）。
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(250).Commit();   // CommittedTail=(2,50)，3 段

        var lease = table.ReclaimTailLease(new LogicalAddress(1, 20));   // 占 [1,20)-(2,50)：2 chunk
        lease.OnChunkCommit(0);
        Assert.Equal(LeaseState.Active, lease.State);

        lease.OnChunkRollback(1);   // 补满 mask → Finalized → ShrinkTail + ReleaseTailWatermark
        Assert.Equal(LeaseState.Finalized, lease.State);
        Assert.Equal(new LogicalAddress(1, 20), table.CommittedTail);

        using var append = table.AppendLease(30);   // hold 已释放——Allocate 立即成功
        Assert.Equal(LeaseState.Active, append.State);
    }


    [Fact]
    public void ReclaimTail_Held_BlocksAppendAllocateUntilTimeout()
    {
        // 短超时（100ms）——Append 在双尾水位被持有时应超时
        var table = new SegmentTable(new SegmentTableSettings(1000, 0, 8, SpinMilliseconds: 100));
        table.AppendLease(500).Commit();

        var lease = table.ReclaimTailLease(new LogicalAddress(0, 100));   // HoldTailWatermark
        try
        {
            // ★ Append 被双尾水位独占阻塞，100ms 后超时（不复用 NewTable 的 2000ms，避免测试慢）
            Assert.Throws<TimeoutException>(() => table.AppendLease(50));
        }
        finally
        {
            lease.Dispose();   // 清理：Release
        }
    }

    [Fact]
    public void ReclaimTail_Commit_ReleasesTailWatermark_AppendSucceeds()
    {
        using var table = NewTable();
        table.AppendLease(500).Commit();

        var lease = table.ReclaimTailLease(new LogicalAddress(0, 100));   // Hold
        lease.Commit();   // ShrinkTail → ReleaseTailWatermark

        // ★ 标志已释放，Append 立即成功
        using var appendLease = table.AppendLease(50);
        Assert.Equal(LeaseState.Active, appendLease.State);
    }

    [Fact]
    public void ReclaimTail_Dispose_ReleasesTailWatermark_AppendSucceeds()
    {
        using var table = NewTable();
        table.AppendLease(500).Commit();

        var lease = table.ReclaimTailLease(new LogicalAddress(0, 100));   // Hold
        lease.Dispose();   // Rollback → ShrinkTail → ReleaseTailWatermark

        // ★ using 释放（Rollback）也清标志，Append 成功
        using var appendLease = table.AppendLease(50);
        Assert.Equal(LeaseState.Active, appendLease.State);
    }

    [Fact]
    public void ReclaimTail_Concurrent_SecondThrows()
    {
        using var table = NewTable();
        table.AppendLease(500).Commit();

        var lease1 = table.ReclaimTailLease(new LogicalAddress(0, 100));   // TryHold 成功
        try
        {
            // ★ 第二个 ReclaimTail：双尾水位被 lease1 持有 → 抛 InvalidOperationException（不等待，不像 Append 自旋）
            Assert.Throws<InvalidOperationException>(
                () => table.ReclaimTailLease(new LogicalAddress(0, 50)));
        }
        finally
        {
            lease1.Dispose();   // Release
        }
    }

    // ════════════════════════════════════════════════════════════
    //  CreateSegmentCallback 幂等性（S1 固化：失败分支 CAS 对称，非 Empty 一律 no-op）
    //  ★ 场景：池预建失败回调与正式建段失败回调在高并发下可先后命中同一段——
    //    重复/迟到失败回调不得抛（异常路径里再抛 = worker 毒化）、不得打断已迁移段。
    // ════════════════════════════════════════════════════════════

    /// <summary>无操作段处理器——handler 在场时注册段出生 Empty（物理门关），回调测试用。</summary>
    private sealed class NoopSegmentHandler : ISegmentHandler
    {
        public void OnSegmentCreate(int segId, long growthLimit, bool isHighPriority) { }
        public void OnSegmentFull(int segId, long finalSize, long growthLimit) { }
        public void OnSegmentDelete(int segId) { }
        public void OnSegmentReplace(int segId, long growthLimit, long maxOffset) { }
        public void OnSegmentReclaim(int segId, long from, long to, long growthLimit) { }
        public void SubmitBackgroundWork(Action work) { }
    }

    private static SegmentTable NewTableWithHandler(long growthLimit = 1000)
        => new(new SegmentTableSettings(growthLimit, SpinMilliseconds: 2000), new NoopSegmentHandler());

    [Fact]
    public void CreateSegmentCallback_Failure_Repeat_IsIdempotentNoThrow()
    {
        // 双失败回调（池预建失败 + 正式建段失败）先后命中——第二次 no-op，绝不抛
        using var table = NewTableWithHandler();
        using var lease = table.AppendLease(100);   // 注册 seg0（Empty——handler 在场）
        Assert.Equal(StableState.Empty, table.GetSegment(0).StableState);

        table.CreateSegmentCallback(0, success: false);
        var ex = Record.Exception(() => table.CreateSegmentCallback(0, success: false));

        Assert.Null(ex);
        Assert.Equal(StableState.Broken, table.GetSegment(0).StableState);
    }

    [Fact]
    public void CreateSegmentCallback_LateFailure_AfterReady_IsNoOp()
    {
        // 迟到失败回调遇已成功段（Empty→Ready 已迁移）——不打断健康段
        using var table = NewTableWithHandler();
        using var lease = table.AppendLease(100);

        table.CreateSegmentCallback(0, success: true);
        table.CreateSegmentCallback(0, success: false);

        Assert.Equal(StableState.Ready, table.GetSegment(0).StableState);
    }

    [Fact]
    public void CreateSegmentCallback_LateSuccess_AfterBroken_IsNoOp()
    {
        // 反向迟到：失败先到（Broken 终态），迟到的成功回调不得复活段
        using var table = NewTableWithHandler();
        using var lease = table.AppendLease(100);

        table.CreateSegmentCallback(0, success: false);
        table.CreateSegmentCallback(0, success: true);

        Assert.Equal(StableState.Broken, table.GetSegment(0).StableState);
    }
}
