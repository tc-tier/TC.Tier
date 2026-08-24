using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Mem;

namespace TC.Tier.Core.Tests.IO.Mem;

/// <summary>
/// OpenRegistryEntry 单元测试——命名类型的值相等性（注销路径 Remove 的正确性基础）与字段语义。
/// </summary>
public sealed class OpenRegistryEntryTests
{
    [Fact]
    public void ValueEquality_SameFields_AreEqual()
    {
        var a = new OpenRegistryEntry(FileSharing.ReadWrite, NeedsRead: true, NeedsWrite: false);
        var b = new OpenRegistryEntry(FileSharing.ReadWrite, NeedsRead: true, NeedsWrite: false);
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ValueEquality_DifferentFields_NotEqual()
    {
        var a = new OpenRegistryEntry(FileSharing.ReadWrite, NeedsRead: true, NeedsWrite: false);
        var b = new OpenRegistryEntry(FileSharing.ReadWrite, NeedsRead: false, NeedsWrite: true);   // 两 bool 互换 = 不同语义
        a.Should().NotBe(b, "NeedsRead/NeedsWrite 互换是不同的登记项——命名类型下显式可见");
    }

    [Fact]
    public void Remove_ByEntry_RemovesExactMatch()
    {
        var registry = new List<OpenRegistryEntry>
        {
            new(FileSharing.ReadWrite, NeedsRead: true, NeedsWrite: true),
            new(FileSharing.Read, NeedsRead: true, NeedsWrite: false),
        };
        var target = new OpenRegistryEntry(FileSharing.Read, NeedsRead: true, NeedsWrite: false);
        registry.Remove(target).Should().BeTrue();
        registry.Should().ContainSingle();
        registry[0].NeedsWrite.Should().BeTrue();
    }
}
