using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Products.Kv;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// TierKv 配置调优矩阵（积木模式核心——配置组合性能天差地别，调到最优才验证/优化性能）：
/// <para>★ 轴：RingPageSize（写穿粒度）/ ColdReadRatio（冷读缓存占比——扫描退化主嫌疑）/
///   MutableFraction（mutable 区占比）/ 提交档。</para>
/// <para>★ ShortRun 精度；拆批顺序跑（不后台长跑）。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*TierKvTuning*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class TierKvTuningBench
{
    private const int PrefillCount = 100_000;

    private IFileSystem? _fs;
    private TierKvOfLongLong? _kv;
    private long[] _keys = null!;
    private int _cursor;

    // ═══════════ 轴 1：Ring 页大小（写穿粒度）——覆写 Committed ═══════════

    [Params(1 << 18, 1 << 20)]
    public int PageSize { get; set; }

    [GlobalSetup(Target = nameof(Put_Committed))]
    public async Task Setup_PutCommitted()
    {
        await SetupCoreAsync(pageSize: PageSize, coldReadRatio: 0.25);
    }

    [Benchmark(Description = "覆写Committed(页大小轴)")]
    public async ValueTask Put_Committed()
    {
        var key = _keys[_cursor++ % PrefillCount];
        await _kv!.PutFormattedAsync(key, key, KvCommitPolicy.Committed);
    }

    // ═══════════ 轴 2：冷读缓存（扫描退化主嫌疑）═══════════

    [Params(0.25, 1.0)]
    public double ColdReadRatio { get; set; }

    [GlobalSetup(Target = nameof(Scan_100))]
    public async Task Setup_Scan()
    {
        await SetupCoreAsync(pageSize: 1 << 18, coldReadRatio: ColdReadRatio);
    }

    [Benchmark(Description = "范围扫描100条(冷读缓存轴)")]
    public async ValueTask<int> Scan_100()
    {
        // 字节序语义：key=0x100..0x1FF（byte1=0x01 段，256 条连续字节序区间）取 100 条
        var count = 0;
        await foreach (var _ in _kv!.ScanByRangeAsync(0x100, 0x100 + 100))
            count++;
        return count;
    }

    [GlobalSetup(Target = nameof(Get_Hot))]
    public async Task Setup_GetHot()
    {
        await SetupCoreAsync(pageSize: 1 << 18, coldReadRatio: ColdReadRatio);
    }

    [Benchmark(Description = "点查(冷读缓存轴)")]
    public async ValueTask<long> Get_Hot()
    {
        var key = _keys[_cursor++ % PrefillCount];
        var (found, value) = await _kv!.TryGetFormattedAsync(key);
        return found ? value : -1;
    }

    // ═══════════ 装配 ═══════════

    private async Task SetupCoreAsync(int pageSize, double coldReadRatio)
    {
        _keys = Enumerable.Range(0, PrefillCount).Select(i => (long)i)
            .OrderBy(_ => Random.Shared.Next()).ToArray();
        _fs = TierFs.New("memory:");
        var options = TierKvOptions.Default
            .WithKvName($"tuning-{pageSize}-{coldReadRatio}")
            .WithRangeIndex(true)
            .WithRingPageSize(pageSize)
            .WithRingMemorySize(64L << 20)
            .WithRingColdReadRatio(coldReadRatio);
        _kv = await TierKvOfLongLong.CreateAsync(_fs, options);
        using var s = _kv.CreateSession(KvSessionConditions.None);
        for (long k = 0; k < PrefillCount; k++)
            await s.PutFormattedAsync(k, k, KvCommitPolicy.FireAndForget);
        await s.CompletePendingAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_kv is not null) await _kv.DisposeAsync();
        _fs?.Dispose();
    }
}
