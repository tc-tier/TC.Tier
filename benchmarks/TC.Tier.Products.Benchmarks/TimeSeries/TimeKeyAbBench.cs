using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Contracts.Storage;
using TC.Tier.Contracts.Structures;
using TC.Tier.Core.IO;
using TC.Tier.Products.TimeSeries;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Benchmarks.Storage;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Products.Benchmarks.TimeSeries;

/// <summary>
/// TimeKey（16B 单序列）vs DenseTimeKey（20B 稠密多序列）A/B 对照微基准（#443 设计稿 §3 实证承诺
/// ——组件独立微基准 A/B 律：同进程同轮背靠背）。
/// <para>★ 三口径：比较器哈希（raw hash 成本）/ BTree 点查命中（floor 语义——含 20B 键扇出 -20%
///   的树高差效应）/ BTree 插入+删除（索引维护成本，同形态 A/B）。树体均为 mem 卷真实
///   BTreeOfTimeKey / BTreeOfDenseTimeKey（NodeSize 256 缺省），5 万条等规模对齐。</para>
/// <para>运行：<c>dotnet run -c Release --project benchmarks/TC.Tier.Products.Benchmarks --
///   --filter "*TimeKeyAb*"</c>。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 10)]
public class TimeKeyAbBench : IDisposable
{
    private const int EntryCount = 50_000;

    private BenchVolume? _volume;
    private BTreeOfTimeKey? _index16;
    private BTreeOfDenseTimeKey? _index20;
    private long[] _queryStamps = null!;
    private long _sequence;
    private long _insertTie;

    [GlobalSetup]
    public void Setup()
    {
        _volume = new BenchVolume("memory:");
        var fs = _volume.Fs;
        _queryStamps = new long[64];
        long step = TimeSpan.TicksPerSecond / 100;   // 10ms 步距

        for (int i = 0; i < 64; i++)
            _queryStamps[i] = step * (i * (EntryCount / 64));   // 全域均布命中点

        var comparer16 = new TimeKeyComparer();
        var settings16 = new BTreeIndexSettings(new StorageEngineOptions("bench.tskey.index", 64L << 20,
            enableSegmentation: false, preallocateFile: false)) { NodeSize = 256 };
        _index16 = new BTreeOfTimeKey(fs, settings16, keyComparer: comparer16,
            keyResolver: new StubResolver<TimeKey>());
        InitializeAndPopulate(_index16, i => new TimeKey(step * i, i));

        var comparer20 = new DenseTimeKeyComparer();
        var settings20 = new BTreeIndexSettings(new StorageEngineOptions("bench.dtskey.index", 64L << 20,
            enableSegmentation: false, preallocateFile: false)) { NodeSize = 256 };
        _index20 = new BTreeOfDenseTimeKey(fs, settings20, keyComparer: comparer20,
            keyResolver: new StubResolver<DenseTimeKey>());
        InitializeAndPopulate(_index20, i => new DenseTimeKey(7, step * i, i));
    }

    private static void InitializeAndPopulate<TKey>(BTreeIndex<TKey> idx, Func<int, TKey> keyOf)
        where TKey : unmanaged, IEquatable<TKey>
    {
        idx.Initialize(new SortedIndexRecoveryHints(LogicalAddress.Empty, new LogicalAddress(0, 0, long.MaxValue)));
        idx.WaitForReadyAsync(default).GetAwaiter().GetResult();
        for (int i = 0; i < EntryCount; i++)
            idx.Insert(keyOf(i), new LogicalAddress(0, 0, i), idx.BeginAddress);
    }

    /// <summary>比较器哈希：TimeKey（16B 键字节面）。</summary>
    [Benchmark(Baseline = true, Description = "Hash64 TimeKey 16B")]
    public ulong Hash16()
        => new TimeKeyComparer().GetHashCode64(new TimeKey(_queryStamps[_sequence % 64], _sequence));

    /// <summary>比较器哈希：DenseTimeKey（20B 键字节面）。</summary>
    [Benchmark(Description = "Hash64 DenseTimeKey 20B")]
    public ulong Hash20()
        => new DenseTimeKeyComparer().GetHashCode64(new DenseTimeKey(7, _queryStamps[_sequence % 64], _sequence));

    /// <summary>BTree 点查（floor 语义命中）：TimeKey 16B 树。</summary>
    [Benchmark(Description = "BTree 点查 TimeKey 16B")]
    public bool PointLookup16()
    {
        var q = new TimeKey(_queryStamps[_sequence++ % 64], long.MaxValue);
        return _index16!.TryGetFloor(q, out _, out _);
    }

    /// <summary>BTree 点查（floor 语义命中）：DenseTimeKey 20B 树（同构查询形态）。</summary>
    [Benchmark(Description = "BTree 点查 DenseTimeKey 20B")]
    public bool PointLookup20()
    {
        var q = new DenseTimeKey(7, _queryStamps[_sequence++ % 64], long.MaxValue);
        return _index20!.TryGetFloor(q, out _, out _);
    }

    /// <summary>BTree 插入+删除（索引维护成本，同形态 A/B）：TimeKey 16B 树。</summary>
    [Benchmark(Description = "BTree 插入+删除 TimeKey 16B")]
    public bool InsertDelete16()
    {
        var key = new TimeKey(_queryStamps[_sequence % 64], ++_insertTie);
        _index16!.Insert(key, new LogicalAddress(0, 0, _insertTie), _index16.BeginAddress);
        return _index16.Delete(key);
    }

    /// <summary>BTree 插入+删除（索引维护成本，同形态 A/B）：DenseTimeKey 20B 树。</summary>
    [Benchmark(Description = "BTree 插入+删除 DenseTimeKey 20B")]
    public bool InsertDelete20()
    {
        var key = new DenseTimeKey(7, _queryStamps[_sequence % 64], ++_insertTie);
        _index20!.Insert(key, new LogicalAddress(0, 0, _insertTie), _index20.BeginAddress);
        return _index20.Delete(key);
    }

    public void Dispose()
    {
        _index16?.Dispose();
        _index20?.Dispose();
        _volume?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>stub 解析器（恢复重放数据面——基准树不经重放，恒空）。</summary>
    private sealed class StubResolver<TKey> : IKeyResolver<TKey> where TKey : unmanaged, IEquatable<TKey>
    {
        public bool TryGetKey(LogicalAddress addr, out TKey key) { key = default; return false; }
        public LogicalAddress GetFlushedWatermark() => LogicalAddress.Invalid;
        public async IAsyncEnumerable<(TKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
            LogicalAddress begin, LogicalAddress end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
        public IAsyncEnumerable<(TKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
            => ScanAsync(LogicalAddress.Invalid, LogicalAddress.Invalid, ct);
    }
}
