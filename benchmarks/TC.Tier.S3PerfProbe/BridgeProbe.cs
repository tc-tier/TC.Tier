using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.S3;

namespace TC.Tier.S3PerfProbe;

/// <summary>
/// 桥级真实介质压测：RemoteFileSystem(云对象存储)——Flush 全量/增量、读回校验、洞读加速。
/// 入口：dotnet run -c Release -- bridge endpoint ...（Program 分发）。
/// </summary>
internal static class BridgeProbe
{
    public static void Run(string[] args)
    {
        try
        {
            RunCore(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✗ {ex}");
            Console.Error.WriteLine(ex.StackTrace);
        }
    }

    private static void RunCore(string[] args)
    {
        var endpoint = Pick(args, "endpoint", Environment.GetEnvironmentVariable("TIER_S3_TEST_ENDPOINT") ?? "https://cos.ap-chengdu.myqcloud.com");
        var bucket = Pick(args, "bucket", "tc-1253530278");
        var access = Pick(args, "access", Environment.GetEnvironmentVariable("TIER_S3_TEST_ACCESS_KEY") ?? "");
        var secret = Pick(args, "secret", Environment.GetEnvironmentVariable("TIER_S3_TEST_SECRET_KEY") ?? "");
        var vhost = Pick(args, "vhost", "1") == "1";
        var sizeMB = PickInt(args, "sizeMB", 128);
        var appendMB = PickInt(args, "appendMB", 32);
        if (string.IsNullOrEmpty(secret))
        {
            Console.Error.WriteLine("须提供 access/secret");
            return;
        }

        using var store = S3ObjectStore.Create(new S3ClientOptions
        {
            Endpoint = endpoint,
            Bucket = bucket,
            Credentials = new StaticCredentials(access, secret),
            Timeout = TimeSpan.FromMinutes(10),
            MaxRetries = 1,
            UseVirtualHostAddressing = vhost,
        });
        using var fs = RemoteFileSystem.OpenOrCreate(store, new RemoteFileSystemOptions
        {
            KeyPrefix = "perf-bridge/",
            Spill = RemoteSpill.ToDisk(System.IO.Path.GetTempPath()),   // staging 超内存预算的落盘根（128MB > 默认 64MB 预算）
        });

        Console.WriteLine($"── 桥级压测（RemoteFileSystem × {endpoint} vhost={vhost}）：初始 {sizeMB}MB + 追加 {appendMB}MB");
        var data = new byte[sizeMB * 1024L * 1024];
        new Random(7).NextBytes(data);
        var name = $"bench-{Guid.NewGuid():N}";

        // ① 全量 Flush（multipart 上传 sizeMB）
        var t1 = Stopwatch.StartNew();
        using (var h = fs.Open(name, new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }))
        {
            var pos = 0L;
            while (pos < data.Length)
            {
                var len = (int)Math.Min(1024 * 1024, data.Length - pos);
                h.Append(data.AsSpan((int)pos, len));
                pos += len;
            }
            t1.Start();
            h.Flush();
            t1.Stop();
        }
        Report("① 全量 Flush（追加→multipart）", sizeMB, t1.Elapsed);

        // ② 增量 Flush（追加 appendMB——老 part 服务端拷贝，仅增量上传）
        var append = new byte[appendMB * 1024L * 1024];
        new Random(9).NextBytes(append);
        var t2 = Stopwatch.StartNew();
        using (var h2 = fs.Open(name, new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }))
        {
            h2.Append(append);
            t2.Start();
            h2.Flush();
            t2.Stop();
        }
        Report("② 增量 Flush（仅增量上传）", appendMB, t2.Elapsed);
        Console.WriteLine($"   （增量/全量耗时比 = {t2.Elapsed.TotalMilliseconds / t1.Elapsed.TotalMilliseconds:P0}——未改 part 服务端拷贝收益）");

        // ③ 读回校验（长度 + 五点采样）
        var t3 = Stopwatch.StartNew();
        using (var r = fs.Open(name, new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting }))
        {
            var expected = (sizeMB + appendMB) * 1024L * 1024;
            Console.WriteLine(r.Length == expected ? "   长度校验 ✓" : $"   ✗ 长度 {r.Length} ≠ {expected}");
            var probe = new byte[4096];
            var ok = true;
            foreach (var off in new[] { 0L, data.Length / 2, data.Length - 4096, data.Length, expected - 4096 })
            {
                var n = r.Read(off, probe);
                ok &= n == 4096;
            }
            Console.WriteLine(ok ? "   五点采样读 ✓" : "   ✗ 采样读长度异常");
            t3.Stop();
        }
        Report("③ 读回（5 点 Range 采样）", 0, t3.Elapsed, "ops");

        // ④ 洞读加速（打洞→Flush→新句柄读洞区间——本地零填充）
        using (var hw = fs.Open(name, new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }))
        {
            hw.PunchHole(1024 * 1024, 8 * 1024 * 1024);
            hw.Flush();
        }
        var t4 = Stopwatch.StartNew();
        using (var rh = fs.Open(name, new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting }))
        {
            var buf = new byte[8 * 1024 * 1024];
            var n = rh.Read(1024 * 1024, buf);
            t4.Stop();
            var allZero = true;
            foreach (var b in buf)
                if (b != 0) { allZero = false; break; }
            Console.WriteLine(n == buf.Length && allZero
                ? $"④ 洞区间整读（8MB 全零 ✓，tier-holes 加速）: {t4.Elapsed.TotalMilliseconds:F0}ms"
                : $"   ✗ 洞读异常 n={n} allZero={allZero}");
        }

        fs.Delete(name);
        Console.WriteLine("── 完成（对象已清理）");
    }

    private static void Report(string what, int mb, TimeSpan elapsed, string unit = "MB/s")
    {
        if (unit == "MB/s")
            Console.WriteLine($"{what,-42} {mb / elapsed.TotalSeconds,8:F1} MB/s   （{elapsed.TotalSeconds,5:F1}s / {mb}MB）");
        else
            Console.WriteLine($"{what,-42} {1000 / Math.Max(1, elapsed.TotalMilliseconds),8:F1} {unit}   （{elapsed.TotalMilliseconds:F0}ms）");
    }

    private static string Pick(string[] args, string name, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return fallback;
    }

    private static int PickInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
