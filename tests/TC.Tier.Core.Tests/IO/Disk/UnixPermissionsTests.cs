using System.Buffers;
using System.Net.Sockets;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;
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

    // ═══ ApplyInodeMode（#508——bind 产物 inode chmod 面：文件/socket 任意 inode 同语义）═══

    [Fact]
    public void ApplyInodeMode_File_0600_PerPlatformRealBehavior()
    {
        using (var h = _fs.Open("inode.bin", Opts(FileOpenMode.CreateNew)))
        {
            h.Write(0, "x"u8);
        }
        var full = Path.Combine(_dir, "inode.bin");
        var mode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var act = () => _fs.ApplyInodeMode("inode.bin", mode0600);
        if (OperatingSystem.IsWindows())
        {
            act.Should().Throw<NotSupportedException>("Windows 无 POSIX inode 语义——安全请求不静默忽略");
        }
        else
        {
            act.Should().NotThrow();
            File.GetUnixFileMode(full).Should().Be(mode0600, "path-based chmod 对既有文件 inode 生效");
        }
    }

    [Fact]
    public void ApplyInodeMode_MemVolume_NotSupported()
    {
        using var mem = MemoryFileSystem.New();
        mem.CreateFile("f.bin");
        // ★ 意图 = 接口派发命中 DIM 缺省拒绝——具体类型调用会绕过 DIM（CA1859 具体化建议与断言意图冲突，显式 cast）
        var act = () => ((IFileSystem)mem).ApplyInodeMode("f.bin", UnixFileMode.UserRead);
        act.Should().Throw<NotSupportedException>("mem 无真实 inode（DIM 缺省拒绝）——安全权限请求不降级");
    }

    [Fact]
    public void ApplyInodeMode_SocketInode_0600_IpcAuthorizationProof()
    {
        // ★ #508 验收口径：UDS bind → 设 0600 → socket inode 权限位落盘（内核 connect 鉴权的输入铁证）+
        //   跨用户 connect 被内核拒绝（环境门控——root + setpriv + python3 齐备才跑，缺任一显式跳过）。
        if (OperatingSystem.IsWindows()) return;   // POSIX inode 语义仅 Unix——Windows 腿拒绝面由上两用例覆盖
        Assert.True(Directory.Exists(_dir));

        var sockPath = Path.Combine(_dir, "ipc.sock");
        try
        {
        using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            listener.Listen(1);

            // ① socket inode chmod（fs 相对路径——socket 在 fs 根空间内）
            var mode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            _fs.ApplyInodeMode("ipc.sock", mode0600);
            File.GetUnixFileMode(sockPath).Should().Be(mode0600, "path-based chmod 对 socket inode 生效");

            // ② 控制面：属主自身 connect 可达（chmod 未破坏授权路径本身）
            using (var self = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                self.ConnectAsync(new UnixDomainSocketEndPoint(sockPath))
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();   // 属主 connect 必须可达（超时/拒绝即失败）

            // ③ 跨用户铁证（内核层拒绝）——环境门控：root + setpriv + python3
            var gate = CrossUserProbeGate();
            if (!gate.available)
            {
                return;   // 环境缺前置（非 root/无 setpriv/无 python3）——显式跳过（模式断言①②已成立）
            }
            var script = "import socket,sys;s=socket.socket(socket.AF_UNIX);s.connect(sys.argv[1])";
            var (exitCode, _) = RunProcess("setpriv",
                $"--reuid=65534 --regid=65534 --clear-groups python3 -c \"{script}\" {sockPath}", timeoutSec: 15);
            exitCode.Should().NotBe(0, "非授权用户（uid 65534）connect 0600 socket 必须被内核拒绝（EACCES）");
        }
        }
        finally
        {
            try { File.Delete(sockPath); } catch { /* 清理容错 */ }
        }
    }

    /// <summary>跨用户铁证环境门控（root + setpriv + python3——任一缺失则跳过段）。</summary>
    private static (bool available, string reason) CrossUserProbeGate()
    {
        var (idCode, idOut) = RunProcess("id", "-u", timeoutSec: 5);
        if (idCode != 0 || idOut.Trim() != "0") return (false, "非 root——无法降权为 nobody");
        if (RunProcess("which", "setpriv", timeoutSec: 5).exitCode != 0) return (false, "setpriv 缺失");
        if (RunProcess("which", "python3", timeoutSec: 5).exitCode != 0) return (false, "python3 缺失");
        return (true, string.Empty);
    }

    private static (int exitCode, string output) RunProcess(string fileName, string arguments, int timeoutSec)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return (-1, string.Empty);
            if (!p.WaitForExit(timeoutSec * 1000)) { p.Kill(); return (-1, string.Empty); }
            return (p.ExitCode, p.StandardOutput.ReadToEnd());
        }
        catch { return (-1, string.Empty); }
    }
}
