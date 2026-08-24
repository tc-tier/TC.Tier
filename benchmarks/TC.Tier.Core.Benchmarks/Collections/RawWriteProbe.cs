using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// Raw 写路径并发化验收探针（CORE-02 写计划协议）——对照 io-performance.md V1 基线：
///   V1：原地覆写缓冲档 391 MB/s；多文件并发写 19-24 MB/s；并发写反扩展 1250→300 MB/s；
///   读写混合（饱和写者）读者 0.01M ops/s。
/// 模式：--raw-write-probe single|multi|mixed|overwrite [writers] [seconds]
///   single/multi/mixed = 顺序追加（直达档——磁盘地板，验证不回退）；
///   overwrite = 随机覆写固定工作集（页驻留 + 内存吞吐——CPU/锁瓶颈，测并发扩展率）。
/// </summary>
internal static class RawWriteProbe
{
    public static int Run(string[] args)
    {
        var mode = args.Length > 1 ? args[1] : "single";
        var writers = args.Length > 2 && int.TryParse(args[2], out var w) ? w : 2;
        var seconds = args.Length > 3 && int.TryParse(args[3], out var s) ? s : 8;
        var overwrite = mode is "overwrite" or "mixed";   // mixed = overwrite 写形态 + 读者（内存态测读写并发）

        var dir = Path.Combine(Path.GetTempPath(), "tier-raw-write-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fs = RawFileSystem.New(RawCarrier.File(Path.Combine(dir, "volume.raw")),
                new RawFormatOptions { BlockSize = 4096, QuotaBytes = 16L << 30 });   // 默认 PageCacheBytes=64MB——overwrite 工作集 32MB 被预算覆盖（页驻留）
            var handles = Enumerable.Range(0, writers)
                .Select(i => fs.Open($"f{i}", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.CreateNew }))
                .ToArray();

            if (overwrite)
            {
                // 预写每文件 8MB 工作集（N×8MB ≤ 64MB 预算——页驻留，覆写走 StorePage 内存路径）
                var seed = new byte[1 << 20];
                for (var i = 0; i < writers; i++)
                    for (long off = 0; off < (1L << 23); off += seed.Length)
                        handles[i].Write(off, seed);
            }

            var sw = Stopwatch.StartNew();
            var perWriterBytes = new long[writers];
            var readOps = 0L;
            var tasks = new List<Task>();
            for (var i = 0; i < writers; i++)
            {
                var fi = i;
                var h = handles[fi];
                tasks.Add(Task.Run(() =>
                {
                    var buf = new byte[overwrite ? 4096 : 64 * 1024];
                    var rnd = new Random(fi * 7919);
                    rnd.NextBytes(buf);
                    long off = 0, local = 0;
                    var readBuf = new byte[4096];
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        if (overwrite)
                            off = (long)(rnd.Next() & 0x7FF) * 4096;   // 随机 4KB 块（8MB 工作集内——预算覆盖，页驻留）
                        h.Write(off, buf);
                        local += buf.Length;
                        if (mode == "mixed" && (fi & 1) == 0)
                        {
                            // 混合档：一半写者顺带持续读（overwrite = 同文件已写区——页缓存命中/未命中交替；
                            // 顺序档 = 另一文件已写区——读者快照捕获不应被数据段挡住）
                            var rh = overwrite ? h : handles[(fi + 1) % writers];
                            var roff = overwrite ? (long)(rnd.Next() & 0x7FF) * 4096 : (off / 4096 % 1024) * 4096;
                            rh.Read(roff, readBuf);
                            Interlocked.Increment(ref readOps);
                        }
                        off += buf.Length;
                    }
                    perWriterBytes[fi] = local;
                }));
            }
            Task.WaitAll(tasks.ToArray());
            var el = sw.Elapsed;
            var totalBytes = perWriterBytes.Sum();
            var wGBps = totalBytes / el.TotalSeconds / (1 << 30);
            Console.WriteLine($"模式 {mode} × {writers} 写者：{el.TotalSeconds:F1}s 写 {totalBytes / (1 << 20)}MB = {wGBps:F2} GB/s"
                + (mode == "mixed" ? $"；读者 {readOps / el.TotalSeconds / 1e6:F2}M ops/s" : ""));

            // 持久化档（排干 + fsync）——真实落盘吞吐
            var fsw = Stopwatch.StartNew();
            handles[0].Flush();   // JournalCommit（记录屏障——数据先于屏障，fsync 落盘）
            fsw.Stop();
            Console.WriteLine($"  Flush 落盘：{totalBytes / (1 << 20)}MB 排干 + fsync 耗时 {fsw.ElapsedMilliseconds}ms"
                + $"（有效持久化吞吐 {totalBytes / fsw.Elapsed.TotalSeconds / (1 << 20):F0} MB/s）");

            for (var i = 0; i < writers; i++) handles[i].Dispose();
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
