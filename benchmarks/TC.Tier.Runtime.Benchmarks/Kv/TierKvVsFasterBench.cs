using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FASTER.core;
using TC.Tier.Products.Kv;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// TierKv 产品面 vs FASTER 2.6.5 同形对照（W6 性能基线收尾——产品 API 口径，非组合层）：
/// FasterKV&lt;long,long&gt; 纯内存 hlog（NullDevice）+ SimpleFunctions vs TierKvOfLongLong
/// mem 卷——同进程同轮同形（100k 预填 long:long，随机游走键）。
/// <para>★ 与 <see cref="FasterHotReadBench"/> 的区别：那是对组合层两段（index.Find+ring.GetValue），
/// 本类对产品 API 全链（会话/提交档/解帧/formatter——用户真实拿到的形态）。</para>
/// <para>★ 范围扫描无对标项：FASTER 无范围扫描能力（tierkv-design.md §0.1），Scan 为 TierKv 独有。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*TierKvVsFaster*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 12)]
public class TierKvVsFasterBench : IDisposable
{
    private const int PrefillCount = 100_000;

    // ── TierKv 侧（mem 卷——TierKv 侧全内存口径）──
    private IFileSystem? _fs;
    private TierKvOfLongLong? _kv;

    // ── FASTER 侧（NullDevice 纯内存 hlog）──
    private FasterKV<long, long>? _faster;
    private ClientSession<long, long, long, long, Empty, IFunctions<long, long, long, long, Empty>>? _fsession;

    private long[] _keys = null!;
    private int _cursor;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _keys = Enumerable.Range(0, PrefillCount).Select(i => (long)i)
            .OrderBy(_ => Random.Shared.Next()).ToArray();

        // FASTER：内存 hlog，100k Upsert 后热读/热写
        _faster = new FasterKV<long, long>(
            1L << 20,
            new FASTER.core.LogSettings
            {
                LogDevice = new NullDevice(),
                MemorySizeBits = 22,      // 4M×槽远超 100k 条——全内存
                PageSizeBits = 21,
            });
        _fsession = _faster.NewSession(new SimpleFunctions<long, long>());
        for (long k = 0; k < PrefillCount; k++)
            _fsession.Upsert(k, k);
        _fsession.Refresh();

        // TierKv：mem 卷，同形 100k
        _fs = TierFs.New("memory:");
        _kv = await TierKvOfLongLong.CreateAsync(_fs,
            TierKvOptions.Default.WithKvName("bench-tierkv-vs-faster"));
        using var s = _kv.CreateSession(KvSessionConditions.None);
        for (long k = 0; k < PrefillCount; k++)
            await s.PutFormattedAsync(k, k, policy: KvCommitPolicy.FireAndForget);
        await s.CompletePendingAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _fsession?.Dispose();
        _faster?.Dispose();
        if (_kv is not null) await _kv.DisposeAsync();
        _fs?.Dispose();
    }

    // ═══ 点查（随机热读——产品 API 全链）═══

    [Benchmark(Baseline = true, Description = "FASTER.Read(product,100k hot)")]
    public long Faster_Get()
    {
        var key = _keys[_cursor++ % PrefillCount];
        var (_, output) = _fsession!.Read(key);
        return output;
    }

    [Benchmark(Description = "TierKv.TryGetFormatted(product,100k hot)")]
    public async ValueTask<long> TierKv_Get()
    {
        var key = _keys[_cursor++ % PrefillCount];
        var (found, value) = await _kv!.TryGetFormattedAsync(key);
        return found ? value : -1;
    }

    // ═══ 覆写（Upsert——TierKv F&F 口径不刷盘 ≙ FASTER 内存 Upsert）═══

    [Benchmark(Description = "FASTER.Upsert(product,100k hot)")]
    public void Faster_Upsert()
    {
        var key = _keys[_cursor++ % PrefillCount];
        _fsession!.Upsert(key, key);
    }

    [Benchmark(Description = "TierKv.PutFormatted(FF,100k hot)")]
    public ValueTask<LogicalAddress> TierKv_Upsert()
    {
        var key = _keys[_cursor++ % PrefillCount];
        return _kv!.PutFormattedAsync(key, key, policy: KvCommitPolicy.FireAndForget);
    }

    // ═══ 同步热路径对照（零 async 零分配）═══

    [Benchmark(Description = "TierKv.TryGetFormattedSync(sync,100k hot)")]
    public long TierKv_GetSync()
    {
        var key = _keys[_cursor++ % PrefillCount];
        return _kv!.TryGetFormatted(key, out var v) ? v : -1;
    }

    [Benchmark(Description = "TierKv.PutFormattedSync(sync,100k hot)")]
    public LogicalAddress TierKv_UpsertSync()
    {
        var key = _keys[_cursor++ % PrefillCount];
        return _kv!.PutFormatted(key, key);
    }

    /// <summary>BDN 基准生命周期收口（GlobalCleanup 阶段自动调用；字段释放随 GlobalCleanup 域）。</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
