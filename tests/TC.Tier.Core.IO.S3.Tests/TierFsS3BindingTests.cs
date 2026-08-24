using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.IO.S3.Tests;

/// <summary>
/// s3 协议接线测试（medium-protocol-and-parity-design §2.2 二级注册表）——ModuleInitializer 自动注册、
/// spec → S3ObjectStore + RemoteFileSystem 组装、cred=env: 引用解析（缺失/畸形 fail-fast）、spill 二态映射。
/// RemoteFileSystem.Create 零网络——无需真实端点即可全链路验证组装。
/// </summary>
public sealed class TierFsS3BindingTests : IDisposable
{
    private const string EnvName = "TIERFS_S3_BINDING_TEST";
    private readonly List<string> _tempDirs = [];   // 2026-08-20 修复：此前零 Dispose——目录全残留

    // 强制加载 S3 程序集 → ModuleInitializer 注册 s3 协议（测试断言均为 Core 类型，不触碰则程序集不加载）
    static TierFsS3BindingTests() => GC.KeepAlive(S3ProtocolBuilder.Instance);

    private string TempDir()
    {
        var dir = TestTempDir.Create("tierfs-s3");
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs) TestTempDir.TryCleanup(dir);
    }

    [Fact]
    public void ModuleInitializer_AutoRegistersS3()
    {
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "AKID:SECRET");
            using var fs = TierFs.Open($"network:///s3/localhost:9000/bkt/pfx?tls=0&cred=env:{EnvName}");
            Assert.IsType<RemoteFileSystem>(fs);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
        }
    }

    [Fact]
    public void FullSurface_PrefixMapped()
    {
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "AKID:SECRET");
            using var fs = (RemoteFileSystem)TierFs.Open(
                $"network:///s3/cos.example.com/tc-bucket/engine-a?vhost=1&region=cn-chengdu&cred=env:{EnvName}");
            Assert.Equal("engine-a", fs.Options.KeyPrefix);
            Assert.Null(fs.Options.Spill);   // G7 收编：单一 Spill 概念（null = 不配置）
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
        }
    }

    [Fact]
    public void CredentialRef_MissingEnv_FailFast()
    {
        Environment.SetEnvironmentVariable(EnvName, null);
        var ex = Assert.Throws<NotSupportedException>(
            () => TierFs.Open($"network:///s3/h/b/p?tls=0&cred=env:{EnvName}"));
        Assert.Contains(EnvName, ex.Message);
    }

    [Fact]
    public void CredentialRef_MalformedValue_FailFast()
    {
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "no-separator");
            var ex = Assert.Throws<NotSupportedException>(
                () => TierFs.Open($"network:///s3/h/b/p?tls=0&cred=env:{EnvName}"));
            Assert.Contains("accessKey:secretKey", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
        }
    }

    [Fact]
    public void CredentialRef_Absent_FailFast()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TierFs.Open("network:///s3/h/b/p?tls=0"));
        Assert.Contains("cred=env:", ex.Message);
    }

    [Fact]
    public void Spill_LocalDirectory_And_Memory_Mapped()
    {
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "AKID:SECRET");
            var spillDir = TempDir().Replace('\\', '/');

            var byDir = (RemoteFileSystem)TierFs.Open(
                $"network:///s3/h/b/p?tls=0&cred=env:{EnvName}&spill=local:///{spillDir}");
            Assert.Equal(spillDir, byDir.Options.Spill!.Directory);   // spec 层归一 /（翻译岗），.NET 路径 API 通吃
            byDir.Dispose();

            var byMem = (RemoteFileSystem)TierFs.Open(
                $"network:///s3/h/b/p?tls=0&cred=env:{EnvName}&spill=memory:");
            Assert.True(byMem.Options.Spill!.IsMemory);
            byMem.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
        }
    }

    [Fact]
    public void AccessRo_Implemented_Enforced()
    {
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "AKID:SECRET");
            using var fs = TierFs.Open($"network:///s3/h/b/p?tls=0&cred=env:{EnvName}&access=ro");
            Assert.Throws<FileIOException>(() => fs.CreateFile("a"));   // G2 已生效：ro 写族拒绝
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
        }
    }

    // exclusive 已生效（G5：fencing 构造期抢建）——离线无法验证真 fencing（需真实端点）；
    // 行为路径经 Core 侧 fake 协议与 MinIO 契约套覆盖（run-minio-tests.sh）。
}
