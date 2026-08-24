using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// SegmentTable 地址算术验证（SegmentTable.Addressing.cs:52-148）。
/// 覆盖：AdvanceAddress（段内/跨段/边界进位/零/负数）、RetreatAddress（段内/跨段借位/低于MinAddress返Invalid）、
/// GetDistance（同段/跨段/from>to负值/相等零）。
/// 地址算术依赖段存在（GetSegment 取 GrowthLimit 算进位），测试用 Allocate 建段。
/// </summary>
public class SegmentTableAddressingTests
{
    private static SegmentTable NewTableWithSegments(long growthLimit, int segCount)
    {
        // 用 Allocate 建连续段（每段填满触发下一段预建）
        var table = new SegmentTable(new SegmentTableSettings(growthLimit, 0, Math.Max(8, segCount)));
        for (int i = 0; i < segCount; i++)
            table.AllocateLease(growthLimit);
        return table;
    }

    // ── AdvanceAddress ──

    [Fact]
    public void AdvanceAddress_WithinSegment()
    {
        var table = NewTableWithSegments(1000, 1);
        var end = table.AdvanceAddress(new LogicalAddress(0, 100), 200);
        Assert.Equal(new LogicalAddress(0, 300), end);
    }

    [Fact]
    public void AdvanceAddress_CrossSegment()
    {
        var table = NewTableWithSegments(1000, 2);
        // (0,900) + 200 → seg0 剩 100 + seg1 走 100 → (1,100)
        var end = table.AdvanceAddress(new LogicalAddress(0, 900), 200);
        Assert.Equal(new LogicalAddress(1, 100), end);
    }

    [Fact]
    public void AdvanceAddress_ExactlyAtSegmentBoundary_StaysAtSegmentEnd()
    {
        var table = NewTableWithSegments(1000, 2);
        // (0,800) + 200 → 恰好填满 seg0 → 停驻段末边界 (0,1000)（区间统一：不归一成 (1,0)）
        var end = table.AdvanceAddress(new LogicalAddress(0, 800), 200);
        Assert.Equal(new LogicalAddress(0, 1000), end);
    }

    [Fact]
    public void AdvanceAddress_ReturnedExtensionIsZero()
    {
        // 契约：返回的 Extension 恒为 0（调用方须显式保留 start.Extension）
        var table = NewTableWithSegments(1000, 1);
        var start = new LogicalAddress(0, 5, 100);   // segId=0, extension=5, offset=100
        var end = table.AdvanceAddress(start, 50);
        Assert.Equal(0, end.Extension);
    }

    [Fact]
    public void AdvanceAddress_Zero_ReturnsStart()
    {
        var table = NewTableWithSegments(1000, 1);
        var start = new LogicalAddress(0, 100);
        Assert.Equal(start, table.AdvanceAddress(start, 0));
    }

