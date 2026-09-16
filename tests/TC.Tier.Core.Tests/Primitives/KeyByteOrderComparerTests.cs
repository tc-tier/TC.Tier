using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// KeyByteOrderComparer 契约（W7 范围扫描字节序底座）：字节字典序 Compare/Equals +
/// IsBytePrefix 前缀判定（0=恒真/8=全等/中间长度）。LE 布局注意：byte0 = 最低字节。
/// </summary>
public class KeyByteOrderComparerTests
{
    private readonly KeyByteOrderComparer<long> _cmp = new();

    [Fact]
    public void Compare_ByteLexicographic_LittleEndian()
    {
        // long 0x10 = [10,00,…]、0x110 = [10,01,…]、0x20 = [20,00,…]——字节序排序
        _cmp.Compare(0x10, 0x110).Should().BeNegative("byte0 相同（0x10=0x10），byte1：00 < 01");
        _cmp.Compare(0x110, 0x20).Should().BeNegative("byte0：0x10 < 0x20（先决）");
        _cmp.Compare(0x20, 0x10).Should().BePositive();
        _cmp.Compare(0x10, 0x10).Should().Be(0);
    }

    [Fact]
    public void Equals_ByteEquality()
    {
        _cmp.Equals(0x110, 0x110).Should().BeTrue();
        _cmp.Equals(1, 0x100_0000_0000_0001).Should().BeFalse("字节序等价谓词 = 字节相等");
    }

    [Fact]
    public void IsBytePrefix_LengthSemantics()
    {
        // long 0x110 = [11,01,00,…]（LE：byte0=0x11）
        var key = 0x0111;
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x11, 1).Should().BeTrue("1 字节前缀 [11]");
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x0111, 2).Should().BeTrue("2 字节前缀 [11,01]");
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x0111, 8).Should().BeTrue("全长相等");
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x12, 1).Should().BeFalse("byte0 不同");
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x0211, 2).Should().BeFalse("byte1 不同");
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x11, 0).Should().BeTrue("0 长前缀恒真");
    }

    [Fact]
    public void RoundTrip_ByteLayout_Identity()
    {
        var key = 0x020100FF_AA_BB_CC_DD;
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<long>(in key));
        bytes[0].Should().Be(0xDD);
        bytes[7].Should().Be(0x02, "LE：byte0 = 最低字节");
    }
}
