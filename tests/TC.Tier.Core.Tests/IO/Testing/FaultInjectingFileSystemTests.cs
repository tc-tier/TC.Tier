using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Core.Tests.IO.Testing;

/// <summary>
/// FaultInjectingFileSystem 单元测试——路径×操作×概率三维注入 + 确定性第 N 次注入（Append 失败语义① /
/// CopyRange 部分失败㉘的测试载体）。
/// </summary>
public sealed class FaultInjectingFileSystemTests
{
    private static FileOpenOptions Opts() =>
        new() { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate };

    [Fact]
    public void Inject_ByPathAndOperation_Selective()
    {
        using var inner = MemoryFileSystem.New();
        using var fs = new FaultInjectingFileSystem(inner);
        fs.AddRule("victim", "Write", IOError.IOFailure);

        using (var ok = fs.Open("other", Opts()))
        {
            var act = () => ok.Write(0, new byte[8]);
            act.Should().NotThrow("路径不匹配——放行");
        }
        using (var victim = fs.Open("victim", Opts()))
        {
            var act = () => victim.Write(0, new byte[8]);
            act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.IOFailure);
            // Read 不匹配操作——放行
            ((Action)(() => victim.Read(0, new byte[8]))).Should().NotThrow();
        }
    }

    [Fact]
    public void Inject_WildcardMatchesEverything()
    {
        using var inner = MemoryFileSystem.New();
        using var fs = new FaultInjectingFileSystem(inner);
        fs.AddRule("*", "Delete", IOError.AccessDenied);
        fs.Open("a", Opts()).Dispose();
        var act = () => fs.Delete("a");
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AccessDenied);
    }

    [Fact]
    public void Inject_ProbabilityZero_NeverFires()
    {
        using var inner = MemoryFileSystem.New();
        using var fs = new FaultInjectingFileSystem(inner);
        fs.AddRule("*", "*", IOError.IOFailure, probability: 0);
        using var h = fs.Open("a", Opts());
        for (var i = 0; i < 100; i++)
        {
            var act = () => h.Write(0, new byte[4]);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Inject_DeterministicNthCall_FailsExactlyOnce()
    {
        using var inner = MemoryFileSystem.New();
        using var fs = new FaultInjectingFileSystem(inner);
        var rule = fs.AddRule("a", "Write", IOError.DiskFull, failAtCallIndex: 3);

        using var h = fs.Open("a", Opts());
        h.Write(0, new byte[1]);   // #1 放行
        h.Write(0, new byte[1]);   // #2 放行
        ((Action)(() => h.Write(0, new byte[1]))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.DiskFull);   // #3 注入
        ((Action)(() => h.Write(0, new byte[1]))).Should().NotThrow();   // #4 放行（只注入一次）
        Volatile.Read(ref rule.MatchCount).Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Inject_AppendFailure_OnDisk()
    {
        // ① Append 失败语义在磁盘介质上的注入载体验证（ReservedOffset 全语义已由 mem 配额测试覆盖——
        //   装饰器在 Append 级拦截，发生在内层预留之前）
        var dir = TestTempDir.Create("core-io-fi");
        using var inner = DiskFileSystem.OpenOrCreate(dir);
        inner.EnsureRoot();
        using var fs = new FaultInjectingFileSystem(inner);
        fs.AddRule("*", "Append", IOError.IOFailure, failAtCallIndex: 2);

        using var h = fs.Open("log", Opts());
        h.Append(new byte[8]).Should().Be(0);   // Append #1 放行
        var ex = Assert.Throws<FileIOException>(() => h.Append(new byte[8]));   // Append #2 注入
        ex.Error.Should().Be(IOError.IOFailure);
        h.Length.Should().Be(8);
        ((Action)(() => h.Append(new byte[4]))).Should().NotThrow();   // #3 起放行——句柄不废止

        TestTempDir.TryCleanup(dir);
    }

    [Fact]
    public void Inject_ClearRules_StopsInjection()
    {
        using var inner = MemoryFileSystem.New();
        using var fs = new FaultInjectingFileSystem(inner);
        using var h = fs.Open("a", Opts());   // 先开句柄（再上规则——Open 也被 "*" 拦截）
        fs.AddRule("*", "*", IOError.IOFailure);
        ((Action)(() => h.Write(0, new byte[1]))).Should().Throw<FileIOException>();
        fs.ClearRules();
        ((Action)(() => h.Write(0, new byte[1]))).Should().NotThrow();
    }

    [Fact]
    public void Inject_NamespaceOperations_Covered()
    {
        using var inner = MemoryFileSystem.New();
        using var fs = new FaultInjectingFileSystem(inner);
        fs.AddRule("*", "Move", IOError.SharingViolation);
        fs.Open("a", Opts()).Dispose();
        var act = () => fs.Move("a", "b");
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
    }

    [Fact]
    public void Decorator_ForwardsCapabilitiesAndVolume()
    {
        using var inner = MemoryFileSystem.New(new MemoryFileSystemOptions { QuotaBytes = 1 << 20 });
        using var fs = new FaultInjectingFileSystem(inner);
        fs.Capabilities.Should().Be(inner.Capabilities);
        fs.Volume.TotalSpace.Should().Be(1 << 20);
    }
}