    [Fact]
    public void AdvanceAddress_Negative_Throws()
    {
        var table = NewTableWithSegments(1000, 1);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => table.AdvanceAddress(new LogicalAddress(0, 100), -50));
    }

    // ── RetreatAddress ──

    [Fact]
    public void RetreatAddress_WithinSegment()
    {
        var table = NewTableWithSegments(1000, 1);
        var end = table.RetreatAddress(new LogicalAddress(0, 500), 200);
        Assert.Equal(new LogicalAddress(0, 300), end);
    }

    [Fact]
    public void RetreatAddress_CrossSegment()
    {
        var table = NewTableWithSegments(1000, 2);
        // (1,100) - 200 → seg1 剩 100 → 借 seg0 末尾 100 → (0,900)
        var end = table.RetreatAddress(new LogicalAddress(1, 100), 200);
        Assert.Equal(new LogicalAddress(0, 900), end);
    }

    [Fact]
    public void RetreatAddress_Zero_ReturnsStart()
    {
        var table = NewTableWithSegments(1000, 1);
        var start = new LogicalAddress(0, 100);
        Assert.Equal(start, table.RetreatAddress(start, 0));
    }

    [Fact]
    public void RetreatAddress_Negative_Throws()
    {
        var table = NewTableWithSegments(1000, 1);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => table.RetreatAddress(new LogicalAddress(0, 100), -50));
    }

    [Fact]
    public void RetreatAddress_BelowMinAddress_ReturnsInvalid()
    {
        var table = NewTableWithSegments(1000, 1);
        // (0,50) - 100 → 低于 MinAddress(0,0) → Invalid
        var end = table.RetreatAddress(new LogicalAddress(0, 50), 100);
        Assert.Equal(LogicalAddress.Invalid, end);
        Assert.False(end.IsValid);
    }

    // ── GetDistance ──

    [Fact]
    public void GetDistance_SameSegment()
    {
        var table = NewTableWithSegments(1000, 1);
        Assert.Equal(400, table.GetDistance(new LogicalAddress(0, 100), new LogicalAddress(0, 500)));
    }

    [Fact]
    public void GetDistance_CrossSegment()
    {
        var table = NewTableWithSegments(1000, 2);
        // seg0 剩 100 + seg1 走 100 = 200
        Assert.Equal(200, table.GetDistance(new LogicalAddress(0, 900), new LogicalAddress(1, 100)));
    }

    [Fact]
    public void GetDistance_FromGreaterThanTo_ReturnsNegative()
    {
        var table = NewTableWithSegments(1000, 1);
        Assert.Equal(-400, table.GetDistance(new LogicalAddress(0, 500), new LogicalAddress(0, 100)));
    }

    [Fact]
    public void GetDistance_Equal_ReturnsZero()
    {
        var table = NewTableWithSegments(1000, 1);
        var addr = new LogicalAddress(0, 500);
        Assert.Equal(0, table.GetDistance(addr, addr));
    }

    // ── 多段 / 混合场景（多段进位、一直加、一直减、先+后-、空段 Hollow）──

    [Fact]
    public void AdvanceAddress_CrossMultipleSegments()
    {
        var table = NewTableWithSegments(1000, 3);
        // (0,100) + 2100 → seg0 剩 900 + seg1 全 1000 + seg2 走 200 → (2,200)
        Assert.Equal(new LogicalAddress(2, 200),
            table.AdvanceAddress(new LogicalAddress(0, 100), 2100));
    }

    [Fact]
    public void AdvanceAddress_CrossManySegments()
    {
        var table = NewTableWithSegments(100, 10);
        // (0,0) + 550 → 跨 seg0..4（5×100=500）+ seg5 走 50 → (5,50)
        Assert.Equal(new LogicalAddress(5, 50),
            table.AdvanceAddress(new LogicalAddress(0, 0), 550));
    }

    [Fact]
    public void AdvanceAddress_CrossUnbuiltSegments_UsesGlobalGrowthLimit()
    {
        var table = NewTableWithSegments(1000, 1);   // 只建 seg0；seg1/seg2 未建（Hollow 占位）
        // (0,0) + 2500 → seg0 全 1000 + seg1(Hollow,全局1000) + seg2 走 500 → (2,500)
        Assert.Equal(new LogicalAddress(2, 500),
            table.AdvanceAddress(new LogicalAddress(0, 0), 2500));
    }

    [Fact]
    public void AdvanceAddress_RepeatedAdvances_EqualSingleBigAdvance()
    {
        var table = NewTableWithSegments(1000, 5);
        var start = new LogicalAddress(0, 0);
        var bigStep = table.AdvanceAddress(start, 3200);   // 一次跨到 (3,200)
        var cur = start;
        for (int i = 0; i < 4; i++)
            cur = table.AdvanceAddress(cur, 800);          // 4 × 800 = 3200（一直加，分步）
        Assert.Equal(bigStep, cur);
    }

    [Fact]
    public void RetreatAddress_CrossMultipleSegments()
    {
        var table = NewTableWithSegments(1000, 4);
        // (3,100) - 2100 → seg3 剩 100 + seg2 全 + seg1 全 → (1,0)
        Assert.Equal(new LogicalAddress(1, 0),
            table.RetreatAddress(new LogicalAddress(3, 100), 2100));
    }

    [Fact]
    public void AdvanceThenRetreat_ReturnsToOrigin()
    {
        var table = NewTableWithSegments(1000, 5);
        var origin = new LogicalAddress(1, 500);
        var advanced = table.AdvanceAddress(origin, 1800);  // → (3,300)
        // 先 + 后 - 应回到原点（跨段往返可逆）
        Assert.Equal(origin, table.RetreatAddress(advanced, 1800));
    }

    [Fact]
    public void RetreatThenAdvance_ReturnsToOrigin()
    {
        var table = NewTableWithSegments(1000, 5);
        var origin = new LogicalAddress(3, 500);
        var retreated = table.RetreatAddress(origin, 1800); // → (1,700)
        // 先 - 后 + 应回到原点（跨段往返可逆）
        Assert.Equal(origin, table.AdvanceAddress(retreated, 1800));
    }

    [Fact]
    public void GetDistance_CrossMultipleSegments()
    {
        var table = NewTableWithSegments(1000, 4);
        // (0,100)→(3,200): seg0 剩 900 + seg1 全 + seg2 全 + seg3 走 200 = 3100
        Assert.Equal(3100,
            table.GetDistance(new LogicalAddress(0, 100), new LogicalAddress(3, 200)));
    }

    // ── 边界/Hollow 补全覆盖（2026-08-16 审计；2026-08-21 区间统一：恰好填满停驻段末 (seg,limit)，废除 (seg+1,0) 哨兵形态）──

    [Fact]
    public void AdvanceAddress_ExactFillFromSegmentHead()
    {
        var table = NewTableWithSegments(1000, 2);
        // (0,0) + 1000 → 恰好填满整段 → 停驻段末边界 (0,1000)——数据写到"最后"，不给下一段地址
        Assert.Equal(new LogicalAddress(0, 1000),
            table.AdvanceAddress(new LogicalAddress(0, 0), 1000));
    }

    [Fact]
    public void AdvanceAddress_ExactFillIntoUnbuiltSegment()
    {
        var table = NewTableWithSegments(1000, 1);   // 只建 seg0；seg1/seg2 未建（Hollow 占位）
        // (0,0) + 2000 → seg0 恰好填满 + seg1(Hollow,全局1000) 恰好填满 → 停驻 seg1 段末 (1,1000)
        Assert.Equal(new LogicalAddress(1, 1000),
            table.AdvanceAddress(new LogicalAddress(0, 0), 2000));
    }

    [Fact]
    public void RetreatAddress_FromLegacySentinelHead_BorrowsFullSegment()
    {
        var table = NewTableWithSegments(1000, 2);
        // 存量兼容面：旧哨兵形态输入 (1,0)（≡ 段末 (0,1000) 同点）借整段 → (0,0)
        Assert.Equal(new LogicalAddress(0, 0),
            table.RetreatAddress(new LogicalAddress(1, 0), 1000));
    }

    [Fact]
    public void Roundtrip_AtExactFillBoundary()
    {
        var table = NewTableWithSegments(1000, 3);
        // 恰好填满边界的往返互逆（统一后无"归一头"特判）：Advance 停驻 (0,1000)，Retreat 精确回到 (0,0)
        var end = table.AdvanceAddress(new LogicalAddress(0, 0), 1000);
        Assert.Equal(new LogicalAddress(0, 1000), end);
        Assert.Equal(new LogicalAddress(0, 0), table.RetreatAddress(end, 1000));
        // 中段起步的镜像同样成立：Advance 恰满停驻段末，Retreat 精确退回起步点
        Assert.Equal(new LogicalAddress(0, 800),
            table.RetreatAddress(table.AdvanceAddress(new LogicalAddress(0, 800), 200), 200));
    }

    [Fact]
    public void RetreatAddress_ThroughHollowSegment_UsesGlobalGrowthLimit()
    {
        var table = NewTableWithSegments(1000, 1);   // 只建 seg0；seg1 未建（Hollow 占位）
        // (2,300) - 1350 → seg2 走 300 + seg1(Hollow,全局1000) 借整段 + seg0 走 50 → (0,950)
        Assert.Equal(new LogicalAddress(0, 950),
            table.RetreatAddress(new LogicalAddress(2, 300), 1350));
    }

    [Fact]
    public void GetDistance_AcrossHollowSegments_UsesGlobalGrowthLimit()
    {
        var table = NewTableWithSegments(1000, 1);   // 只建 seg0
        // (0,0)→(2,0)：跨 seg0 全 + seg1(Hollow,全局1000) = 2000
        Assert.Equal(2000,
            table.GetDistance(new LogicalAddress(0, 0), new LogicalAddress(2, 0)));
    }

    [Fact]
    public void GetDistance_LegacySentinel_EqualsCanonicalSegmentEnd()
    {
        var table = NewTableWithSegments(1000, 2);
        // (0,1000) 是规范段末形（区间统一），(1,0) 是旧哨兵形（存量输入）——同点距离必须为 0
        Assert.Equal(0,
            table.GetDistance(new LogicalAddress(1, 0), new LogicalAddress(0, 1000)));
        Assert.Equal(0,
            table.GetDistance(new LogicalAddress(0, 1000), new LogicalAddress(1, 0)));
    }

    [Fact]
    public void GetDistance_FromGreaterThanTo_CrossSegment_ReturnsNegative()
    {
        var table = NewTableWithSegments(1000, 2);
        // (1,100)→(0,900)：倒跨段 = -(100 + 100) = -200
        Assert.Equal(-200,
            table.GetDistance(new LogicalAddress(1, 100), new LogicalAddress(0, 900)));
    }
}
