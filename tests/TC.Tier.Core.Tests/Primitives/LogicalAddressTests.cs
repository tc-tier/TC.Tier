using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Tests.Primitives;

public sealed class LogicalAddressTests
{
    [Fact]
    public void Empty_IsZero()
    {
        LogicalAddress.Empty.SegId.Should().Be(0);
        LogicalAddress.Empty.Offset.Should().Be(0);
        LogicalAddress.Empty.Extension.Should().Be(0);
    }

    [Fact]
    public void Constructor_TwoArgs_SetsExtensionToZero()
    {
        var addr = new LogicalAddress(5, 1024);
        addr.SegId.Should().Be(5);
        addr.Offset.Should().Be(1024);
        addr.Extension.Should().Be(0);
    }

    [Fact]
    public void Constructor_ThreeArgs_AllFieldsSet()
    {
        var addr = new LogicalAddress(3, 42, 2048);
        addr.SegId.Should().Be(3);
        addr.Extension.Should().Be(42);
        addr.Offset.Should().Be(2048);
    }

    [Fact]
    public void Equals_SameSegIdAndOffset_ReturnsTrue()
    {
        var a = new LogicalAddress(1, 100);
        var b = new LogicalAddress(1, 100);
        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentExtension_StillReturnsTrue()
    {
        // Extension 不参与相等性比较
        var a = new LogicalAddress(1, 5, 100);
        var b = new LogicalAddress(1, 9, 100);
        a.Equals(b).Should().BeTrue("Extension 不参与 equals");
    }

    [Fact]
    public void Equals_DifferentSegId_ReturnsFalse()
    {
        var a = new LogicalAddress(1, 100);
        var b = new LogicalAddress(2, 100);
        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentOffset_ReturnsFalse()
    {
        var a = new LogicalAddress(1, 100);
        var b = new LogicalAddress(1, 200);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_ObjectOverload_Works()
    {
        var a = new LogicalAddress(1, 100);
        object b = new LogicalAddress(1, 100);
        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void Equals_ObjectNull_ReturnsFalse()
    {
        var a = new LogicalAddress(1, 100);
        a.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Equals_ObjectWrongType_ReturnsFalse()
    {
        var a = new LogicalAddress(1, 100);
        a.Equals("string").Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_SameValues_ProducesSameHash()
    {
        var a = new LogicalAddress(5, 1024);
        var b = new LogicalAddress(5, 1024);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentExtension_ProducesSameHash()
    {
        var a = new LogicalAddress(5, 3, 1024);
        var b = new LogicalAddress(5, 9, 1024);
        a.GetHashCode().Should().Be(b.GetHashCode(), "Extension 不参与 hash");
    }

    // === Comparison ===

    [Fact]
    public void CompareTo_SameAddress_ReturnsZero()
    {
        var a = new LogicalAddress(1, 100);
        a.CompareTo(a).Should().Be(0);
    }

    [Fact]
    public void CompareTo_SmallerSegId_ReturnsNegative()
    {
        var a = new LogicalAddress(1, 500);
        var b = new LogicalAddress(2, 100);
        a.CompareTo(b).Should().BeNegative();
    }

    [Fact]
    public void CompareTo_SameSegId_LargerOffset_ReturnsPositive()
    {
        var a = new LogicalAddress(1, 500);
        var b = new LogicalAddress(1, 100);
        a.CompareTo(b).Should().BePositive();
    }

    [Fact]
    public void LessThan_Operator_Works()
    {
        (new LogicalAddress(1, 0) < new LogicalAddress(2, 0)).Should().BeTrue();
        (new LogicalAddress(1, 100) < new LogicalAddress(1, 200)).Should().BeTrue();
    }

    [Fact]
    public void GreaterThan_Operator_Works()
    {
        (new LogicalAddress(2, 0) > new LogicalAddress(1, 0)).Should().BeTrue();
        (new LogicalAddress(1, 200) > new LogicalAddress(1, 100)).Should().BeTrue();
    }

    [Fact]
    public void LessThanOrEqual_Operator_Works()
    {
        var a = new LogicalAddress(1, 100);
        var b = new LogicalAddress(1, 100);
        (a <= b).Should().BeTrue();
        (new LogicalAddress(1, 0) <= new LogicalAddress(2, 0)).Should().BeTrue();
    }

    [Fact]
    public void GreaterThanOrEqual_Operator_Works()
    {
        var a = new LogicalAddress(1, 100);
        var b = new LogicalAddress(1, 100);
        (a >= b).Should().BeTrue();
        (new LogicalAddress(2, 0) >= new LogicalAddress(1, 0)).Should().BeTrue();
    }

    // === ToString ===

    [Fact]
    public void ToString_ReturnsExpectedFormat()
    {
        var addr = new LogicalAddress(3, 0x1A2B);
        addr.ToString().Should().Be("seg#3@0x1A2B");
    }

    // === Sort stability ===

    [Fact]
    public void Sort_MultipleAddresses_ProducesCorrectOrder()
    {
        var list = new List<LogicalAddress>
        {
            new(3, 500),
            new(1, 200),
            new(1, 100),
            new(2, 0),
            new(3, 100),
        };
        list.Sort();
        list[0].Should().Be(new LogicalAddress(1, 100));
        list[1].Should().Be(new LogicalAddress(1, 200));
        list[2].Should().Be(new LogicalAddress(2, 0));
        list[3].Should().Be(new LogicalAddress(3, 100));
        list[4].Should().Be(new LogicalAddress(3, 500));
    }

    [Fact]
    public void Extension_IsIndependent_ForEqualAddresses()
    {
        // 两个地址在 SegId+Offset 上相等但 Extension 不同——相等判断返回 true
        var a = new LogicalAddress(1, 10, 100);
        var b = new LogicalAddress(1, 20, 100);
        a.Equals(b).Should().BeTrue();
        a.CompareTo(b).Should().Be(0, "Extension 不参与比较排序");
    }
}
