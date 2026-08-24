using System.Diagnostics;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// 对照组：裸 RandomAccess.Write 顺序写单个稀疏文件，绕开所有 Device 逻辑。
/// <para>★ 目的：判断 D盘HDD 卡死是 OS/盘的物理问题，还是我们 Device 代码的问题。</para>
/// <para>用法：RawWriteProbe [totalMB] [payloadKB] [disk] [fileOptions]</para>
/// <para>  fileOptions: none (默认) | writethrough (WriteThrough)</para>
/// <para>示例：RawWriteProbe 5120 64 D none        # 5GB / 64KB payload / D盘 / 普通写</para>
/// </summary>
public static class RawWriteProbe
{
    public static int Run(string[] args)
    {
        long totalMB = args.Length > 0 && long.TryParse(args[0], out var t) ? t : 5120;
        int payloadKB = args.Length > 1 && int.TryParse(args[1], out var pk) ? pk : 64;
        string disk = args.Length > 2 ? args[2].ToUpperInvariant() : "D";
        string optStr = args.Length > 3 ? args[3].ToLowerInvariant() : "none";
        bool writeThrough = optStr == "writethrough";

        long totalBytes = totalMB * 1024L * 1024L;
        int payload = payloadKB * 1024;

        string dir = $"{disk}:\\tc-raw-probe";
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"raw-{Guid.NewGuid():N}.dat");
        var opts = FileOptions.SequentialScan | (writeThrough ? FileOptions.WriteThrough : FileOptions.None);

        Console.WriteLine("=== 裸 RandomAccess.Write 对照组 ===");
        Console.WriteLine($"盘: {disk}: | 总量: {totalMB} MB | payload: {payloadKB} KB | WriteThrough: {writeThrough}");
        Console.WriteLine($"路径: {path}");
        Console.WriteLine();

        // 用与 Device 相同的方式打开（不预分配，稀疏）
        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, opts, preallocationSize: 0);

        var buf = new byte[payload];
        for (int i = 0; i < payload; i++) buf[i] = (byte)(i & 0xFF);

        long written = 0;
        long offset = 0;
        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();

        var monitor = new Thread(() =>
        {
            long lastWritten = 0;
            double lastTime = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(500);
                double now = sw.Elapsed.TotalSeconds;
                double dt = now - lastTime;
                if (dt <= 0) continue;
                long cur = Interlocked.Read(ref written);
                double mbps = ((cur - lastWritten) / (1024.0 * 1024.0)) / dt;
                Console.WriteLine($"t={now,6:0.0}s | {mbps,7:0.0} MB/s | written={cur / (1024 * 1024.0),6:0.0}MB");
                lastWritten = cur;
                lastTime = now;
            }
        }) { IsBackground = true };
        monitor.Start();

        try
        {
            while (written < totalBytes)
            {
                RandomAccess.Write(handle, buf, offset);
                offset += payload;
                written += payload;
            }
        }
        finally
        {
            sw.Stop();
            cts.Cancel();
            monitor.Join(500);
        }

        double sec = sw.Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"完成: {totalBytes / (1024 * 1024.0):0.0} MB in {sec:0.00}s = {(totalBytes / (1024 * 1024.0)) / sec:0.0} MB/s avg");
        try { File.Delete(path); } catch { }
        return 0;
    }
}
