using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// Raw 页缓存并发正确性探针（CORE-01/RM-12 竞态封闭回归验证）——预算压力场景：
/// 预算远小于工作集（强制逐出 + 压力排干）+ 多线程写多文件（StorePage 标脏）
/// + 多线程并发读回校验（GetOrLoadPage 持拴拷贝——逐出者与读者并发的窗口在此放大）。
/// 校验：每块写模式字节，并发读者读回断言——抓"拷贝到已归还池缓冲"类竞态
/// （逐出者 TryRemove+还缓冲 与 读者锁内拷贝的窗口，v1 真实存在的数据损坏级竞态）。
///
/// 用法：dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --raw-page-cache-probe [seconds]
/// 返回码：0 = 持续推进且数据校验通过；2 = 进度停滞 15s；3 = 数据校验失败（竞态实锤）
/// </summary>
internal static class RawPageCacheProbe
{
    private const int PageBudgetBytes = 2 << 20;      // 2MB 预算——远小于工作集（强制逐出+排干）
    private const long PerWriterBytes = 4L << 20;     // 每写者 4MB（> 1MB 排干阈值，持续跨越）
    private const int FileCount = 2;

    public static int Run(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 30;
        Console.WriteLine($"===== RawPageCacheProbe（{seconds}s）——预算 {PageBudgetBytes >> 20}MB / 写者 {PerWriterBytes >> 20}MB / {FileCount} 文件 =====");

        var dir = Path.Combine(Path.GetTempPath(), "tier-raw-cache-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var doneWrites = 0;
        var totalOps = 0;
        var checksumMismatch = 0;

        try
        {
            using var fs = RawFileSystem.New(RawCarrier.File(Path.Combine(dir, "volume.raw")),
                new RawFormatOptions { BlockSize = 4096, QuotaBytes = 256L << 20 });
            var handles = new IFileHandle[FileCount];
            for (var i = 0; i < FileCount; i++)
                handles[i] = fs.Open($"f{i}", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.CreateNew });

            var sw = Stopwatch.StartNew();
            var workers = new List<Task>();
            var writtenBlocks = new long[FileCount];   // 每文件已写块数（Volatile 推进——读者只读已写区域）
            // 写者：每写者一个文件，循环逐块写 4KB（模式 = 文件序 + 块序 + 值——确定性可校验）
            for (var w = 0; w < 2; w++)
            {
                var fi = w;
                var h = handles[fi];
                workers.Add(Task.Run(() =>
                {
                    var buf = new byte[4096];
                    long off = 0;
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        if (off >= PerWriterBytes) off = 0;
                        FillPattern(buf, fi, off);
                        h.Write(off, buf);
                        Volatile.Write(ref writtenBlocks[fi], off / 4096 + 1);
                        off += 4096;
                        Interlocked.Increment(ref totalOps);
                    }
                    Interlocked.Increment(ref doneWrites);
                }));
            }
            // 校验读者：只读已写进度内的块（写读并发窗口持续——命中/逐出-重装路径全覆盖）
            for (var r = 0; r < 2; r++)
            {
                var fi = r;
                var h = handles[fi];
                workers.Add(Task.Run(() =>
                {
                    var buf = new byte[4096];
                    var expect = new byte[4096];
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        var limit = Volatile.Read(ref writtenBlocks[fi]);
                        if (limit <= 0) continue;
                        var off = ((sw.ElapsedMilliseconds / 7) % limit) * 4096;
                        h.Read(off, buf);
                        FillPattern(expect, fi, off);
                        if (!buf.AsSpan().SequenceEqual(expect))
                            Interlocked.Increment(ref checksumMismatch);
                        Interlocked.Increment(ref totalOps);
                    }
                }));
            }

            // 看门狗：总进度停滞 15s = 挂死 → 取证退出
            var lastOps = 0L;
            var stall = Stopwatch.StartNew();
            while (!Task.WaitAll(workers.ToArray(), 500))
            {
                var cur = Volatile.Read(ref totalOps);
                if (cur != lastOps) { lastOps = cur; stall.Restart(); continue; }
                if (stall.ElapsedMilliseconds > 15_000)
                {
                    Console.WriteLine($"\n★ 挂死嫌疑：进度 {cur} ops 停滞 {stall.Elapsed.TotalSeconds:F0}s（写完成 {doneWrites}/2）");
                    Console.WriteLine("  定位：dotnet-stack report -p <pid> 看各线程 Monitor.Enter 栈");
                    return 2;
                }
            }

            if (Volatile.Read(ref checksumMismatch) > 0)
            {
                Console.WriteLine($"✗ 数据校验失败 {checksumMismatch} 次——页缓存竞态（拷贝到已归还缓冲/数据错乱）实锤");
                return 3;
            }

            Console.WriteLine($"✓ {sw.Elapsed.TotalSeconds:F1}s 持续推进，{totalOps} ops（写 {doneWrites}/2 完成）——无挂死、数据校验全过");
            for (var i = 0; i < FileCount; i++) handles[i].Dispose();
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void FillPattern(byte[] buf, int file, long offset)
    {
        for (var i = 0; i < buf.Length; i++)
            buf[i] = (byte)(file * 31 + (offset / 4096) * 7 + i * 3);
    }
}
