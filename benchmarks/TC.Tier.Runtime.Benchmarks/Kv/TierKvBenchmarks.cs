using System.Text;
using BenchmarkDotNet.Attributes;
using TC.Tier.CodeGen;
using TC.Tier.Products.Kv;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// TierKv 性能基线（W6 收尾——BDN 载体）：点查命中/未命中、覆写（F&amp;F 口径）、范围扫描。
/// <para>★ mem 介质（TierFs 内存卷）口径；盘上介质与 FasterKV 同轮对标另立回合（同机同轮协议——
///   跨机/跨轮数字不可比，见记忆「性能论断须实测」）。</para>
/// <para>★ 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter *TierKv*</para>
/// </summary>
[MemoryDiagnoser]
public class TierKvBenchmarks
{
    private IFileSystem? _fs;
    private TierKvOfLongLong? _kv;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fs = TierFs.New("memory:");
        _kv = await TierKvOfLongLong.CreateAsync(_fs,
            TierKvOptions.Default.WithKvName("bench-tierkv").WithRangeIndex(true));
        using var s = _kv.CreateSession(KvSessionConditions.None);
        for (long k = 1; k <= 1_000; k++)
            await s.PutFormattedAsync(k, k, policy: KvCommitPolicy.FireAndForget);
        await s.CompletePendingAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_kv is not null) await _kv.DisposeAsync();
        _fs?.Dispose();
    }

    /// <summary>点查命中（索引 O(1) → Ring 取值解帧）。</summary>
    [Benchmark(Baseline = true)]
    public async ValueTask<long> Get_Hit()
    {
        var (found, value) = await _kv!.TryGetFormattedAsync(500);
        return found ? value : -1;
    }

    /// <summary>点查未命中（索引 miss 快路径）。</summary>
    [Benchmark]
    public async ValueTask<bool> Get_Miss()
    {
        var (found, _) = await _kv!.TryGetFormattedAsync(-1);
        return found;
    }

    /// <summary>覆写（append-only 追加 + 主索引 CAS 换绑 + 范围索引双写，不刷盘——F&amp;F 口径）。</summary>
    [Benchmark]
    public ValueTask<LogicalAddress> Put_FireAndForget()
    {
        Counter++;
        return _kv!.PutFormattedAsync(500_000 + Counter % 1_000,
            500_000 + Counter % 1_000, policy: KvCommitPolicy.FireAndForget);
    }

    /// <summary>范围扫描（字节序 BTree 游标 + 逐条 Ring 取值解帧）——Range [1, 11) 恰 10 条。</summary>
    [Benchmark]
    public async ValueTask<int> ScanRange_10()
    {
        var count = 0;
        await foreach (var e in _kv!.ScanByRangeAsync(1, 11))
            count++;
        return count;
    }

    private long Counter;
}
