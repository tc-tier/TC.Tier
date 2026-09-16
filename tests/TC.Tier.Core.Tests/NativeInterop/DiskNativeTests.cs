namespace TC.Tier.Core.Tests.NativeInterop;

/// <summary>DiskNative 静态工具守卫测试。</summary>
public class DiskNativeTests
{
    [Fact]
    public void GetSectorSize_NullPath_ThrowsArgumentNull()
    {
        Action act = () => DiskNative.GetSectorSize(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetSectorSize_EmptyPath_ThrowsArgumentEmpty()
    {
        Action act = () => DiskNative.GetSectorSize(string.Empty);
        act.Should().Throw<ArgumentException>();
    }
}
