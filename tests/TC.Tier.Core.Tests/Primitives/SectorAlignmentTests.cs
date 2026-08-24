namespace TC.Tier.Core.Tests.Primitives;

public sealed class SectorAlignmentTests
{
    [Theory]
    [InlineData(0, 512, 0)]
    [InlineData(1, 512, 512)]
    [InlineData(511, 512, 512)]
    [InlineData(512, 512, 512)]
    [InlineData(513, 512, 1024)]
    [InlineData(1023, 512, 1024)]
    [InlineData(1024, 512, 1024)]
    public void AlignUp_PowerOfTwo_Values(long value, int alignment, long expected)
    {
        Assert.Equal(expected, value.AlignUp(alignment));
    }

    [Theory]
    [InlineData(0, 512, 0)]
    [InlineData(1, 512, 0)]
    [InlineData(511, 512, 0)]
    [InlineData(512, 512, 512)]
    [InlineData(513, 512, 512)]
    [InlineData(1023, 512, 512)]
    [InlineData(1024, 512, 1024)]
    public void AlignDown_PowerOfTwo_Values(long value, int alignment, long expected)
    {
        Assert.Equal(expected, value.AlignDown(alignment));
    }

    [Fact]
    public void AlignUp_Alignment1_IsIdentity()
    {
        Assert.Equal(12345L, 12345L.AlignUp(1));
        Assert.Equal(0L, 0L.AlignUp(1));
        Assert.Equal(long.MaxValue, long.MaxValue.AlignUp(1));
    }

    [Fact]
    public void AlignDown_Alignment1_IsIdentity()
    {
        Assert.Equal(12345L, 12345L.AlignDown(1));
        Assert.Equal(0L, 0L.AlignDown(1));
    }

    [Fact]
    public void AlignUp_LongMax_NoOverflow()
    {
        // long.MaxValue aligned to 512 should not overflow
        long result = (long.MaxValue - 511).AlignUp(512);
        Assert.True(result > 0);
    }

    [Fact]
    public void AlignDown_Zero_AlwaysZero()
    {
        Assert.Equal(0L, 0L.AlignDown(512));
        Assert.Equal(0L, 0L.AlignDown(4096));
        Assert.Equal(0, 0.AlignDown(512));
    }

    [Fact]
    public void AlignUp_4096Alignment_Works()
    {
        Assert.Equal(0L, 0L.AlignUp(4096));
        Assert.Equal(4096L, 1L.AlignUp(4096));
        Assert.Equal(4096L, 4095L.AlignUp(4096));
        Assert.Equal(4096L, 4096L.AlignUp(4096));
    }

    [Fact]
    public void Consistency_AlignUpDown_Roundtrip()
    {
        long[] values = { 0, 1, 511, 512, 1023, 1024, 1234567 };
        foreach (var v in values)
        {
            long up = v.AlignUp(512);
            long down = up.AlignDown(512);
            Assert.Equal(up, down);
        }
    }

    [Fact]
    public void AlignUp_Int_Overload_Works()
    {
        Assert.Equal(512, 1.AlignUp(512));
        Assert.Equal(512, 511.AlignUp(512));
        Assert.Equal(0, 0.AlignUp(512));
    }

    [Fact]
    public void AlignDown_Int_Overload_Works()
    {
        Assert.Equal(0, 1.AlignDown(512));
        Assert.Equal(0, 511.AlignDown(512));
        Assert.Equal(512, 512.AlignDown(512));
    }

    // ── 校验：非 2 幂 / 非正 alignment 必须抛 ArgumentOutOfRangeException（防静默错位）──

    [Theory]
    [InlineData(0)]      // 0：mask=-1 → 原本静默返回 0（最危险）
    [InlineData(-4096)]  // 负数
    [InlineData(3)]      // 非 2 幂
    [InlineData(5000)]   // 非 2 幂
    public void AlignUp_LongIntAlignment_NonPowerOfTwoOrNonPositive_Throws(int alignment)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => 12345L.AlignUp(alignment));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4096)]
    [InlineData(3)]
    public void AlignDown_LongIntAlignment_NonPowerOfTwoOrNonPositive_Throws(int alignment)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => 12345L.AlignDown(alignment));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-4096L)]
    [InlineData(3L)]
    public void AlignUp_LongAlignment_NonPowerOfTwoOrNonPositive_Throws(long alignment)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => 12345L.AlignUp(alignment));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-512)]
    [InlineData(3)]
    public void AlignUp_IntOverload_NonPowerOfTwoOrNonPositive_Throws(int alignment)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => 12345.AlignUp(alignment));
    }

    [Fact]
    public void Align_ValidPowerOfTwo_StillWorks_AfterValidation()
    {
        // 校验加入后，合法 2 幂不受影响（回归保护）
        Assert.Equal(4096L, 1L.AlignUp(4096));
        Assert.Equal(0L, 1L.AlignDown(4096));
        Assert.Equal(512, 511.AlignUp(512));
        Assert.Equal(512, 1023.AlignDown(512));
    }
}
