using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// SegmentTable 构造 + 边界只读属性验证。
/// 契约（构造即用，src/.../SegmentTable.cs:47-61）：
/// - 构造后 MinAddress = (MinSegId, 0)，双尾水位都是 (MinSegId, 0)
/// - 空表 SegCount = 0，MaxSegId = MinSegId - 1
/// - dispose 后 MinAddress = LogicalAddress.Invalid
/// </summary>
public class SegmentTableConstructionTests
{
    private static SegmentTable NewTable(long growthLimit = 1000, int minSegId = 0, int capacity = 8)
        => new(new SegmentTableSettings(growthLimit, minSegId, capacity));

    [Fact]
    public void Construct_DefaultMinSegId_BoundariesAtZero()
    {
        var table = NewTable(growthLimit: 1000);

        Assert.Equal(0, table.MinSegId);
        Assert.Equal(new LogicalAddress(0, 0), table.MinAddress);
        Assert.Equal(new LogicalAddress(0, 0), table.AllocatedTail);
        Assert.Equal(new LogicalAddress(0, 0), table.CommittedTail);
        Assert.Equal(0, table.SegCount);
        Assert.Equal(-1, table.MaxSegId);   // 空表 = MinSegId - 1
    }

    [Fact]
    public void Construct_NonZeroMinSegId_BoundariesAtMinSegId()
    {
        // MinSegId 非零：MinAddress 和双尾都从 (MinSegId, 0) 起
        var table = NewTable(growthLimit: 1000, minSegId: 5);

        Assert.Equal(5, table.MinSegId);
        Assert.Equal(new LogicalAddress(5, 0), table.MinAddress);
        Assert.Equal(new LogicalAddress(5, 0), table.AllocatedTail);
        Assert.Equal(new LogicalAddress(5, 0), table.CommittedTail);
        Assert.Equal(0, table.SegCount);
        Assert.Equal(4, table.MaxSegId);   // MinSegId - 1 = 4
    }

    [Fact]
    public void Construct_AllocatedTail_EqualsCommittedTail_Initially()
    {
        // 不变量：CommittedTail ≤ AllocatedTail。构造时两者相等。
        var table = NewTable();
        Assert.True(table.CommittedTail <= table.AllocatedTail);
    }

    [Fact]
    public void Dispose_MinAddress_BecomesInvalid()
    {
        // dispose 后 MinAddress 读返回 Invalid（SegmentTable.Addressing.cs:17-19 disposed 判断）
        var table = NewTable();
        Assert.NotEqual(LogicalAddress.Invalid, table.MinAddress);

        table.Dispose();

        Assert.Equal(LogicalAddress.Invalid, table.MinAddress);
        Assert.False(table.MinAddress.IsValid);
    }

    [Fact]
    public void Dispose_Idempotent_DoesNotThrow()
    {
        // 多次 dispose 不抛
        var table = NewTable();
        table.Dispose();
        table.Dispose();
    }
}
