using System.Diagnostics;
using TC.Tier.Core.IO;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ 临时取证探针：fsync 单点成本分解——裸 .NET FlushToDisk（对照组）vs 本层 IFileHandle.Flush
/// vs EntryLog.Prepare（引擎路径 fsync 数分解）。回答"27 回合/s 是盘的问题还是我们 Fs 的问题"。
/// 运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --fs-flush-probe [local|mem]
/// </summary>
public static class FsFlushProbe
{
    private const int N = 200;

    public static void Run(string medium = "local")
    {
        string dir = medium == "local"
            ? $"F:/CodeSource/mytzz/dotnet/TC.Tier/test_out/flush-probe-{Guid.NewGuid():N}"
            : "memory:";
        Directory.CreateDirectory(dir);
        string rawPath = Path.Combine(dir, "raw.bin");
        string oursPath = Path.Combine(dir, "ours.bin");

        var payload = new byte[24];

        // ① 裸 .NET：RandomAccess 写 + FlushToDisk（fsync 系统调用本体）
        long rawMean;
        {
            using var handle = File.OpenHandle(rawPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, FileOptions.None);
            RandomAccess.Write(handle, payload, 0);   // 预热建文件
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                RandomAccess.Write(handle, payload, 0);
                RandomAccess.FlushToDisk(handle);
            }
            sw.Stop();
            rawMean = sw.ElapsedMilliseconds / N;
        }

        // ② 本层：TierFs local + IFileHandle 定位写 + Flush（同形状）
        long oursMean;
        {
            using var fs = TierFs.New($"local:///{dir}/fsw");
            if (!fs.Exists("ours.bin"))
                fs.CreateFile("ours.bin", 4096);
            using var h = fs.Open("ours.bin", new FileOpenOptions
            {
                Access = AccessMode.ReadWrite,
                Mode = FileOpenMode.OpenOrCreate,
                Sharing = FileSharing.ReadWrite | FileSharing.Delete,
            });
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                h.Write(0, payload);
                h.Flush();
            }
            sw.Stop();
            oursMean = sw.ElapsedMilliseconds / N;
        }

        // ③ EntryLog.Prepare 单点（数据+meta 落盘的引擎路径）
        long prepareMean;
        {
            using var fs = TierFs.New($"local:///{dir}/entry");
            var settings = new TC.Tier.Runtime.Structures.Log.EntryLogSettings(
                new TC.Tier.Runtime.Storage.StorageEngineOptions("probe-entry", 32L << 20,
                    enableSegmentation: true, preallocateFile: false, deleteOnClose: false))
            {
                CommitInterval = TimeSpan.FromMilliseconds(-1),
                MaxUnflushedBytes = long.MaxValue,
                MaxUnflushedCount = int.MaxValue,
            };
            using var log = new TC.Tier.Runtime.Structures.Log.EntryLog(fs, settings);
            log.Initialize();
            log.WaitForReady();
            var entry = new byte[64];
            log.Append(entry);   // 预热
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                log.Append(entry);
                log.Prepare(seq: i + 1);
            }
            sw.Stop();
            prepareMean = sw.ElapsedMilliseconds / N;
        }

        Console.WriteLine($"[flush-decompose medium={medium} N={N}] " +
                          $"裸 fsync(RandomAccess.FlushToDisk)={rawMean} ms/次 | " +
                          $"本层 IFileHandle.Write+Flush={oursMean} ms/次 | " +
                          $"EntryLog.Append+Prepare={prepareMean} ms/次");
    }
}
