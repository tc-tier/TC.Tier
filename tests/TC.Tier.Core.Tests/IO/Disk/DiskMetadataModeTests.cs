using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;

namespace TC.Tier.Core.Tests.IO.Disk;

/// <summary>
/// DiskMetadataMode 单元测试——元数据存储模式的构造配置路由（枚举直传 Create）
/// （filesystem-root-space-design §3.6 修订：部署决策，非逐文件隐式判定）。
/// </summary>
public sealed class DiskMetadataModeTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-disk-meta");

    private DiskFileSystem NewFs(DiskMetadataMode mode)
    {
        var fs = DiskFileSystem.OpenOrCreate(_dir, new DiskFileSystemOptions { MetadataMode = mode });
        fs.EnsureRoot();
        return fs;
    }

    public void Dispose()
        => TestTempDir.TryCleanup(_dir);

    [Fact]
    public void Default_IsFallback()
    {
        using var fs = DiskFileSystem.OpenOrCreate(_dir);
        fs.CreateFile("dflt", extra: new byte[] { 7 });   // 缺省模式 Fallback 下正常写入读回
        fs.Stat("dflt").FileExtra.ToArray().Should().Equal(7);
    }

    [Fact]
    public void Sidecar_Mode_PhysicalSidecarFile_HiddenInEnumeration_BoundLifecycle()
    {
        using var fs = NewFs(DiskMetadataMode.Sidecar);
        var meta = new byte[] { 1, 2, 3, 4, 5 };
        fs.CreateFile("s0", extra: meta);

        // 物理伴生文件存在（同目录点前缀）且内容即元数据（tmp 原子换名不留残留）
        var sidecarPath = Path.Combine(_dir, ".s0");
        File.Exists(sidecarPath).Should().BeTrue();
        File.ReadAllBytes(sidecarPath).Should().Equal(meta);
        File.Exists(sidecarPath + ".tmp").Should().BeFalse("原子换名后无 tmp 残留");

        // Stat 读回（单通道 sidecar）
        fs.Stat("s0").FileExtra.ToArray().Should().Equal(meta);

        // 枚举隐藏配对 sidecar（.s0 因 s0 存在而不可见）
        fs.EnumerateFiles("*").Select(e => e.Name).Should().NotContain(".s0");

        // 生命周期绑定：Delete 主文件同删 sidecar
        fs.Delete("s0");
        File.Exists(sidecarPath).Should().BeFalse();
    }

    [Fact]
    public void Sidecar_Mode_Move_BindsSidecar()
    {
        using var fs = NewFs(DiskMetadataMode.Sidecar);
        fs.CreateFile("m0", extra: new byte[] { 9 });
        fs.Move("m0", "m1");
        File.Exists(Path.Combine(_dir, ".m0")).Should().BeFalse("源 sidecar 随迁");
        File.Exists(Path.Combine(_dir, ".m1")).Should().BeTrue();
        fs.Stat("m1").FileExtra.ToArray().Should().Equal(9);
    }

    [Fact]
    public void ExtendedAttr_Mode_WorksWithChannel()
    {
        // NTFS ADS / Linux xattr 可用环境：写入走通道（无 sidecar 物理文件），读回一致
        using var fs = NewFs(DiskMetadataMode.ExtendedAttr);
        var meta = new byte[] { 0x0A, 0x0B };
        fs.CreateFile("x0", extra: meta);
        File.Exists(Path.Combine(_dir, ".x0")).Should().BeFalse("xattr 模式不产生 sidecar");
        fs.Stat("x0").FileExtra.ToArray().Should().Equal(meta);
    }

    [Fact]
    public void FileExtra_OverLimit_Rejected()
    {
        using var fs = NewFs(DiskMetadataMode.Sidecar);
        var act = () => fs.CreateFile("big", extra: new byte[IFileSystem.MaxFileExtraBytes + 1]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ChannelKeyName_SingleSource()
    {
        // 键名为实现细节（契约无键概念）——单一事实源 FileNative.XattrName（Mem 槽字段无键、Remote 引用同源）
        FileNative.XattrName.Should().Be("TC_TIER");
    }

    [Fact]
    public void SpanRead_ZeroMetadata_ReturnsEmpty()
    {
        using var fs = NewFs(DiskMetadataMode.Sidecar);
        fs.CreateFile("no-meta");
        fs.Stat("no-meta").FileExtra.Length.Should().Be(0);
    }
}
