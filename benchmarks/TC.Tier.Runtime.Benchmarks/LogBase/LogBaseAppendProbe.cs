using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Log;

namespace TC.Tier.Runtime.Benchmarks.LogBase;

/// <summary>
/// LogBase 批追加性能基线探针（--logbase-append-probe）：
/// AppendBatch 协议（Begin → 逐条 Append → Dispose）在 {mem, local 真盘(DIO/buffered)} × {页大小位宽}
/// 矩阵下的吞吐/批延迟分布/分配——LogBase 批协议让渡化改造（页满阻塞消除）的 A/B 基线仪。
/// <para>★ 盘上主测 DIO（NoBuffering——TierWal 生产默认姿态：页刷=大块顺序写 DIO 甜区，
///   storage-engine-perf-baseline §3.5 写矩阵）；buffered 行 = 裸默认配置对照（暴露默认档代价）。</para>
/// <para>★ 小页位宽强制高频滚页，把页满路径放大到可测区间。</para>
/// </summary>
public static class LogBaseAppendProbe
{
    private static readonly int[] s_s_pageBits_22__16 = { 22, 16 };
    private static readonly int[] s_s_pageBits_22__14 = { 22, 14 };
    private static readonly int[] s_s_pageBits_16 = { 16 };
    private const int PayloadBytes = 64;
    private const int BatchEntries = 400;
    private const int Batches = 500;          // 200k 条/配置
    private const int WarmupBatches = 100;

    public static int Run(string[] args)
    {
        var diskRoot = args.Length > 1 ? args[1] : "F:/tier-test-tmp/logbase-bench";
        Console.WriteLine($"=== LogBase 批追加基线（payload {PayloadBytes}B · 批 {BatchEntries} 条 · {Batches} 批/配置）===");
        Console.WriteLine($"环境：{Environment.ProcessorCount} 逻辑核 · .NET {Environment.Version} · 磁盘根 {diskRoot}");

        var payload = new byte[PayloadBytes];
        payload.AsSpan().Fill(0x5A);

        // mem 卷：页位宽 {默认22(4MB), 14(16KB)}——小页高频滚页放大页满路径（DIO 不支持自动降级，等价 buffered）
        RunMatrix("mem", () => TC.Tier.Core.IO.TierFs.New("memory:"), preallocate: false,
            hints: FileOpenHints.None, payload, pageBits: s_s_pageBits_22__14);
        // local 真盘：★ DIO（NoBuffering——TierWal 生产默认姿态，页刷=大块顺序写 DIO 甜区，
        // storage-engine-perf-baseline §3.5）+ buffered 对照行（裸默认配置——暴露默认档代价）
        RunMatrix("disk·DIO", () => TC.Tier.Core.IO.TierFs.New($"local:///{diskRoot}/{Guid.NewGuid():N}"), preallocate: true,
            hints: FileOpenHints.NoBuffering, payload, pageBits: s_s_pageBits_22__16);
        RunMatrix("disk·buffered对照", () => TC.Tier.Core.IO.TierFs.New($"local:///{diskRoot}/{Guid.NewGuid():N}"), preallocate: true,
            hints: FileOpenHints.None, payload, pageBits: s_s_pageBits_16);
        // virtual 单文件卷（TierWal 产品默认介质）——★ 载体写穿档挂载（IS-03：TierWal DurabilityValidation
        // 在 virtual 须 CarrierWriteThrough——契约① 选举窗口姿态；journal 提交免独立 fsync）
        RunMatrix("virtual·载体写穿", () => TC.Tier.Core.IO.TierFs.New($"virtual:///{diskRoot}/{Guid.NewGuid():N}.tier",
            new TC.Tier.Core.IO.TierVolume.TierVolumeFormatOptions { CarrierWriteThrough = true }), preallocate: false,
            hints: FileOpenHints.NoBuffering, payload, pageBits: s_s_pageBits_22__16);

        Console.WriteLine("=== 完成 ===");
        return 0;
    }

    private static void RunMatrix(string medium, Func<IFileSystem> fsFactory, bool preallocate,
        FileOpenHints hints, byte[] payload, int[] pageBits)
    {
        foreach (var bits in pageBits)
        {
            var fs = fsFactory();
            try
            {
                var result = RunOne(fs, preallocate, bits, hints, payload);
                Console.WriteLine($"[{medium} · 页 {1 << bits >> 10}KB] 批时间 p50={result.P50:F2}ms p99={result.P99:F2}ms max={result.Max:F2}ms · " +
                    $"{result.EntriesPerSec:F0} entry/s · {result.MBytesPerSec:F1} MB/s · 分配 {result.AllocPerEntry:F0} B/条 · GC2 +{result.Gc2}");
            }
            finally
            {
                fs.Dispose();
            }
        }
    }

    private static (double P50, double P99, double Max, double EntriesPerSec, double MBytesPerSec, double AllocPerEntry, int Gc2)
        RunOne(IFileSystem fs, bool preallocate, int pageBits, FileOpenHints hints, byte[] payload)
    {
        var engine = new StorageEngineOptions(
            "bench", segmentGrowthLimit: 1L << 30, enableSegmentation: true,
            preallocateFile: preallocate, deleteOnClose: false).WithHints(hints);
        var settings = new EntryLogSettings(engine) { LogPageSizeBits = pageBits };
        using var log = new EntryLog(fs, settings);
        log.Initialize();
        log.WaitForReady();

        var batchTimes = new List<double>(Batches);
        var sw = Stopwatch.StartNew();
        var alloc0 = GC.GetTotalAllocatedBytes(precise: false);
        var gc0 = GC.CollectionCount(2);

        for (var b = 0; b < WarmupBatches + Batches; b++)
        {
            var bsw = b >= WarmupBatches ? Stopwatch.StartNew() : null;
            using var batch = log.BeginAppendBatch();
            for (var i = 0; i < BatchEntries; i++)
                _ = batch.Append(payload);
            if (b >= WarmupBatches && bsw is not null) batchTimes.Add(bsw.Elapsed.TotalMilliseconds);
        }

        var elapsed = sw.Elapsed.TotalSeconds;
        var alloc = GC.GetTotalAllocatedBytes(precise: false) - alloc0;
        var totalEntries = (double)Batches * BatchEntries;
        batchTimes.Sort();
        var n = batchTimes.Count;
        var p50 = batchTimes[n / 2];
        var p99 = batchTimes[(int)(n * 0.99)];
        var max = batchTimes[^1];
        var mbps = alloc / 1048576.0 / elapsed;
        return (p50, p99, max, Batches * BatchEntries / elapsed, totalEntries * PayloadBytes / 1048576.0 / elapsed, alloc / totalEntries, (int)(GC.CollectionCount(2) - gc0));
    }
}
