using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;
using System;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

public class RingSettingsValidationTests
{
    // CA1861：常量数组参数须提为 static readonly 字段，避免每次调用重复分配。
    private static readonly string[] ExpectedRingMetaPolicyKinds =
        { "Disabled", "Managed", "Transport" };

    [Fact]
    public void Default_Settings_Produce_PowerOfTwo_PageCount()
    {
        var s = new BlittableRingSettings();
        int pageCount = (int)(s.MemorySize / s.PageSize);
        ((pageCount & (pageCount - 1)) == 0).Should().BeTrue(
            $"默认 MemorySize {s.MemorySize} / PageSize {s.PageSize} = {pageCount} 必须是 2 的幂");
    }

    [Fact]
    public void Default_PageSize_IsPowerOfTwo()
    {
        var s = new BlittableRingSettings();
        ((s.PageSize & (s.PageSize - 1)) == 0).Should().BeTrue("PageSize 默认必须是 2 的幂");
    }

    [Fact]
    public void Default_PageSize_In_ValidRange()
    {
        var s = new BlittableRingSettings();
        s.PageSize.Should().BeInRange(4096, 1 << 30, "PageSize 须 [4KB, 1GB]");
    }

    [Fact]
    public void Enums_Have_Expected_Values()
    {
        ((int)OverflowPolicy.Disabled).Should().Be(0);
        ((int)OverflowPolicy.Enabled).Should().Be(1);
        Enum.GetNames<MetaPolicyKind>()
            .Should().BeEquivalentTo(ExpectedRingMetaPolicyKinds);
    }

    [Fact]
    public void ColdReadRatio_Default_IsQuarter()
    {
        var s = new BlittableRingSettings();
        s.ColdReadRatio.Should().Be(0.25);
    }

    [Fact]
    public void ClockCacheCapacity_Default_IsNull()
    {
        var s = new BlittableRingSettings();
        s.ClockCacheCapacity.Should().BeNull();
    }

    [Fact]
    public void ColdRecordBufferLimit_Default_Is1MB()
    {
        var s = new BlittableRingSettings();
        s.ColdRecordBufferLimit.Should().Be(1 << 20);
    }
}
