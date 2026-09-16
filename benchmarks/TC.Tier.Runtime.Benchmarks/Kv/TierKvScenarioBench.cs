using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Products.Kv;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// TierKv 场景矩阵基准（W-Hot 后性能分析——补齐并发/提交档/原子批/RMW/规模/扫描深度）：
/// <para>★ 全部产品 API 口径（TierKvOfLongLong，缺省 Hash 主索引）；标注场景显式列出介质/预填/会话数。</para>
/// <para>★ ShortRun 精度（3 warmup / 5 iter）——全景对比用，关键项另行精跑。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*TierKvScenario*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class TierKvScenarioBench
{
    private const int PrefillCount = 100_000;

    private IFileSystem? _fs;
    private TierKvOfLongLong? _kv;
    private KvSession<long, long>? _session;
    private KvSession<long, long>[]? _sessions;
    private long[] _keys = null!;
    private int _cursor;

    // ═══════════ 并发扩展（mem，100k 预填，8 会话同写/同读/混合 70 读 30 写）═══════════

    [Params(1, 8)]
    public int Sessions { get; set; }

    [GlobalSetup(Target = nameof(Concurrent_Get))]
    public void Setup_ConcurrentGet() => SetupSync(prefill: PrefillCount);

    [Benchmark(Description = "并发点查(8线程随机热)")]
    public long Concurrent_Get()
    {
        var total = 0L;
        Parallel.For(0, Sessions, w =>
        {
            var s = _sessions![w];
            for (var i = 0; i < 64; i++)
            {
                var (_, v) = s.TryGetFormattedAsync(_keys[(w * 64 + i) % PrefillCount]).AsTask().GetAwaiter().GetResult();
                total += v;
            }
        });
        return total;
    }

    [GlobalSetup(Target = nameof(Concurrent_Put))]
    public void Setup_ConcurrentPut() => SetupSync(prefill: PrefillCount);

    [Benchmark(Description = "并发写(8线程F&F新key)")]
    public long Concurrent_Put()
    {
        var total = 0L;
        Parallel.For(0, Sessions, w =>
        {
            var s = _sessions![w];
            for (var i = 0; i < 64; i++)
            {
                var k = 1_000_000L + w * 10_000 + i;
                s.PutFormattedAsync(k, k, KvCommitPolicy.FireAndForget).AsTask().GetAwaiter().GetResult();
                total += 1;
            }
        });
        return total;
    }

    // ═══════════ 提交档位（mem 介质——F&F vs Committed 组提交）═══════════

    [GlobalSetup(Target = nameof(Put_Committed_Mem))]
    public void Setup_CommittedMem() => SetupSync(prefill: PrefillCount);

    [Benchmark(Description = "覆写 Committed 档(mem 组提交)")]
    public ValueTask<LogicalAddress> Put_Committed_Mem()
    {
        var key = _keys[_cursor++ % PrefillCount];
        return _kv!.PutFormattedAsync(key, key, KvCommitPolicy.Committed);
    }

    // ═══════════ 原子批 vs 逐条（mem，16 条一批）═══════════

    [GlobalSetup(Target = nameof(Batch_16))]
    public void Setup_Batch() => SetupSync(prefill: PrefillCount);

    [Benchmark(Description = "原子批 16 条(全或无)")]
    public async ValueTask Batch_16()
    {
        using var s = _kv!.CreateSession(KvSessionConditions.None);
        s.BeginAtomicBatch();
        for (long i = 0; i < 16; i++)
            await s.PutFormattedAsync(5_000_000L + i, i, KvCommitPolicy.FireAndForget);
        await s.CommitBatchAsync();
    }

    [Benchmark(Description = "逐条 16 条(无原子性)")]
    public async ValueTask Single_16()
    {
        using var s = _kv!.CreateSession(KvSessionConditions.None);
        for (long i = 0; i < 16; i++)
            await s.PutFormattedAsync(5_000_000L + i, i, KvCommitPolicy.FireAndForget);
    }

    // ═══════════ RMW 三流（mem，100k 预填）═══════════

    private CounterFunctions _counterFunctions = new();

    [GlobalSetup(Target = nameof(Rmw_InitialOrCopy))]
    public void Setup_Rmw() => SetupSync(prefill: PrefillCount);

    [Benchmark(Description = "RMW(命中 Copy 流+3 解析)")]
    public async ValueTask<KvStatus> Rmw_InitialOrCopy()
    {
        var key = _keys[_cursor++ % PrefillCount];
        return await _kv!.RmwAsync(key, 1L, _counterFunctions, 0L);
    }

    // ═══════════ 规模 scaling（mem，点查随机热）═══════════

    [Params(1_000, 100_000)]
    public int ScalePrefill { get; set; }

    [GlobalSetup(Target = nameof(Get_Scale))]
    public void Setup_Scale() => SetupSync(prefill: ScalePrefill);

    [Benchmark(Description = "点查(规模 scaling)")]
    public async ValueTask<long> Get_Scale()
    {
        var key = _keys[_cursor++ % ScalePrefill];
        var (found, value) = await _kv!.TryGetFormattedAsync(key);
        return found ? value : -1;
    }

    // ═══════════ 扫描深度（mem，范围索引 BTree）═══════════

    [Params(10, 100, 1_000)]
    public int ScanDepth { get; set; }

    [GlobalSetup(Target = nameof(Scan_Depth))]
    public void Setup_Scan() => SetupSync(prefill: 100_000, rangeIndex: true);

    [Benchmark(Description = "范围扫描(深度 scaling)")]
    public async ValueTask<int> Scan_Depth()
    {
        var count = 0;
        await foreach (var _ in _kv!.ScanByRangeAsync(1, 1 + ScanDepth))
            count++;
        return count;
    }

    // ═══════════ 公共装配 ═══════════

    private void SetupSync(int prefill, bool rangeIndex = false)
    {
        _keys = Enumerable.Range(0, prefill).Select(i => (long)i)
            .OrderBy(_ => Random.Shared.Next()).ToArray();
        _fs = TierFs.New("memory:");
        var options = TierKvOptions.Default.WithKvName("bench-tierkv-scenario");
        if (rangeIndex) options = options.WithRangeIndex(true);
        _kv = TierKvOfLongLong.CreateAsync(_fs, options).GetAwaiter().GetResult();
        using var s = _kv.CreateSession(KvSessionConditions.None);
        for (long k = 0; k < prefill; k++)
            s.PutFormattedAsync(k, k, KvCommitPolicy.FireAndForget).AsTask().GetAwaiter().GetResult();
        s.CompletePendingAsync().AsTask().GetAwaiter().GetResult();
        _session = _kv.CreateSession(KvSessionConditions.None);
        _sessions = Enumerable.Range(0, 8).Select(_ => _kv.CreateSession(KvSessionConditions.None)).ToArray();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_kv is not null) await _kv.DisposeAsync();
        _fs?.Dispose();
    }
}

/// <summary>RMW 折叠函数（value += input——Counter 语义）。</summary>
internal sealed class CounterFunctions : KvFunctionsBase<long, long, long, long, long>
{
    public override bool InitialUpdater(ref long key, ref long input, ref long value, ref long output, ref long context)
    {
        value = input;
        output = value;
        return true;
    }

    public override bool CopyUpdater(ref long key, ref long input, ref long oldValue, ref long newValue, ref long output, ref long context)
    {
        newValue = oldValue + input;
        output = newValue;
        return true;
    }

    public override bool InPlaceUpdater(ref long key, ref long input, ref long value, ref long output, ref long context)
    {
        value += input;
        output = value;
        return true;
    }
}
