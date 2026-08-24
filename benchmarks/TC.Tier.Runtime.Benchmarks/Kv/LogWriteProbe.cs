using System.Diagnostics;
using TC.Tier.Runtime.Structures.Log;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ Log（EntryLog）写/恢复吞吐探针——现行版 Log 独有压测报表（旧架构基准套件数字已随
///   40 文件清理过时）。口径：Append 写页缓冲 + 页满 flush 落盘（组提交默认策略）；mem 介质。
/// <para>变体：single（单条 Append）/ batch（BeginAppendBatch 批 512）/ recovery（跨实例重开扫盘恢复）。
/// 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks --
///   --log-write-probe [count] [entrySize]</para>
/// </summary>
public static class LogWriteProbe
{
    public static void Run(int count = 500_000, int entrySize = 64, int writers = 8)
    {
        Console.WriteLine($"[probe] Log 写/恢复吞吐 count={count} entry={entrySize}B medium=mem");
        RunWrite("single", count, entrySize, useBatch: false);
        RunWrite("batch", count, entrySize, useBatch: true);
        RunConcurrent("concurrent", count, entrySize, writers);
        RunRecovery(count, entrySize);
    }

    private static void RunConcurrent(string name, int count, int entrySize, int writers)
    {
        using var fs = TierFs.New("memory:");
        using var log = NewEntryLog(fs, $"lw-{name}");

        var payload = new byte[entrySize];
        new Random(7).NextBytes(payload);
        int perWriter = count / writers;
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int k = 0; k < perWriter; k++)
                log.Append(payload);
        })).ToArray();
        Task.WaitAll(workers);
        sw.Stop();

        long total = (long)writers * perWriter;
        double secs = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"[probe] {name,-10} {total,10:N0} appends in {secs,6:F2}s → {total / secs,12:N0} op/s  ({writers} writers)");
    }

    private static void RunWrite(string name, int count, int entrySize, bool useBatch)
    {
        using var fs = TierFs.New("memory:");
        using var log = NewEntryLog(fs, $"lw-{name}");

        var payload = new byte[entrySize];
        new Random(7).NextBytes(payload);
        var sw = Stopwatch.StartNew();

        if (useBatch)
        {
            for (int k = 0; k < count;)
            {
                using var batch = log.BeginAppendBatch();
                int budget = 512;
                while (k < count && budget-- > 0)
                {
                    batch.Append(payload);
                    k++;
                }
            }
        }
        else
        {
            for (int k = 0; k < count; k++)
                log.Append(payload);
        }
        sw.Stop();

        double secs = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"[probe] {name,-10} {count,10:N0} appends in {secs,6:F2}s → {count / secs,12:N0} op/s");
    }

    private static void RunRecovery(int count, int entrySize)
    {
        using var fs = TierFs.New("memory:");
        using (var log = NewEntryLog(fs, "lw-recover"))
        {
            var payload = new byte[entrySize];
            new Random(7).NextBytes(payload);
            for (int k = 0; k < count; k++)
                log.Append(payload);
            log.Prepare(seq: 1);
        }

        var sw = Stopwatch.StartNew();
        using var log2 = NewEntryLog(fs, "lw-recover");
        sw.Stop();
        Console.WriteLine($"[probe] recovery   {count,10:N0} entries reopen in {sw.ElapsedMilliseconds,6:F0} ms");
    }

    private static EntryLog NewEntryLog(IFileSystem fs, string name)
    {
        var settings = new EntryLogSettings(
            new StorageEngineOptions(name, 64L << 20, enableSegmentation: true,
                preallocateFile: false, deleteOnClose: false))
        {
            MetaPolicyKind = TC.Tier.Contracts.Meta.MetaPolicyKind.Managed,
        };
        var log = new EntryLog(fs, settings);
        log.Initialize();
        log.WaitForReady();
        return log;
    }
}
