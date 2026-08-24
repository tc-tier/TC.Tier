namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// A7 裁定回归——Broken 段分配跳过（烧洞）契约测试。
/// <para>★ Broken = 建段失败终态（物理门永关），其地址永不可交付：分配区间不得包含 Broken 段的任何
///   字节；尾在 Broken 段内或请求跨越 Broken 段 → 烧洞（水位 CAS 前推过洞）→ 重试后区间全落好段。
///   lease 区间连续性契约由此保持；洞前好段余量随洞牺牲（上界 &lt; length）。</para>
/// <para>★ 夹具：同步失败回调 handler——注册（Ensure 内）即 Broken，先于分配扫描定型，确定性复现。
///   引擎级异步路径（注册后建段失败、在途 lease 吃单次快失败）见 Storage/SegmentBrokenSkipTests。</para>
/// </summary>
public class SegmentTableBrokenSkipTests
{
    /// <summary>同步成败回调 handler——failSegId 建段失败（注册即 Broken），其余注册即 Ready。</summary>
    private sealed class FailBuildHandler : ISegmentHandler
    {
        private readonly int _failSegId;
        public SegmentTable? Table { get; set; }
        public FailBuildHandler(int failSegId) => _failSegId = failSegId;

        public void OnSegmentCreate(int segId, long growthLimit, bool isHighPriority)
            => Table?.CreateSegmentCallback(segId, success: segId != _failSegId);
        public void OnSegmentFull(int segId, long finalSize, long growthLimit) { }
        public void OnSegmentDelete(int segId) { }
        public void OnSegmentReplace(int segId, long growthLimit, long maxOffset) { }
        public void OnSegmentReclaim(int segId, long from, long to, long growthLimit) { }
        public void SubmitBackgroundWork(Action work) { }
    }

    private static SegmentTable NewTable(long growthLimit, int failSegId,
        bool singleSegment = false)
    {
        var handler = new FailBuildHandler(failSegId);
        var table = new SegmentTable(new SegmentTableSettings(growthLimit,
            SpinMilliseconds: 2000, EnableSingleSegment: singleSegment), handler);
        handler.Table = table;
        return table;
    }

    [Fact]
    public void TailInsideBroken_FirstAllocation_SkipsToNextSegment()
    {
        // seg0 Broken（尾在 Broken 段内）——首次分配烧洞到 (1,0)，lease 全落 seg1
        using var table = NewTable(growthLimit: 100, failSegId: 0);
        using var lease = table.AppendLease(50);

        Assert.Equal(new LogicalAddress(1, 0), lease.Start);
        Assert.Equal(new LogicalAddress(1, 50), lease.End);
        Assert.Equal(StableState.Broken, table.GetSegment(0).StableState);
        Assert.Equal(StableState.Ready, table.GetSegment(1).StableState);
        Assert.Equal(new LogicalAddress(1, 50), table.AllocatedTail);
    }

    [Fact]
    public void RequestCrossingBroken_BurnsHoleAndPrefix_LeaseLandsAfterHole()
    {
        // seg0 好段剩 20B，seg1 Broken，请求 50B 跨洞 → 烧 [ (0,80)→(2,0) )（好段余量随洞牺牲）→ lease 落 seg2
        using var table = NewTable(growthLimit: 100, failSegId: 1);
        table.AppendLease(80).Commit();

        using var lease = table.AppendLease(50);

        Assert.Equal(new LogicalAddress(2, 0), lease.Start);
        Assert.Equal(new LogicalAddress(2, 50), lease.End);
        Assert.Equal(StableState.Broken, table.GetSegment(1).StableState);

        // CommittedTail 粗游标跨洞（A6 双轨：可读性跟 extent 走，跟游标无关）
        lease.Commit();
        Assert.Equal(new LogicalAddress(2, 50), table.CommittedTail);
    }

    [Fact]
    public void BrokenHole_NeverReadable_AllocatedDataReadable()
    {
        // 洞的地址消费不交付：seg1 全程无 extents（读门阻断）；洞前洞后已提交数据照常可读
        using var table = NewTable(growthLimit: 100, failSegId: 1);
        table.AppendLease(80).Commit();
        using var after = table.AppendLease(30);
        after.Commit();

        Assert.True(table.IsRangeFullyReadable(0, 0, 80), "洞前已提交数据必须可读");
        Assert.False(table.IsRangeFullyReadable(1, 0, 100), "Broken 洞必须不可读（零 extents）");
        Assert.True(table.IsRangeFullyReadable(2, 0, 30), "洞后已提交数据必须可读");

        // seg1 区间表零记录（烧掉的地址不产生任何 extent）
        using var reader = table.SnapshotSegmentExtents(1);
        var records = 0;
        while (reader.MoveNext()) records++;
        Assert.Equal(0, records);
    }

    [Fact]
    public void RepeatedAllocation_OverBroken_NeverGrinds()
    {
        // 连续分配碾过 Broken 段——零异常、零地址落段（研磨行为的反断言）
        using var table = NewTable(growthLimit: 100, failSegId: 1);
        var failures = 0;
        var landedInBroken = 0;
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var lease = table.AppendLease(30);
                lease.Commit();
                if (lease.Start.SegId == 1) landedInBroken++;
            }
            catch
            {
                failures++;
            }
        }
        Assert.Equal(0, failures);
        Assert.Equal(0, landedInBroken);
        Assert.Equal(StableState.Broken, table.GetSegment(1).StableState);
    }

    [Fact]
    public void AllocateLease_BrokenSkip_PlaceholderNeverMarksBrokenSegment()
    {
        // Allocate（isCommit=true 占位路径）同规则跳洞——Broken 段不被打 sparse 占位
        using var table = NewTable(growthLimit: 100, failSegId: 1);
        table.AppendLease(80).Commit();

        var (start, end) = table.AllocateLease(50);

        Assert.Equal(new LogicalAddress(2, 0), start);
        Assert.Equal(new LogicalAddress(2, 50), end);
        Assert.False(table.IsRangeFullyReadable(1, 0, 100), "Broken 段不得被占位标记");
    }

    [Fact]
    public void SingleSegmentMode_BrokenSeg0_ThrowsFast()
    {
        // 单段模式无洞可烧——seg0 Broken = 地址空间不可用，快速失败（不得死循环/不得烧到 seg1）
        using var table = NewTable(growthLimit: 100, failSegId: 0, singleSegment: true);

        Assert.Throws<InvalidOperationException>(() => table.AppendLease(50));
    }
}
