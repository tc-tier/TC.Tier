using TC.Tier.Runtime.Benchmarks.Storage;

namespace TC.Tier.Products.Benchmarks.Wal;

/// <summary>
/// TierVolume flush 成本直连探针（virtual 根因分析——TierWAL meta 提交 0.05ms vs
/// 单条提交 33ms 的不对称 + 11.5s 组提交巨刺的共同嫌疑：载体 flush/页缓存/journal 链）。
/// <para>mode=overwrite：固定 offset 覆写 16KB + Flush 循环（纯数据脏页路径 = meta 提交形态）。
/// mode=append：递增 offset 追加写 + Flush（grew 路径——元数据变更 + journal 记录）。
/// mode=growth：单次大块写观察载体增长成本（11.5s 刺嫌疑）。</para>
/// </summary>
public static class WalTierVolumeFlushProbe
{
    public static void Run(string spec, string mode = "overwrite", int iterations = 2000)
    {
        WalProbeCommon.PrintHeader($"TierVolume flush 直连（{spec}，mode={mode}，{iterations:N0} 次）");
        using var vol = new BenchVolume(WalProbeCommon.SpecOf(spec));
        var fs = vol.Fs;
        fs.EnsureRoot();
        fs.CreateFile("probe.dat");
        var handle = fs.Open("probe.dat", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite,
        });

        var blob = new byte[16 * 1024];
        var lat = new double[iterations];
        long offset = 0;
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            handle.Write(offset, blob);
            handle.Flush();
            lat[i] = sw.Elapsed.TotalMilliseconds;
            if (mode == "append") offset += blob.Length;
        }
        Array.Sort(lat);
        Console.WriteLine($"mode={mode}：{WalProbeCommon.Format(WalProbeCommon.Percentiles(lat))}");

        if (mode == "growth")
        {
            var big = new byte[64 * 1024 * 1024];
            var sw = Stopwatch.StartNew();
            handle.Write(offset, big);
            handle.Flush();
            sw.Stop();
            Console.WriteLine($"growth：64MB 写+Flush = {sw.Elapsed.TotalSeconds:F2}s");
        }

        if (mode is "smallfile" or "bigfile")
        {
            // 裸 RandomAccess 对照（Windows 层分层——排除 TierVolume 页缓存/写绕后 FlushFileBuffers 本身成本）
            var path = Path.Combine(Path.GetTempPath(), $"flush-probe-{Guid.NewGuid():N}.dat");
            using var raw = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
            if (mode == "bigfile") RandomAccess.SetLength(raw, 256L * 1024 * 1024);
            var blob2 = new byte[16 * 1024];
            var lat2 = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                RandomAccess.Write(raw, blob2, 0);
                RandomAccess.FlushToDisk(raw);
                lat2[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(lat2);
            Console.WriteLine($"裸 RandomAccess {mode}（FileOptions.Asynchronous）：{WalProbeCommon.Format(WalProbeCommon.Percentiles(lat2))}");
            raw.Dispose();
            File.Delete(path);
        }

        handle.Dispose();
        fs.FlushRoot();
    }
}
