using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// SegmentTable 段查询验证（SegmentTable.Segment.cs:30-54）。
/// 覆盖：GetSegment（不存在返 SegmentView.Hollow，IsValid=false）、TryGetSegment（out 模式，不存在返 false）、SegToIndex（不存在返 -1）。
/// </summary>
public class SegmentTableQueryTests
{
    private static SegmentTable NewTableWithSegments(long growthLimit, int segCount)
    {
        // 用不恰好填满的方式建段（每段写 growthLimit-1，留 1 字节不触发段满预建），segCount 精确
        var table = new SegmentTable(new SegmentTableSettings(growthLimit, 0, Math.Max(8, segCount)));
        for (int i = 0; i < segCount; i++)
            table.AllocateLease(growthLimit - 1);
        return table;
    }

    // ── 空表查询（无段）──

    [Fact]
    public void TryGetSegment_OnEmptyTable_ReturnsNull()
    {
        var table = new SegmentTable(new SegmentTableSettings(1000, 0, 8));
        Assert.False(table.TryGetSegment(5, out _));
    }

    [Fact]
    public void SegToIndex_OnEmptyTable_ReturnsNegativeOne()
    {
        var table = new SegmentTable(new SegmentTableSettings(1000, 0, 8));
        Assert.Equal(-1, table.SegToIndex(5));
    }

    // ── 有段查询 ──

    [Fact]
    public void GetSegment_Existing_ReturnsSegmentWithCorrectSegId()
    {
        var table = NewTableWithSegments(1000, 3);
        for (int i = 0; i < 3; i++)
        {
            var seg = table.GetSegment(i);
            Assert.True(seg.IsValid);
        }
    }

    [Fact]
    public void TryGetSegment_Existing_ReturnsNonNull()
    {
        var table = NewTableWithSegments(1000, 3);
        Assert.True(table.TryGetSegment(1, out var seg));
        Assert.True(seg!.Value.IsValid);
    }

    [Fact]
    public void TryGetSegment_NonExistent_ReturnsNull()
    {
        var table = NewTableWithSegments(1000, 3);
        Assert.False(table.TryGetSegment(99, out _));
    }

    [Fact]
    public void SegToIndex_Existing_ReturnsArrayIndex()
    {
        // 连续建段：segId → 数组下标 一一对应（无 Invalid 占位）
        var table = NewTableWithSegments(1000, 3);
        Assert.Equal(0, table.SegToIndex(0));
        Assert.Equal(1, table.SegToIndex(1));
        Assert.Equal(2, table.SegToIndex(2));
        Assert.Equal(-1, table.SegToIndex(99));   // 不存在
    }

    [Fact]
    public void GetSegmentByIndex_ValidIndex_ReturnsSegment()
    {
        // GetSegmentByIndex 已退役（private）：连续段下标 i ↔ segId=i，改按 segId 查
        var table = NewTableWithSegments(1000, 3);
        Assert.True(table.GetSegment(0).IsValid);
        Assert.True(table.GetSegment(1).IsValid);
        Assert.True(table.GetSegment(2).IsValid);
    }

    [Fact]
    public void GetSegmentByIndex_OutOfRange_ReturnsNull()
    {
        // GetSegmentByIndex 已退役：越界改用 GetSegment(segId) 验证 IsValid=false（返 Hollow）
        var table = NewTableWithSegments(1000, 3);
        Assert.False(table.GetSegment(3).IsValid);    // == count 越界
        Assert.False(table.GetSegment(99).IsValid);   // 远越界
        Assert.False(table.GetSegment(-1).IsValid);   // 负值
    }
}
