using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage.AddressSpace;

/// <summary>
/// EnsureSegmentsForLength 空表死锁修复的针对性验证。
/// 验证"构造即用"契约：空表第一次 Allocate 自动建 seg(MinSegId)，不依赖 ApplyHints/LoadAddressTable 启动。
/// </summary>
public class EnsureSegmentsForLengthFixTests
{
    private static SegmentTable NewTable(long growthLimit = 1000, int minSegId = 0)
        => new(new SegmentTableSettings(growthLimit, minSegId, IndexCapacity: 8));

    [Fact]
    public void Allocate_OnEmptyTable_BuildsFirstSegment_NoTimeout()
    {
        // 修复前：空表 MaxSegId=-1，EnsureSegmentsForLength 循环进不去 → Allocate 死循环 30s 超时
        // 修复后：前置建 seg(MinSegId)，Allocate 正常返回
        var table = NewTable(growthLimit: 1000);

        var (start,end) = table.AllocateLease(100);

        Assert.Equal(new LogicalAddress(0, 0), start);
        Assert.Equal(new LogicalAddress(0, 100), end);
        Assert.Equal(new LogicalAddress(0, 100), table.AllocatedTail);
        Assert.Equal(1, table.SegCount);
        Assert.Equal(0, table.MaxSegId);   // 建了 seg0
        var seg0 = table.GetSegment(0);
        Assert.True(seg0.IsValid);
        Assert.Equal(StableState.Ready, seg0.StableState);   // 无 handler → Written 立即可用
    }

    [Fact]
    public void Allocate_CrossSegment_OnEmptyTable_BuildsMultipleSegments()
    {
        // 跨段场景：GrowthLimit=1000，Allocate(1500) = seg0 满 1000 + seg1 填 500（未满）
        // seg1 未满 → 不应预建 seg2（过度预建 bug 的回归断言）
        var table = NewTable(growthLimit: 1000);

        var (start, end) = table.AllocateLease(1500);

        Assert.Equal(new LogicalAddress(0, 0), start);
        Assert.Equal(new LogicalAddress(1, 500), end);
        Assert.Equal(new LogicalAddress(1, 500), table.AllocatedTail);
        Assert.Equal(2, table.SegCount);   // 精确：seg0 + seg1，没有多余的 seg2
        Assert.Equal(1, table.MaxSegId);
    }

    [Fact]
    public void Allocate_ExactlyFillsSegment_PreCreatesNextSegment()
    {
        // 恰好填满当前段 → 尾停驻段末边界 (0,1000)（区间统一，不再进位 (1,0)），并预建下一段
        var table = NewTable(growthLimit: 1000);

        table.AllocateLease(1000);   // 恰好填满 seg0 → AllocatedTail 停驻 (0,1000)

        Assert.Equal(new LogicalAddress(0, 1000), table.AllocatedTail);
        Assert.Equal(2, table.SegCount);   // seg0 + 预建的 seg1
        Assert.Equal(1, table.MaxSegId);
    }

    [Fact]
    public void Allocate_FillsMultipleSegmentsExactly_PreCreatesNext()
    {
        // 2000 = seg0 满 + seg1 满 → AllocatedTail 停驻 seg1 段末 (1,1000)，预建 seg2
        var table = NewTable(growthLimit: 1000);

        table.AllocateLease(2000);

        Assert.Equal(new LogicalAddress(1, 1000), table.AllocatedTail);
        Assert.Equal(3, table.SegCount);   // seg0 + seg1 + 预建 seg2
        Assert.Equal(2, table.MaxSegId);
    }

    [Fact]
    public void Allocate_OnEmptyTable_WithMinSegId_StartsFromMinSegId()
    {
        // MinSegId 非零：第一次 Allocate 应建 seg(MinSegId) 并从 (MinSegId,0) 起分配
        var table = NewTable(growthLimit: 1000, minSegId: 5);

        var (start, end) = table.AllocateLease(50);

        Assert.Equal(new LogicalAddress(5, 0), start);
        Assert.Equal(new LogicalAddress(5, 50), table.AllocatedTail);
        Assert.Equal(5, table.MaxSegId);
        Assert.True(table.GetSegment(5).IsValid);
    }

    [Fact]
    public void Allocate_SecondTime_ReusesExistingSegment()
    {
        // 已建段后再 Allocate：前置 if 不触发（segId <= maxSegId），走原循环，行为不变
        var table = NewTable(growthLimit: 1000);

        table.AllocateLease(100);
        var (secondStart, secondEnd) = table.AllocateLease(50);

        Assert.Equal(new LogicalAddress(0, 100), secondStart);
        Assert.Equal(new LogicalAddress(0, 150), secondEnd);
        Assert.Equal(new LogicalAddress(0, 150), table.AllocatedTail);
        Assert.Equal(1, table.SegCount);   // 没有重复建段
    }

    [Fact]
    public void Allocate_ZeroLength_OnEmptyTable_ReturnsInitialTail_NoSegmentCreated()
    {
        // length==0 短路返回，不进 EnsureSegmentsForLength，不建段（契约保持）
        var table = NewTable(growthLimit: 1000);

        var (start, end) = table.AllocateLease(0);

        Assert.Equal(new LogicalAddress(0, 0), start);
        Assert.Equal(new LogicalAddress(0, 0), end);
        Assert.Equal(0, table.SegCount);   // 零长度不建段
    }
}
