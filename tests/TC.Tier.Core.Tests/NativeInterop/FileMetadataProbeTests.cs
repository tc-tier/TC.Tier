using System.Runtime.InteropServices;

namespace TC.Tier.Core.Tests.NativeInterop;

/// <summary>
/// 文件元数据（xattr/ADS）可用性探测——验证"不增文件不改布局，用 4K 系统元数据记几个值"可行。
/// <para>验证项：能否写入/读回/持久化，容量是否够（几个 long），跨平台支持情况。</para>
/// </summary>
public sealed class FileMetadataProbeTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose() { foreach (var d in _dirs) TestTempDir.TryCleanup(d); }

    private string NewPath()
    {
        var dir = TestTempDir.Create("tc-xattr");
        _dirs.Add(dir);
        return Path.Combine(dir, "meta.dat");
    }

    // ═══════════════════════════════════════════
    // Windows: NTFS Alternate Data Stream (ADS)
    // 路径 "file.dat:streamname" 即一个 ADS
    // ═══════════════════════════════════════════

    [Fact]
    public void M1_Windows_ADS_WriteReadBack()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var path = NewPath();
        File.WriteAllText(path, "data");
        var adsPath = path + ":tc_meta";

        // 写 ADS：几个 long（maxOffset + growthLimit + segId）
        using (var fs = new FileStream(adsPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(42L);              // segId
            bw.Write(256L * 1024 * 1024); // growthLimit
            bw.Write(100L * 1024 * 1024); // maxOffset
        }

        // 读回
        using (var fs = new FileStream(adsPath, FileMode.Open, FileAccess.Read))
        using (var br = new BinaryReader(fs))
        {
            br.ReadInt64().Should().Be(42, "segId 读回");
            br.ReadInt64().Should().Be(256L * 1024 * 1024, "growthLimit 读回");
            br.ReadInt64().Should().Be(100L * 1024 * 1024, "maxOffset 读回");
        }

        // 文件主体不变
        File.ReadAllText(path).Should().Be("data", "ADS 不改数据区");
    }

    [Fact]
    public void M2_Windows_ADS_SurvivesReopen()
    {
        if (!IsRunningOnWindows()) return;
        var path = NewPath();
        File.WriteAllText(path, "data");
        var adsPath = path + ":tc_meta";

        using (var fs = new FileStream(adsPath, FileMode.Create, FileAccess.Write))
            fs.Write(new byte[] { 0xAA, 0xBB, 0xCC });

        // 模拟"重启"：关闭后重开读
        using (var fs = new FileStream(adsPath, FileMode.Open, FileAccess.Read))
        {
            var buf = new byte[3];
            fs.ReadExactly(buf, 0, 3);
            buf.Should().Equal(new byte[] { 0xAA, 0xBB, 0xCC }, "ADS 重开后持久化");
        }
    }

    private static bool IsRunningOnWindows() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void M3_FileSize_Unaffected_By_ADS()
    {
        if (!IsRunningOnWindows()) return;
        var path = NewPath();
        File.WriteAllText(path, "12345");  // 5 bytes
        var lenBefore = new FileInfo(path).Length;
        lenBefore.Should().Be(5);

        // 写 ADS
        var adsPath = path + ":tc_meta";
        using (var fs = new FileStream(adsPath, FileMode.Create, FileAccess.Write))
            fs.Write(new byte[4096]);  // 写 4K 到 ADS

        // FileInfo.Length 不变（ADS 不计入主流大小）
        new FileInfo(path).Length.Should().Be(5,
            "ADS 不影响 FileInfo.Length——不撑大 Length，不改布局");
    }

    // ═══════════════════════════════════════════
    // Linux: xattr (setxattr/getxattr)
    // ═══════════════════════════════════════════

    [Fact]
    public void M4_Linux_XAttr_WriteReadBack()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        var path = NewPath();
        File.WriteAllText(path, "data");

        // 走真实实现：FileNative.WriteFileMeta（LibC.Setxattr）→ ReadFileMeta（Getxattr）读回校验
        var meta = new byte[] { 42, 7, 1, 2, 3, 4, 5, 6, 7, 8 };
        FileNative.WriteFileMeta(path, meta).Should().BeTrue("Linux ext4 应支持 user.xattr 写入");

        var readBack = FileNative.ReadFileMeta(path);
        readBack.Should().NotBeNull("xattr 应可读回");
        readBack.Should().Equal(meta, "xattr 读回应与写入一致");

        FileNative.DeleteFileMeta(path);
        FileNative.ReadFileMeta(path).Should().BeNull("删除后读回应为 null");
    }
}
