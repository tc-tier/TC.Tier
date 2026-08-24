using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// KV 组合性能基准（ring-generic-key 设计稿 §5——改版的直接目的）：
/// RingOfLong（真相源）× 两族索引（派生）的两段合口径。
/// <para>★ PointRead = index.Find + Ring.GetValue（点查最后一跳）；
///   Write = Ring.Write + index.Insert（写编排两步正序）。</para>
/// <para>★ mem 介质（组合层开销口径——引擎 IO 噪声为零，磁盘介质另行对照）。</para>
/// <para>★ 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*KvComposition*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 5, iterationCount: 10)]
public class KvCompositionBench
{
    public enum IndexKind { Hash, BTree, SkipList }

    [Params(IndexKind.Hash, IndexKind.BTree, IndexKind.SkipList)]
    public IndexKind Kind { get; set; }

    private const int PrefillCount = 100_000;
    private const int ValueSize = 64;

    private IFileSystem _fs = null!;
    private RingOfLong _ring = null!;
    private IIndex<long> _index = null!;
    private long[] _readKeys = null!;
    private long _writeKey;
    private byte[] _value = null!;
    private byte[] _readBuf = null!;

    // ═══ 点查（预填 100k 后热读）═══

    [GlobalSetup(Target = nameof(PointRead))]
    public void SetupRead()
    {
        CreateComposition(prefill: PrefillCount);
        _readKeys = Enumerable.Range(0, PrefillCount).Select(i => (long)i)
            .OrderBy(_ => Random.Shared.Next()).ToArray();
        _readBuf = new byte[ValueSize];
    }

    [GlobalCleanup(Target = nameof(PointRead))]
    public void CleanupRead() => DisposeComposition();

    private long _readCursor;

    [Benchmark(Description = "KV.PointRead(find+getvalue)")]
    public long PointRead()
    {
        var key = _readKeys[_readCursor++ % PrefillCount];
        var addr = _index.Find(key);
        return addr == LogicalAddress.Empty ? -1 : _ring.GetValue(addr, _readBuf);
    }

    // ═══ 写吞吐（每迭代全新组合；每 invocation 批量 256 条摊平计时粒度——
    //     IterationSetup 场景 BDN 强制 InvocationCount=1，单条/invocation 会把计时粒度虚增成几十 µs 假象）═══

    private const int WriteBurst = 256;

    [IterationSetup(Targets = new[] { nameof(Write), nameof(WriteBatch) })]
    public void SetupWrite() => CreateComposition(prefill: 0);

    [IterationCleanup(Targets = new[] { nameof(Write), nameof(WriteBatch) })]
    public void CleanupWrite() => DisposeComposition();

    [Benchmark(Description = "KV.Write(ring.write+index.insert)", OperationsPerInvoke = WriteBurst)]
    public long Write()
    {
        long last = 0;
        for (int i = 0; i < WriteBurst; i++)
        {
            var addr = _ring.Write(_writeKey, _value);
            _index.Insert(_writeKey, addr, LogicalAddress.Empty);
            last = _writeKey++;
        }
        return last;
    }

    [Benchmark(Description = "KV.WriteBatch(begin+batch.append+insert)", OperationsPerInvoke = WriteBurst)]
    public long WriteBatch()
    {
        long last = 0;
        using (var batch = _ring.BeginWriteBatch())
        {
            for (int i = 0; i < WriteBurst; i++)
            {
                var addr = batch.Append(_writeKey, _value);
                _index.Insert(_writeKey, addr, LogicalAddress.Empty);
                last = _writeKey++;
            }
        }
        return last;
    }

    // ═══ 组合装配 ═══

    private static BlittableRingSettings RingSettings()
        => new(new StorageEngineOptions("kv-ring", 64L << 20, enableSegmentation: true,
            preallocateFile: true, deleteOnClose: false))
        {
            PageSize = 8192,
            MemorySize = 32L << 20,   // 4096 页；100k×~96B ≈ 10MB——全热区，驱逐零干扰
        };

    private void CreateComposition(int prefill)
    {
        _fs = TierFs.New("memory:");
        _ring = RingOfLong.Create(RingSettings(), _fs);
        _index = CreateIndex();

        _value = new byte[ValueSize];
        new Random(42).NextBytes(_value);
        for (long k = 0; k < prefill; k++)
        {
            var addr = _ring.Write(k, _value);
            _index.Insert(k, addr, LogicalAddress.Empty);
        }
        _writeKey = prefill;
    }

    /// <summary>装配+启动索引（Initialize/WaitForReady 在具体类型上调——IIndex 最小协议不含生命周期）。</summary>
    private IIndex<long> CreateIndex() => Kind switch
    {
        IndexKind.Hash => Start(new HashIndex<long>(_fs,
            new HashIndexSettings(new StorageEngineOptions("kv-hash", 1L << 24, true, true, true)), null, _ring)),
        IndexKind.BTree => Start(new BTreeIndex<long>(_fs,
            new BTreeIndexSettings(new StorageEngineOptions("kv-bt", 1L << 24, true, true, true)))),
        IndexKind.SkipList => Start(new SkipListIndex<long>(_fs,
            new SkipListIndexSettings(new StorageEngineOptions("kv-sl", 1L << 24, true, true, true)))),
        _ => throw new InvalidOperationException(),
    };

    private static HashIndex<long> Start(HashIndex<long> index)
    { index.Initialize(); index.WaitForReady(); return index; }

    private static BTreeIndex<long> Start(BTreeIndex<long> index)
    { index.Initialize(); index.WaitForReady(); return index; }

