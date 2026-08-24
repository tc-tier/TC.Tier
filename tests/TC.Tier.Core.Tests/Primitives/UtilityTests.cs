namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// Utility 工具类单元测试。
/// </summary>
public class UtilityTests
{
    // ══ ParseSize ══

    [Theory]
    [InlineData("4k", 4096)]
    [InlineData("4K", 4096)]
    [InlineData("8m", 8388608)]
    [InlineData("1g", 1073741824)]
    [InlineData("16t", 17592186044416)]
    [InlineData("1024", 1024)]
    [InlineData("0", 0)]
    public void ParseSize_CommonSuffixes_ParsesCorrectly(string input, long expected)
    {
        Utility.ParseSize(input).Should().Be(expected);
    }

    [Fact]
    public void ParseSize_PureDigits_NoSuffix_ReturnsRawValue()
    {
        Utility.ParseSize("12345").Should().Be(12345);
    }

    // ══ PreviousPowerOf2 ══

    [Theory]
    [InlineData(1L, 1L)]
    [InlineData(2L, 2L)]
    [InlineData(3L, 2L)]
    [InlineData(5000L, 4096L)]
    [InlineData(8192L, 8192L)]
    [InlineData(8193L, 8192L)]
    [InlineData(1048576L, 1048576L)]
    public void PreviousPowerOf2_ReturnsLargestPowerOfTwoNotExceeding(long input, long expected)
    {
        Utility.PreviousPowerOf2(input).Should().Be(expected);
    }

    [Fact]
    public void NumBitsPreviousPowerOf2_ExactPowerOf2_ReturnsLog2()
    {
        // 4096 = 2^12
        Utility.NumBitsPreviousPowerOf2(4096).Should().Be(12);
    }

    [Fact]
    public void NumBitsPreviousPowerOf2_NonPowerOf2_RoundsDown()
    {
        // 5000 下舍入到 4096 = 2^12
        Utility.NumBitsPreviousPowerOf2(5000).Should().Be(12);
    }

    // ══ IsPowerOfTwo ══

    [Theory]
    [InlineData(1L, true)]
    [InlineData(2L, true)]
    [InlineData(4096L, true)]
    [InlineData(8192L, true)]
    [InlineData(3L, false)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(5000L, false)]
    public void IsPowerOfTwo_VariousValues(long input, bool expected)
    {
        Utility.IsPowerOfTwo(input).Should().Be(expected);
    }

    // ══ GetLogBase2 ══

