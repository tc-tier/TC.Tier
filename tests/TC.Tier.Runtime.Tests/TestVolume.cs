using TC.Tier.Core.IO;

namespace TC.Tier.Runtime.Tests;

/// <summary>
/// 测试介质卷——功能测试统一组合根：介质 = 一根连接字符串（<see cref="TierFs"/> spec）。
/// <para>★ 默认 "memory:"（mem 模拟磁盘：几何基准 512 + DIO 对齐强制 + ExclusiveLock 真锁）——
///   全量 417 项 ~17s（磁盘真 IO 同套 ~12min）。</para>
/// <para>★ 介质切换 = 环境变量 <c>TC_TEST_FS_SPEC</c>（零重编译）：
///   <c>local:///abs/root</c>（真磁盘）/ <c>virtual:///abs/file.tier</c>（TierVolume）/
///   <c>network:///s3/h/b/p?...</c>（S3）——同一套测试零改动平权回归。</para>
/// <para>★ 隔离与清理：mem 每次 spec 新卷天然隔离（Dispose=拔盘）；
///   local 介质每卷自动配唯一子目录（并行/多卷隔离），Dispose 递归清理。</para>
/// </summary>
internal sealed class TestVolume : IDisposable
{
    /// <summary>卷根空间——引擎/旁路读取器共用（段文件相对路径 {engine}/{engine}.{segId}）。</summary>
    public IFileSystem Fs { get; }

    // local 介质的实例目录（Dispose 清理；mem 无此概念）
    private readonly string? _diskDir;

    /// <summary>默认构造——mem 介质（或 TC_TEST_FS_SPEC 指定的介质）。</summary>
    public TestVolume() : this(Environment.GetEnvironmentVariable("TC_TEST_FS_SPEC") ?? "memory:")
    {
    }

    /// <summary>按 spec 构造（生产组合根同一条路径）。</summary>
    public TestVolume(string spec)
    {
        if (spec.StartsWith("local", StringComparison.Ordinal))
        {
            // local：每卷唯一子目录（spec 直传会被并行卷撞根）——New 对空根幂等
            _diskDir = TestTempDir.Create("tc-volume");
            Fs = TierFs.New($"local:///{_diskDir.Replace('\\', '/')}");
        }
        else
        {
            Fs = TierFs.New(spec);
        }
    }

    public void Dispose()
    {
        Fs.Dispose();
        if (_diskDir is not null) TestTempDir.TryCleanup(_diskDir);
    }
}
