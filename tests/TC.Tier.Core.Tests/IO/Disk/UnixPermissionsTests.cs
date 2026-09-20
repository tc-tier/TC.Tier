using System.Buffers;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using Xunit;

namespace TC.Tier.Core.Tests.IO.Disk;

/// <summary>
/// Unix 文件权限位公开面（#493——密钥 0600 场景）：创建期 <see cref="FileOpenOptions.UnixPermissions"/>
/// 生效语义（仅本次创建应用；OpenOrCreate 既有文件不复写）+ 能力位协商（Unix Disk 置位 / Windows Disk /
/// Mem 不置位——未置位实现显式请求抛 Unsupported，安全权限绝不静默忽略）。
/// <para>★ 平台差异 ≠ Skip——分平台断言各自真实行为（Unix 腿断言权限落盘；Windows 腿断言 Unsupported 拒绝）。</para>
/// </summary>
public sealed class UnixPermissionsTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-unix-perm");
    private readonly DiskFileSystem _fs;

    public UnixPermissionsTests()
    {
        _fs = DiskFileSystem.OpenOrCreate(_dir);
        _fs.EnsureRoot();
    }

    public void Dispose()
    {
        _fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static FileOpenOptions Opts(FileOpenMode mode, UnixFileMode? permissions = null) => new()
    {
        Access = AccessMode.ReadWrite, Mode = mode, Sharing = FileSharing.ReadWrite,
        UnixPermissions = permissions,
    };

    // ═══ 能力位协商 ═══

    [Fact]
    public void Capabilities_UnixDiskSet_WindowsDiskUnset_PerPlatform()
    {
        var hasBit = _fs.Capabilities.HasFlag(FileSystemCapabilities.UnixPermissions);
        if (OperatingSystem.IsWindows())
            hasBit.Should().BeFalse("Windows 无 POSIX 权限语义——能力位不置（显式请求走 Unsupported 拒绝）");
        else
            hasBit.Should().BeTrue("Linux/macOS Disk 支持 POSIX 权限位");
    }

    // ═══ 创建期生效（分平台双腿——各自真实行为）═══

    [Fact]
    public void Open_CreateNew_With0600_PerPlatformRealBehavior()
    {
        var act = () =>
        {
            using var h = _fs.Open("keys.bin", Opts(FileOpenMode.CreateNew, UnixFileMode.UserRead | UnixFileMode.UserWrite));
            h.Write(0, "payload"u8);
        };

        if (OperatingSystem.IsWindows())
        {
            act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.Unsupported,
                "Windows 能力位未置位——安全权限请求显式拒绝，绝不静默忽略");
            return;
        }

        act.Should().NotThrow();
        var mode = File.GetUnixFileMode(Path.Combine(_dir, "keys.bin"));
        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "创建期权限位应用——密钥 0600 形态");
    }

    [Fact]
    public void Open_OpenOrCreate_NewlyCreated_AppliesPermissions_Unix()
    {
        if (OperatingSystem.IsWindows())
            return;   // 创建期应用语义 Unix 腿专测（Windows 行为由 Unsupported 腿覆盖）

        using (var h = _fs.Open("created.bin", Opts(FileOpenMode.OpenOrCreate, UnixFileMode.UserRead | UnixFileMode.UserWrite)))
        {
            h.Write(0, "x"u8);
        }

        File.GetUnixFileMode(Path.Combine(_dir, "created.bin"))
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "OpenOrCreate 本次创建 → 权限应用");
    }

    [Fact]
    public void Open_OpenOrCreate_PreExistingFile_NotReapplied_Unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        // 先以 0644 显式创建（确定性基线——不依赖 umask），再以 0600 请求重开既有文件：
        // 文件归首发创建方所有，本次未创建 → 不应用（防"重开静默改权限"的意外收权/放权）。
        using (var h = _fs.Open("existing.bin", Opts(FileOpenMode.CreateNew,
                   UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)))
        {
            h.Write(0, "x"u8);
        }

        using (var h = _fs.Open("existing.bin", Opts(FileOpenMode.OpenOrCreate, UnixFileMode.UserRead | UnixFileMode.UserWrite)))
        {
            h.Write(0, "y"u8);
        }

        File.GetUnixFileMode(Path.Combine(_dir, "existing.bin"))
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                "本次未创建（文件已存在）→ 权限位不应用");
    }

    // ═══ 未置位介质：显式请求拒绝（跨平台恒定——含 Windows/Mem 双形态）═══

    [Fact]
    public void Mem_Open_WithPermissions_ThrowsUnsupported_AllPlatforms()
    {
        using var mem = TierFs.New("memory:");
        var act = () => _ = mem.Open("k", Opts(FileOpenMode.CreateNew, UnixFileMode.UserRead | UnixFileMode.UserWrite));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.Unsupported,
            "内存介质无 POSIX 权限语义——安全权限请求不降级");
    }
}
