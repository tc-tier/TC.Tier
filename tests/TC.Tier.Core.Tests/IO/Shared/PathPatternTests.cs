using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.Tests.IO.Shared;

/// <summary>
/// PathPattern 单元测试——通配匹配（BCL MatchType.Simple 语义对齐）+ sidecar 命名。
/// </summary>
public sealed class PathPatternTests
{
    [Theory]
    [InlineData("data.0", "*", true)]
    [InlineData("data.0", "data.*", true)]
    [InlineData("data.0", "*.0", true)]
    [InlineData("data.0", "data.?", true)]
    [InlineData("data.10", "data.?", false)]        // ? 单字符——不匹配两位
    [InlineData("data.10", "data.??", true)]
    [InlineData("tc.log.marker", "tc.log.*", true)]
    [InlineData("other.bin", "tc.log.*", false)]
    [InlineData("a.b.c", "a*.c", true)]
    [InlineData("abc", "a*c", true)]
    [InlineData("abc", "a*d", false)]
    [InlineData("abc", "***", true)]                // 连续 * 等价单 *
    [InlineData("abc", "*bc*", true)]
    [InlineData("abc", "", false)]                  // 空 pattern 永不匹配（入口另有 Validate 拒绝）
    [InlineData("CASE.TXT", "case.txt", false)]     // Ordinal 区分大小写
    [InlineData("CASE.TXT", "CASE*", true)]
    public void IsMatch_SimpleSemantics(string name, string pattern, bool expected)
    {
        PathPattern.IsMatch(name, pattern).Should().Be(expected);
    }

    [Theory]
    [InlineData("data.0", ".data.0")]
    [InlineData("a/b/data.0", "a/b/.data.0")]
    [InlineData("a/b/c", "a/b/.c")]
    public void SidecarOf_DotPrefixSameDirectory(string path, string expected)
    {
        PathPattern.SidecarOf(path).Should().Be(expected);
    }

    [Theory]
    [InlineData(".tier-volume-lock", true)]     // 根层隐藏
    [InlineData(".data.0", true)]               // sidecar 形态
    [InlineData("a/.b", true)]                  // 深层隐藏
    [InlineData("a/.b/c", true)]                // 隐藏子树
    [InlineData("a/b/.c", true)]
    [InlineData("vis", false)]
    [InlineData("a/b/vis", false)]
    [InlineData("a.b/c", false)]                // 点在组件内部不算（与路径规则一致）
    [InlineData("", false)]
    public void IsHiddenRelative_AnyDotComponent(string name, bool expected)
    {
        PathPattern.IsHiddenRelative(name).Should().Be(expected);
    }

    [Theory]
    [InlineData(".*", true)]        // A 方案豁免：首字符 '.'
    [InlineData(".tc.log", true)]
    [InlineData("*", false)]
    [InlineData("tc.log.*", false)]
    public void HiddenExempt_PatternFirstCharDot(string pattern, bool expected)
    {
        PathPattern.HiddenExempt(pattern).Should().Be(expected);
    }

    [Fact]
    public void Validate_EmptyOrNull_Throws()
    {
        ((Action)(() => PathPattern.Validate(null))).Should().Throw<ArgumentException>();
        ((Action)(() => PathPattern.Validate(""))).Should().Throw<ArgumentException>();
        ((Action)(() => PathPattern.Validate("*"))).Should().NotThrow();
    }
}
