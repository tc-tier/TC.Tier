using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// KeyByteOrderComparer 契约（W7 范围扫描字节序底座）：字节字典序 Compare/Equals +
/// IsBytePrefix 前缀判定（0=恒真；任意 prefixByteLength ∈ [1,sizeof] 按前 N 字节逐字节比较，
/// 尾部字节不参与）。LE 布局注意：byte0 = 最低字节。
/// </summary>
public class KeyByteOrderComparerTests
{
    private readonly KeyByteOrderComparer<long> _cmp = new();

    /// <summary>16 字节复合键（>sizeof(long) 键型——prefixByteLength=8 不再是全键）。</summary>
    private readonly record struct DualWordKey(long W0, long W1);

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
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x0111, 8).Should().BeTrue("sizeof(long)=8 时前 8 字节 = 整键");
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x12, 1).Should().BeFalse("byte0 不同");
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x0211, 2).Should().BeFalse("byte1 不同");
        KeyByteOrderComparer<long>.IsBytePrefix(key, 0x11, 0).Should().BeTrue("0 长前缀恒真");
    }

    [Fact]
    public void IsBytePrefix_CompositeKey_PrefixShorterThanKey()
    {
        // 16 字节键（#485 回归）：key W0 = [88,77,66,55,44,33,22,11]、W1 = [A9,00,…]（LE）
        var key = new DualWordKey(0x1122_3344_5566_7788, 0x0000_00A9);
        var tailZero = new DualWordKey(key.W0, 0x0000_0000);        // byte8+ 全零
        var byte8Eq = new DualWordKey(key.W0, 0x0001_00A9);         // byte8=A9、byte10=01
        var byte8Diff = new DualWordKey(key.W0, 0x0000_00BE);       // byte8=BE

        var cmp = KeyByteOrderComparer<DualWordKey>.IsBytePrefix;

        cmp(key, tailZero, 8).Should().BeTrue("前 8 字节（W0 全宽）相等即匹配——尾部字节不在前缀长度内");
        cmp(key, byte8Eq, 9).Should().BeTrue("byte8（A9）相等");
        cmp(key, byte8Diff, 9).Should().BeFalse("byte8 差异即不匹配");
        cmp(key, byte8Eq, 10).Should().BeTrue("byte9（00）相等——跨 ulong 字边界逐字节");
        cmp(key, byte8Eq, 11).Should().BeFalse("byte10（key 00 vs 前缀 01）首个差异字节判出");
        cmp(key, byte8Diff, 16).Should().BeFalse("全长含差异字节");
        cmp(key, key, 16).Should().BeTrue("全长全等");
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
