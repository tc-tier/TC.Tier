using BenchmarkDotNet.Attributes;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.Testing;

// ★ CA1001 抑制：BDN 基准实例由 harness 生命周期管理（全局/迭代期资源，无 Dispose 时机）
#pragma warning disable CA1001

namespace TC.Tier.Core.Benchmarks.IO;

/// <summary>
/// 远程介质专项压测（B3.5，§7.3 基线）——MemoryObjectStore 底座测<b>桥层 CPU 管线</b>
/// （staging 组装/回填合并/spill 切换）；网络层（真 S3/MinIO）延迟与吞吐见
/// <c>scripts/run-minio-tests.sh</c> + probe 模式（`-- --list-flat` 选跑）。
/// <para>★ 基线项：Flush 吞吐 × PartSize 曲线 / staging 内存→spill 切换的 Flush 延迟突变（无悬崖验证）。</para>
/// </summary>
[MemoryDiagnoser]
public class RemoteIoBenchmarks
{
    [Params(8, 16, 32)]
    public int PartSizeMB { get; set; }

    private MemoryObjectStore _store = null!;
    private RemoteFileSystem _fs = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _fs = RemoteFileSystem.OpenOrCreate(_store, new RemoteFileSystemOptions
        {
            PartSize = PartSizeMB * 1024L * 1024,
            MultipartThreshold = 1024 * 1024,   // 全走 multipart 路径
            StagingMemoryLimit = 256L * 1024 * 1024,   // 全内存（spill 由专门基准切换）
        });
        _payload = new byte[64 * 1024 * 1024];   // 64MiB 负载
        new Random(42).NextBytes(_payload);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fs.Dispose();
        _store.Dispose();
    }

    /// <summary>Flush 吞吐：64MiB 追加写入 → Flush（multipart 组装 + 上传管线）——MB/s 口径。</summary>
    [Benchmark]
    public long FlushThroughput_Multipart()
    {
        using var h = _fs.Open($"bench-{Guid.NewGuid():N}", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate });
        var pos = 0L;
        while (pos < _payload.Length)
        {
            h.Append(_payload.AsSpan((int)pos, 64 * 1024));
            pos += 64 * 1024;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        h.Flush();
        sw.Stop();
        return _payload.Length / Math.Max(1, sw.ElapsedMilliseconds);   // KB/ms ≈ MB/s（防死代码消除）
    }

    /// <summary>部分改写 Flush：旧对象上随机覆写 1/8 → Flush（回填 7/8 服务端拷贝路径）。</summary>
    [Benchmark]
    public long FlushPartialRewrite_BackfillDominated()
    {
        var name = $"pr-{Guid.NewGuid():N}";
        using (var seed = _fs.Open(name, new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }))
        {
            seed.Write(0, _payload);
            seed.Flush();
        }
        using var h = _fs.Open(name, new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate });
        // 覆写第 1 个与最后 1 个 8MiB 段——中间未触（回填主导）
        h.Write(0, _payload.AsSpan(0, 8 * 1024 * 1024));
        h.Write(_payload.Length - 8 * 1024 * 1024, _payload.AsSpan(_payload.Length - 8 * 1024 * 1024, 8 * 1024 * 1024));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        h.Flush();
        sw.Stop();
        return _payload.Length / Math.Max(1, sw.ElapsedMilliseconds);
    }
}

/// <summary>staging 内存→spill 切换的 Flush 延迟突变（§7.3——验证无悬崖）。</summary>
[MemoryDiagnoser]
public class RemoteSpillBenchmarks
{
    [Params(64, 256)]   // MiB 负载：64 超 32MiB 预算（spill 路径）/ 256 重 spill
    public int PayloadMB { get; set; }

    private MemoryObjectStore _store = null!;
    private RemoteFileSystem _fs = null!;
    private string _dir = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tier-bench-spill-{Guid.NewGuid():N}");
        _store = new MemoryObjectStore();
        _fs = RemoteFileSystem.OpenOrCreate(_store, new RemoteFileSystemOptions
        {
            StagingMemoryLimit = 32L * 1024 * 1024,   // 32MiB 预算——spill 强制启用
            StagingPageSize = 64 * 1024,
            Spill = RemoteSpill.ToDisk(_dir),
        });
        _payload = new byte[PayloadMB * 1024 * 1024];
        new Random(7).NextBytes(_payload);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fs.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* 尽力清理 */ }
    }

    /// <summary>超预算写入 + Flush——spill 落盘/回读路径下的端到端吞吐（对比全内存基线无悬崖）。</summary>
    [Benchmark]
    public long WriteAndFlush_WithSpill()
    {
        using var h = _fs.Open($"sp-{Guid.NewGuid():N}", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate });
        var pos = 0L;
        while (pos < _payload.Length)
        {
            h.Append(_payload.AsSpan((int)pos, 256 * 1024));
            pos += 256 * 1024;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        h.Flush();
        sw.Stop();
        return _payload.Length / Math.Max(1, sw.ElapsedMilliseconds);
    }
}
