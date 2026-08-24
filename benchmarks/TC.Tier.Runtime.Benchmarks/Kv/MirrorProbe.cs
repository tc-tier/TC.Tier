using System.Diagnostics;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ 临时取证探针：主存储恢复阶段分解——帧是否被应用 / ring 重开 / 索引恢复各占多少毫秒。
/// 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --mirror-probe
/// </summary>
public static class MirrorProbe
{
    private const int N = 50_000;
    private const int ValueSize = 64;

    public static void Run()
    {
        var fs = TierFs.New("memory:");
        var value = new byte[ValueSize];
        new Random(42).NextBytes(value);

        // 源：写 N + 建索引 + 主存储 dump
        var addrs = new LogicalAddress[N];
        var swAll = Stopwatch.StartNew();
        using (var ring1 = RingOfLong.Create(RingSettings(), fs))
        {
            for (long k = 0; k < N; k++)
                addrs[k] = ring1.Write(k, value);
            ring1.Prepare(seq: 1);

            using var idx = new HashIndex<long>(fs,
                new HashIndexSettings(new StorageEngineOptions("mp-hash", 1L << 24, true, true, false)), null, ring1);
            var swBuild = Stopwatch.StartNew();
            idx.Initialize();
            idx.WaitForReady();
            for (long k = 0; k < N; k++)
                idx.Insert(k, addrs[k], LogicalAddress.Empty);
            Console.WriteLine($"[build] index={swBuild.ElapsedMilliseconds} ms  count={idx.EntryCount}");
            var swDump = Stopwatch.StartNew();
            idx.TryDump();
            Console.WriteLine($"[build] dump={swDump.ElapsedMilliseconds} ms");
        }
        Console.WriteLine($"[build] total={swAll.ElapsedMilliseconds} ms");

        // 重开：阶段分解
        var sw = Stopwatch.StartNew();
        var ring2 = RingOfLong.Create(RingSettings(), fs);
        Console.WriteLine($"[reopen] ring={sw.ElapsedMilliseconds} ms  tail={ring2.TailAddress}");

        var swIdx = Stopwatch.StartNew();
        using var idx2 = new HashIndex<long>(fs,
            new HashIndexSettings(new StorageEngineOptions("mp-hash", 1L << 24, true, true, false)), null, ring2);
        idx2.Initialize(new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));
        idx2.WaitForReady();
        Console.WriteLine($"[reopen] index+storage={swIdx.ElapsedMilliseconds} ms  applied={idx2.MainStorageAppliedLastRecovery}  count={idx2.EntryCount}");

        var spot = idx2.Find(12345);
        Console.WriteLine($"[verify] find(12345)={(spot != LogicalAddress.Empty ? "hit" : "MISS")}");
    }

    private static BlittableRingSettings RingSettings()
        => new(new StorageEngineOptions("mp-ring", 64L << 20, enableSegmentation: true,
            preallocateFile: true, deleteOnClose: false))
        {
            PageSize = 8192,
            MemorySize = 32L << 20,
        };
}