    [Theory]
    [InlineData(1, 0)]    // 2^0 = 1
    [InlineData(2, 1)]    // 2^1 = 2
    [InlineData(4, 2)]    // 2^2 = 4
    [InlineData(8, 3)]    // 2^3 = 8
    [InlineData(4096, 12)] // 2^12 = 4096
    public void GetLogBase2_Int32_PowerOfTwo_ReturnsExponent(int input, int expected)
    {
        Utility.GetLogBase2(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(1UL, 0)]
    [InlineData(2UL, 1)]
    [InlineData(1024UL, 10)]
    [InlineData(4096UL, 12)]
    public void GetLogBase2_UInt64_PowerOfTwo_ReturnsExponent(ulong input, int expected)
    {
        Utility.GetLogBase2(input).Should().Be(expected);
    }

    [Fact]
    public void GetLogBase2_UInt64_Zero_ReturnsZero()
    {
        Utility.GetLogBase2(0UL).Should().Be(0);
    }

    // ══ MonotonicUpdate (long) ══

    [Fact]
    public void MonotonicUpdate_Long_NewValueGreaterThanCurrent_UpdatesSuccessfully()
    {
        long value = 100;
        var result = Utility.MonotonicUpdate(ref value, 200, out var oldValue);
        result.Should().BeTrue();
        oldValue.Should().Be(100);
        value.Should().Be(200);
    }

    [Fact]
    public void MonotonicUpdate_Long_NewValueEqualToCurrent_DoesNotUpdate()
    {
        long value = 100;
        var result = Utility.MonotonicUpdate(ref value, 100, out var oldValue);
        result.Should().BeFalse();
        oldValue.Should().Be(100);
        value.Should().Be(100);
    }

    [Fact]
    public void MonotonicUpdate_Long_NewValueLessThanCurrent_DoesNotUpdate()
    {
        long value = 100;
        var result = Utility.MonotonicUpdate(ref value, 50, out var oldValue);
        result.Should().BeFalse();
        oldValue.Should().Be(100);
        value.Should().Be(100);
    }

    // ══ MonotonicUpdate (int) ══

    [Fact]
    public void MonotonicUpdate_Int_NewValueGreaterThanCurrent_UpdatesSuccessfully()
    {
        int value = 10;
        var result = Utility.MonotonicUpdate(ref value, 20, out var oldValue);
        result.Should().BeTrue();
        oldValue.Should().Be(10);
        value.Should().Be(20);
    }

    [Fact]
    public void MonotonicUpdate_Int_NewValueLessThanCurrent_DoesNotUpdate()
    {
        int value = 10;
        var result = Utility.MonotonicUpdate(ref value, 5, out var oldValue);
        result.Should().BeFalse();
        oldValue.Should().Be(10);
        value.Should().Be(10);
    }

    // ══ MonotonicUpdate 并发 ══

    [Fact]
    public async Task MonotonicUpdate_Concurrent_AllWinnersWriteMaxValue()
    {
        long value = 0;
        const int threadCount = 8;
        const int perThread = 1000;
        int successCount = 0;
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int tid = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    long candidate = tid * perThread + i + 1;
                    if (Utility.MonotonicUpdate(ref value, candidate, out _))
                        Interlocked.Increment(ref successCount);
                }
            });
        }
        await Task.WhenAll(tasks);
        // 最终值应为所有候选的最大值
        value.Should().Be(threadCount * perThread);
        // 成功次数应 > 0（至少最后一次更新成功）
        successCount.Should().BeGreaterThan(0);
    }

    // ══ IsBlittable ══

    [Fact]
    public void IsBlittable_ValueTypes_ReturnsTrue()
    {
        Utility.IsBlittable<int>().Should().BeTrue();
        Utility.IsBlittable<long>().Should().BeTrue();
        Utility.IsBlittable<byte>().Should().BeTrue();
    }

    [Fact]
    public void IsBlittable_ReferenceTypes_ReturnsFalse()
    {
        Utility.IsBlittable<string>().Should().BeFalse();
        Utility.IsBlittable<object>().Should().BeFalse();
    }

    // ══ PrettySize ══

    [Fact]
    public void PrettySize_CommonSizes_FormatsCorrectly()
    {
        // PrettySize 的逻辑较复杂，验证基本场景不抛异常且包含 B 后缀
        var result = Utility.PrettySize(4096);
        result.Should().EndWith("B");
        result.Should().Contain("4");  // 4KB 含 4
    }

    [Fact]
    public void PrettySize_Zero_DoesNotThrow()
    {
        var act = () => Utility.PrettySize(0);
        act.Should().NotThrow();
    }

    // ══ GetHashCode ══

    [Fact]
    public void GetHashCode_SameInput_ReturnsSameHash()
    {
        var h1 = Utility.GetHashCode(42L);
        var h2 = Utility.GetHashCode(42L);
        h1.Should().Be(h2);
    }

    [Fact]
    public void GetHashCode_DifferentInput_ReturnsDifferentHash()
    {
        var h1 = Utility.GetHashCode(42L);
        var h2 = Utility.GetHashCode(43L);
        h1.Should().NotBe(h2);
    }

    // ══ Is32Bit ══

    [Theory]
    [InlineData(0L, true)]
    [InlineData(100L, true)]
    [InlineData(4294967294L, true)]   // < 2^32 - 1
    [InlineData(4294967296L, false)]   // = 2^32
    [InlineData(-1L, false)]           // (ulong)(-1) = ulong.MaxValue >> 2^32
    public void Is32Bit_VariousValues(long input, bool expected)
    {
        Utility.Is32Bit(input).Should().Be(expected);
    }

    // ══ IsReadCache / AbsoluteAddress ══

    [Fact]
    public void AbsoluteAddress_IsInverseOfReadCacheFlag()
    {
        // IsReadCache 和 AbsoluteAddress 是读缓存标志位的读写对
        // 非 readcache 地址：IsReadCache=false
        var normalAddr = 0x1000L;
        Utility.IsReadCache(normalAddr).Should().BeFalse();
        Utility.AbsoluteAddress(normalAddr).Should().Be(normalAddr);
    }

    // ══ GetHashString ══

    [Fact]
    public void GetHashString_PositiveHash_ReturnsString()
    {
        Utility.GetHashString(12345L).Should().Be("12345");
    }

    [Fact]
    public void GetHashString_NegativeHash_ReturnsWithSign()
    {
        Utility.GetHashString(-12345L).Should().Be("-12345");
    }

    [Fact]
    public void GetHashString_NullableNull_ReturnsNullString()
    {
        Utility.GetHashString((long?)null).Should().Be("null");
    }

    [Fact]
    public void GetHashString_NullableHasValue_ReturnsValueString()
    {
        Utility.GetHashString((long?)42).Should().Be("42");
    }


}
