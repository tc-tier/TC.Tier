using System.Diagnostics;
using TC.Tier.Contracts.Structures;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// 索引多生产者写吞吐探针（集合结构家族 spec §6 / #522 波次 0c——写闸形态的数字说话）：
/// HashIndex（ProbingIndex 探测族——槽 CAS + 溢出链条带锁）/ BTreeIndex（EnterOp 读写全互斥）/
/// SkipListIndex（EnterOp 读写全互斥）三形态 × 多线程并发 Insert。
/// <para>★ 口径：N 线程并发唯一键 Insert 总墙钟 → 吞吐（op/s）；mem 介质（纯 CPU 写路径口径）；
/// 键全局唯一（零 dup 覆写混入）；守恒自检 = 收尾 EntryCount == 计划插入总数（不等即探针作废）。</para>
/// 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks --
///   --index-write-probe [writers] [perWriter]</para>
/// </summary>
public static class IndexWriteThroughputProbe
{
    public static void Run(int writers = 8, int perWriter = 200_000)
    {
        Console.WriteLine($"[probe] 索引并发写吞吐 writers={writers} perWriter={perWriter:N0} key=8B unique medium=mem");
        Console.WriteLine($"[probe] repro: dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --index-write-probe {writers} {perWriter}");
        RunHash(writers, perWriter);
        RunBTree(writers, perWriter);
        RunSkipList(writers, perWriter);
    }

    private static void RunHash(int writers, int perWriter)
    {
        using var fs = TierFs.New("memory:");
        using var index = NewHash(fs);
        RunConcurrent("hash(probing)", writers, perWriter, index,
            (idx, k) => idx.Insert(k, Addr(k), LogicalAddress.Empty), idx => idx.EntryCount);
        // 单写者对照（闸外基线——并发退化幅度的参照点）
        using var fs2 = TierFs.New("memory:");
        using var index2 = NewHash(fs2);
        RunSerial("hash(probing)", perWriter, index2,
            (idx, k) => idx.Insert(k, Addr(k), LogicalAddress.Empty), idx => idx.EntryCount);
    }

    private static void RunBTree(int writers, int perWriter)
    {
        using var fs = TierFs.New("memory:");
        using var index = NewBTree(fs);
        RunConcurrent("btree", writers, perWriter, index,
            (idx, k) => idx.Insert(k, Addr(k), LogicalAddress.Empty), idx => idx.EntryCount);
        using var fs2 = TierFs.New("memory:");
        using var index2 = NewBTree(fs2);
        RunSerial("btree", perWriter, index2,
            (idx, k) => idx.Insert(k, Addr(k), LogicalAddress.Empty), idx => idx.EntryCount);
    }

    private static void RunSkipList(int writers, int perWriter)
    {
        using var fs = TierFs.New("memory:");
        using var index = NewSkipList(fs);
        RunConcurrent("skiplist", writers, perWriter, index,
            (idx, k) => idx.Insert(k, Addr(k), LogicalAddress.Empty), idx => idx.EntryCount);
        using var fs2 = TierFs.New("memory:");
        using var index2 = NewSkipList(fs2);
        RunSerial("skiplist", perWriter, index2,
            (idx, k) => idx.Insert(k, Addr(k), LogicalAddress.Empty), idx => idx.EntryCount);
    }

    private static void RunConcurrent<TIdx>(string name, int writers, int perWriter, TIdx index,
        Action<TIdx, long> insert, Func<TIdx, long> count)
    {
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            long keyBase = (long)w * perWriter;
            for (long k = 0; k < perWriter; k++)
                insert(index, keyBase + k);
        })).ToArray();
        Task.WaitAll(workers);
        sw.Stop();

        Report(name, sw.Elapsed, writers * (long)perWriter, writers, perWriter, count(index));
    }

    private static void RunSerial<TIdx>(string name, int perWriter, TIdx index,
        Action<TIdx, long> insert, Func<TIdx, long> count)
    {
        var sw = Stopwatch.StartNew();
        for (long k = 0; k < perWriter; k++)
            insert(index, k);
        sw.Stop();

        Report(name + " [serial]", sw.Elapsed, perWriter, 1, perWriter, count(index));
    }

    private static void Report(string name, TimeSpan elapsed, long total, int writers, int perWriter, long finalCount)
    {
        double secs = elapsed.TotalSeconds;
        string guard = finalCount == total ? "ok" : $"COUNT MISMATCH({finalCount}≠{total})——探针作废";
        Console.WriteLine($"[probe] {name,-20} {total,10:N0} ops in {secs,6:F2}s → {total / secs,12:N0} op/s  ({writers}w × {perWriter:N0})  守恒={guard}");
    }

    private static LogicalAddress Addr(long k) => new(0, k);

    private static HashIndex<long> NewHash(IFileSystem fs)
    {
        var settings = new HashIndexSettings(
            new StorageEngineOptions("iw-hash", 64L << 20, enableSegmentation: true,
                preallocateFile: true, deleteOnClose: true))
        {
            HashTableCapacity = 1 << 19,   // ≥ 计划条目数（探针稳态零 rehash 混入）
        };
        var index = new ProbeHashIndex(fs, settings);
        index.Initialize();
        index.WaitForReady();
        return index;
    }

    private static BTreeIndex<long> NewBTree(IFileSystem fs)
    {
        var settings = new BTreeIndexSettings(
            new StorageEngineOptions("iw-btree", 64L << 20, enableSegmentation: true,
                preallocateFile: true, deleteOnClose: true))
        {
            PersistencePolicy = new SortedIndexPersistencePolicy(),
        };
        var index = new ProbeBTreeIndex(fs, settings);
        index.Initialize();
        index.WaitForReady();
        return index;
    }

    private static SkipListIndex<long> NewSkipList(IFileSystem fs)
    {
        var settings = new SkipListIndexSettings(
            new StorageEngineOptions("iw-skiplist", 64L << 20, enableSegmentation: true,
                preallocateFile: true, deleteOnClose: true));
        var index = new ProbeSkipListIndex(fs, settings);
        index.Initialize();
        index.WaitForReady();
        return index;
    }

    /// <summary>判等闭环 resolver（探针键即 value 地址 Offset——零存储零竞态回读；纯写探针不触发扫描/水位消费面）。</summary>
    private sealed class ProbeResolver : IKeyResolver<long>
    {
        public bool TryGetKey(LogicalAddress addr, out long key)
        {
            key = addr.Offset;
            return true;
        }

        public LogicalAddress GetFlushedWatermark() => LogicalAddress.Empty;

        public async IAsyncEnumerable<(long Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
            LogicalAddress begin, LogicalAddress end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<(long Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ProbeHashIndex : HashIndex<long>
    {
        public ProbeHashIndex(IFileSystem fs, HashIndexSettings settings) : base(fs, settings, keyResolver: new ProbeResolver()) { }
    }

    private sealed class ProbeBTreeIndex : BTreeIndex<long>
    {
        public ProbeBTreeIndex(IFileSystem fs, BTreeIndexSettings settings) : base(fs, settings) { }
    }

    private sealed class ProbeSkipListIndex : SkipListIndex<long>
    {
        public ProbeSkipListIndex(IFileSystem fs, SkipListIndexSettings settings) : base(fs, settings) { }
    }
}
