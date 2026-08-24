using TC.Tier.Core.IO;

namespace TC.Tier.Runtime.Benchmarks.Storage;

/// <summary>
/// 基准介质卷——压测统一组合根（与测试 <c>TestVolume</c> 同一条路径）：
/// 介质 = 一根连接字符串（<see cref="TierFs"/> spec），引擎/业务代码零介质分支。
/// <para>★ 介质切换 = 环境变量 <c>TC_BENCH_FS_SPEC</c>（零重编译）：
///   <c>local:///abs/root</c>（真磁盘）/ <c>virtual:///abs/file.raw</c>（Raw）/
///   <c>network:///s3/h/b/p?...</c>（S3）——同一套基准零改动平权压测。</para>
/// <para>★ 默认 <c>memory:</c>（快、免清理）；local 介质每卷自动配唯一子目录
///   （并行基准隔离），Dispose 健壮清理。</para>
/// </summary>
internal sealed class BenchVolume : IDisposable
{
    /// <summary>缺省 spec——环境变量 TC_BENCH_FS_SPEC 覆盖（与测试 TC_TEST_FS_SPEC 同构）。</summary>
    public static string DefaultSpec =>
        Environment.GetEnvironmentVariable("TC_BENCH_FS_SPEC") ?? "memory:";

    /// <summary>卷根空间——引擎寄生其下（段文件相对路径 {engine}/{engine}.{segId}）。</summary>
    public IFileSystem Fs { get; }

    // local 介质的实例目录（Dispose 清理；mem 无此概念）
    private readonly string? _diskDir;

    /// <summary>默认构造——mem 介质（或 TC_BENCH_FS_SPEC 指定的介质）。</summary>
    public BenchVolume() : this(DefaultSpec)
    {
    }

    /// <summary>按 spec 构造（生产组合根同一条路径）。</summary>
    public BenchVolume(string spec)
    {
        if (spec.StartsWith("local", StringComparison.Ordinal))
        {
            // local：每卷唯一子目录（并行卷隔离）——引擎 Dispose 后递归清理
            _diskDir = Path.Combine(Path.GetTempPath(), $"tc-bench-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_diskDir);
            Fs = TierFs.New($"local:///{_diskDir.Replace('\\', '/')}");
        }
        else if (spec.StartsWith("virtual", StringComparison.Ordinal))
        {
            // virtual：每卷唯一 .raw 文件——TierFs.New 格式化显式非幂等，固定路径跨迭代撞"载体已格式化"
            _diskDir = Path.Combine(Path.GetTempPath(), $"tc-bench-{Guid.NewGuid():N}.raw");
            Fs = TierFs.New($"virtual:///{_diskDir.Replace('\\', '/')}");
        }
        else
        {
            Fs = TierFs.New(spec);
        }
    }

    public void Dispose()
    {
        Fs.Dispose();
        if (_diskDir is null) return;
        // 健壮删除：句柄延迟释放重试（10×100ms），失败静默（tmp 目录 OS 自清）
        for (int i = 0; i < 10 && Directory.Exists(_diskDir); i++)
        {
            try { Directory.Delete(_diskDir, recursive: true); }
            catch { Thread.Sleep(100); }
        }
    }
}
