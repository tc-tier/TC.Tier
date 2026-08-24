using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.Tests.IO;

/// <summary>RemoteSpill 契约测试（G7 收编——单一概念两形态：ToDisk / ToMemory；null = 超限 DiskFull）。</summary>
public sealed class RemoteSpillTests
{
    [Fact]
    public void ToDisk_CarriesDirectory_NotMemory()
    {
        var spill = RemoteSpill.ToDisk("X:/tmp");
        spill.Directory.Should().Be("X:/tmp");
        spill.IsMemory.Should().BeFalse();
    }

    [Fact]
    public void ToMemory_CarriesFlag_NullDirectory()
    {
        var spill = RemoteSpill.ToMemory();
        spill.IsMemory.Should().BeTrue();
        spill.Directory.Should().BeNull();
    }

    [Fact]
    public void ToDisk_EmptyOrWhiteSpace_Rejected()
    {
        Assert.Throws<ArgumentException>(() => RemoteSpill.ToDisk(null!));
        Assert.Throws<ArgumentException>(() => RemoteSpill.ToDisk("  "));
    }
}
