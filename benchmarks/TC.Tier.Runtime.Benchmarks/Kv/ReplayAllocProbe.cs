using System.Diagnostics;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ 临时取证探针：恢复重放与写路径的托管分配分解（GC.GetAllocatedBytesForCurrentThread 段差）。
/// 悬案：①Hash 恢复 334MB（6.7KB/条）而 Hash 插入纯内存——大头疑在共用 Ring 重开/扫描流；
/// ②BTree 节点缓存生长模式下写基准 60KB/op（定容时 623B）——定位爆点。
/// 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --replay-alloc-probe
/// </summary>
public static class ReplayAllocProbe
{
    private const int RecordCount = 50_000;
    private const int WriteBurst = 256;
    private const int ValueSize = 64;

    public static async Task Run()
    {
        // ── 悬案①：恢复三段分解（ring 重开 / 只扫不插 / Hash 全重放）──
        using (var fs = TierFs.New("memory:"))
        {
            using (var ring1 = RingOfLong.Create(RingSettings(), fs))
            {
                var value = new byte[ValueSize];
                new Random(42).NextBytes(value);
                for (long k = 0; k < RecordCount; k++)
                    ring1.Write(k, value);
                ring1.Prepare(seq: 1);
            }

            long a0 = GC.GetAllocatedBytesForCurrentThread();
            using var ring = RingOfLong.Create(RingSettings(), fs);
            long a1 = GC.GetAllocatedBytesForCurrentThread();

            int scanned = 0;
            long s0 = GC.GetAllocatedBytesForCurrentThread();
            var w = ring.BeginAddress;
            await foreach (var _ in ring.ScanAsync(w, ring.TailAddress))
                scanned++;
            long s1 = GC.GetAllocatedBytesForCurrentThread();

            long h0 = GC.GetAllocatedBytesForCurrentThread();
            var hash = new HashIndex<long>(fs,
                new HashIndexSettings(new StorageEngineOptions("kv-hash", 1L << 24, true, true, true)),
                null, ring);
            hash.Initialize(new ProbingIndexRecoveryHints(w, ring.TailAddress));
            hash.WaitForReady();
            long h1 = GC.GetAllocatedBytesForCurrentThread();

            // 对照：同数据 mock resolver（判等零 Ring 读、ScanAsync 吐预扫数组）——切开 Ring 回读侧 vs Hash 机械侧
            var keys = new long[RecordCount];
            var addrs = new LogicalAddress[RecordCount];
            int n = 0;
            await foreach (var (k, a) in ring.ScanAsync(w, ring.TailAddress))
            {
                keys[n] = k; addrs[n] = a; n++;
            }
            var mock = new PrebuiltResolver(keys, addrs, n);
            GC.Collect();
            long m0 = GC.GetAllocatedBytesForCurrentThread();
            var hash2 = new HashIndex<long>(fs,
                new HashIndexSettings(new StorageEngineOptions("kv-hash2", 1L << 24, true, true, true)),
                null, mock);
            hash2.Initialize(new ProbingIndexRecoveryHints(w, ring.TailAddress));
            hash2.WaitForReady();
            long m1 = GC.GetAllocatedBytesForCurrentThread();

            Console.WriteLine($"[recovery-decompose] reopen={a1 - a0,12:N0} B | scan-only({scanned})={s1 - s0,12:N0} B | hash-replay={h1 - h0,12:N0} B (per-record={(h1 - h0) / RecordCount,5:N0} B, count={hash.EntryCount}) | mock-replay={m1 - m0,12:N0} B (per-record={(m1 - m0) / RecordCount,5:N0} B, count={hash2.EntryCount})");
            ((IDisposable)hash).Dispose();
            ((IDisposable)hash2).Dispose();
        }

        // ── 悬案②：BTree 写爆发分解（全新组合 256 插，两轮对照）──
        for (int round = 0; round < 2; round++)
        {
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            using var fs = TierFs.New("memory:");
            using var ring = RingOfLong.Create(RingSettings(), fs);
            using var index = new BTreeIndex<long>(fs,
                new BTreeIndexSettings(new StorageEngineOptions("kv-bt", 1L << 24, true, true, true)));
            index.Initialize();
            index.WaitForReady();
            long b1 = GC.GetAllocatedBytesForCurrentThread();

            var value = new byte[ValueSize];
            new Random(42).NextBytes(value);
            long last = 0;
            for (int i = 0; i < WriteBurst; i++)
            {
                var addr = ring.Write(last, value);
                index.Insert(last, addr, LogicalAddress.Empty);
                last++;
            }
            long b2 = GC.GetAllocatedBytesForCurrentThread();
            Console.WriteLine($"[btree-write r{round}] compose={b1 - b0,10:N0} B | 256-inserts={b2 - b1,12:N0} B | per-insert={(b2 - b1) / WriteBurst,7:N0} B | entries={index.EntryCount}");
        }
    }

    private static BlittableRingSettings RingSettings()
        => new(new StorageEngineOptions("kv-ring", 64L << 20, enableSegmentation: true,
            preallocateFile: true, deleteOnClose: false))
        {
            PageSize = 8192,
            MemorySize = 32L << 20,
        };

    /// <summary>预扫数组 resolver——判等从数组直取（零 Ring 读），ScanAsync 吐同一条流。</summary>
    private sealed class PrebuiltResolver(long[] keys, LogicalAddress[] addrs, int count)
        : IKeyResolver<long>
    {
        public bool TryGetKey(LogicalAddress address, out long key)
        {
            for (int i = 0; i < count; i++)
                if (addrs[i] == address) { key = keys[i]; return true; }
            key = default;
            return false;
        }

        public LogicalAddress GetFlushedWatermark() => LogicalAddress.Empty;

        public async IAsyncEnumerable<(long Key, LogicalAddress Address)> ScanAsync(
            LogicalAddress begin, LogicalAddress end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < count; i++)
            {
                if (i % 512 == 0) await Task.Yield();
                yield return (keys[i], addrs[i]);
            }
        }

        public IAsyncEnumerable<(long Key, LogicalAddress Address)> ScanAsync(CancellationToken ct = default)
            => ScanAsync(LogicalAddress.Empty, LogicalAddress.Invalid, ct);
    }
}
