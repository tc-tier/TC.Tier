using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Products.Kv;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// TierKv 磁盘介质基线（生产形态——DIO+WriteThrough，真实持久化数字）：
/// <para>★ local 卷临时目录；100k 预填（Committed 全落盘）；点查=页池热区直读；
///   覆写 Committed=页写穿（WT 写即落盘，引擎 Flush 短路）。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*TierKvDisk*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 8)]
public class TierKvDiskBench
{
    private const int PrefillCount = 100_000;

    private IFileSystem? _fs;
    private TierKvOfLongLong? _kv;
    private string? _root;
    private long[] _keys = null!;
    private int _cursor;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _keys = Enumerable.Range(0, PrefillCount).Select(i => (long)i)
            .OrderBy(_ => Random.Shared.Next()).ToArray();
        _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tierkv-disk-bench-" + Guid.NewGuid().ToString("N"));
        _fs = TierFs.New("local:" + _root);   // 磁盘 hints 随 TierKvOptions（DIO+WT）经引擎装配
        var options = TierKvOptions.Default
            .WithKvName("tierkv-disk")
            .WithHints(FileOpenHints.NoBuffering | FileOpenHints.WriteThrough);
        _kv = await TierKvOfLongLong.CreateAsync(_fs, options);
        using var s = _kv.CreateSession(KvSessionConditions.None);
        for (long k = 0; k < PrefillCount; k++)
            await s.PutFormattedAsync(k, k, KvCommitPolicy.Committed);
        await s.CompletePendingAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_kv is not null) await _kv.DisposeAsync();
        _fs?.Dispose();
        if (_root is not null && System.IO.Directory.Exists(_root))
            System.IO.Directory.Delete(_root, recursive: true);
    }

    [Benchmark(Description = "Disk.点查(热区,100k)")]
    public long Get_Hot()
    {
        var key = _keys[_cursor++ % PrefillCount];
        return _kv!.TryGetFormatted(key, out var v) ? v : -1;
    }

    [Benchmark(Description = "Disk.覆写Committed(DIO+WT,100k hot)")]
    public async ValueTask Put_Committed()
    {
        var key = _keys[_cursor++ % PrefillCount];
        await _kv!.PutFormattedAsync(key, key, KvCommitPolicy.Committed);
    }

}