    private static SkipListIndex<long> Start(SkipListIndex<long> index)
    { index.Initialize(); index.WaitForReady(); return index; }

    private void DisposeComposition()
    {
        ((IDisposable)_index).Dispose();
        _ring.Dispose();
        _fs.Dispose();
    }
}

/// <summary>
/// KV 恢复速度基准（§5）——跨实例全量重建（W=Begin 拉流重放）。
/// <para>★ 镜像+增量重放对比项待镜像加速落地（自管镜像真形态）后加入。</para>
/// <para>★ 每迭代：源 Ring 写 N 落盘（IterationSetup）→ 基准体 = 重开 Ring + index 全量重放。
///   测的是整段恢复墙钟（BDN 每次调用=一次完整恢复，返回 EntryCount 验证完整性）。</para>
/// <para>★ 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*KvRecovery*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 8)]
public class KvRecoveryBench
{
    public enum IndexKind { Hash, BTree, SkipList }

    [Params(IndexKind.Hash, IndexKind.BTree, IndexKind.SkipList)]
    public IndexKind Kind { get; set; }

    private const int RecordCount = 50_000;
    private const int ValueSize = 64;

    private IFileSystem _fs = null!;
    private byte[] _value = null!;

    [IterationSetup]
    public void Setup()
    {
        // 源 Ring：写 N + Prepare（数据+水位落盘）→ Dispose——留给基准体重开恢复
        _fs = TierFs.New("memory:");
        using var ring1 = RingOfLong.Create(RingSettings(), _fs);
        _value = new byte[ValueSize];
        new Random(42).NextBytes(_value);
        for (long k = 0; k < RecordCount; k++)
            ring1.Write(k, _value);
        ring1.Prepare(seq: 1);
    }

    [IterationCleanup]
    public void Cleanup() => _fs?.Dispose();

    [Benchmark(Description = "KV.Recovery(reopen+full-replay)")]
    public long FullReplayRecovery()
    {
        using var ring = RingOfLong.Create(RingSettings(), _fs);
        var w = ring.BeginAddress;   // 无镜像 → W=Begin 全量重建
        switch (Kind)
        {
            case IndexKind.Hash:
            {
                using var index = NewHash(_fs, ring, new ProbingIndexRecoveryHints(w, ring.TailAddress));
                return index.EntryCount;
            }
            case IndexKind.BTree:
            {
                using var index = NewBTree(_fs, ring, new SortedIndexRecoveryHints(w, ring.TailAddress));
                return index.EntryCount;
            }
            default:
            {
                using var index = NewSkipList(_fs, ring, new SortedIndexRecoveryHints(w, ring.TailAddress));
                return index.EntryCount;
            }
        }
    }



    [Benchmark(Description = "KV.MirrorRecovery(load-image+delta0)")]
    public long MirrorRecovery()
    {
        using var ring = RingOfLong.Create(RingSettings(), _fs);
        var w = ring.BeginAddress;
        switch (Kind)
        {
            case IndexKind.Hash:
            {
                var index = new HashIndex<long>(_fs,
                    new HashIndexSettings(new StorageEngineOptions("m-kv-hash", 1L << 24, true, true, false)), null, ring);
                index.Initialize(new ProbingIndexRecoveryHints(w, ring.TailAddress));
                index.WaitForReady();
                return index.EntryCount;
            }
            case IndexKind.BTree:
            {
                using var index = new BTreeIndex<long>(_fs,
                    new BTreeIndexSettings(new StorageEngineOptions("m-kv-bt", 1L << 24, true, true, false)), keyResolver: ring);
                index.Initialize(new SortedIndexRecoveryHints(w, ring.TailAddress));
                index.WaitForReady();
                return index.EntryCount;
            }
            default:
            {
                using var index = new SkipListIndex<long>(_fs,
                    new SkipListIndexSettings(new StorageEngineOptions("m-kv-sl", 1L << 24, true, true, false)), keyResolver: ring);
                index.Initialize(new SortedIndexRecoveryHints(w, ring.TailAddress));
                index.WaitForReady();
                return index.EntryCount;
            }
        }
    }

    private static BlittableRingSettings RingSettings()
        => new(new StorageEngineOptions("kv-ring", 64L << 20, enableSegmentation: true,
            preallocateFile: true, deleteOnClose: false))
        {
            PageSize = 8192,
            MemorySize = 32L << 20,
        };

    private static HashIndex<long> NewHash(IFileSystem fs, RingOfLong ring, ProbingIndexRecoveryHints hints)
    {
        var idx = new HashIndex<long>(fs, new HashIndexSettings(
            new StorageEngineOptions("kv-hash", 1L << 24, true, true, true)), null, ring);
        idx.Initialize(hints);
        idx.WaitForReady();
        return idx;
    }

    private static BTreeIndex<long> NewBTree(IFileSystem fs, RingOfLong ring, SortedIndexRecoveryHints hints)
    {
        var idx = new BTreeIndex<long>(fs, new BTreeIndexSettings(
            new StorageEngineOptions("kv-bt", 1L << 24, true, true, true)), keyResolver: ring);
        idx.Initialize(hints);
        idx.WaitForReady();
        return idx;
    }

    private static SkipListIndex<long> NewSkipList(IFileSystem fs, RingOfLong ring, SortedIndexRecoveryHints hints)
    {
        var idx = new SkipListIndex<long>(fs, new SkipListIndexSettings(
            new StorageEngineOptions("kv-sl", 1L << 24, true, true, true)), keyResolver: ring);
        idx.Initialize(hints);
        idx.WaitForReady();
        return idx;
    }
}
