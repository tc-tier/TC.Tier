using System.Collections.Concurrent;
using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.S3;

namespace TC.Tier.S3PerfProbe;

/// <summary>
/// 高并发小对象 TPS 探针（本地/低延迟端点专用形态）。
/// <para>workers 路并发循环 PUT→HEAD→GET→DELETE（四操作各计一次），到时长截止；
/// 逐操作延迟分位 + 按类型错误计数——压测口径 MaxRetries=1（失败不掩埋，与主探针一致）。</para>
/// 用法：<c>dotnet run -c Release -- tps endpoint http://127.0.0.1:19000 bucket tier-perf
///       access minioadmin secret minioadmin [workers 32] [seconds 60] [objKB 64]
///       [vhost 0|1] [signingHost &lt;host&gt;]</c>
/// </summary>
internal static class TpsProbe
{
    public static int Run(string[] args)
    {
        var endpoint = Arg(args, "endpoint", Environment.GetEnvironmentVariable("TIER_S3_TEST_ENDPOINT")!);
        var bucket = Arg(args, "bucket", Environment.GetEnvironmentVariable("TIER_S3_TEST_BUCKET") ?? "tier-perf");
        var access = Arg(args, "access", Environment.GetEnvironmentVariable("TIER_S3_TEST_ACCESS_KEY") ?? "minioadmin");
        var secret = Arg(args, "secret", Environment.GetEnvironmentVariable("TIER_S3_TEST_SECRET_KEY")!);
        var workers = ArgInt(args, "workers", 32);
        var seconds = ArgInt(args, "seconds", 60);
        var objKB = ArgInt(args, "objKB", 64);
        var vhost = Arg(args, "vhost", "0") == "1";
        var signingHost = Arg(args, "signingHost", Environment.GetEnvironmentVariable("TIER_S3_TEST_SIGNING_HOST"));

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(secret))
        {
            Console.Error.WriteLine("须提供 endpoint/secret（参数或 TIER_S3_TEST_* 环境变量）");
            return 1;
        }

        using var store = S3ObjectStore.Create(new S3ClientOptions
        {
            Endpoint = endpoint,
            Bucket = bucket,
            Region = "us-east-1",
            Credentials = new StaticCredentials(access, secret),
            Timeout = TimeSpan.FromMinutes(2),
            MaxRetries = 1,   // 压测口径：失败不重试掩埋
            UseVirtualHostAddressing = vhost,
            SigningHost = string.IsNullOrEmpty(signingHost) ? null : signingHost,
        });

        var prefix = $"tps/{Guid.NewGuid():N}/";
        var cutoff = Stopwatch.StartNew();
        var runFor = TimeSpan.FromSeconds(seconds);

        Console.WriteLine($"── TPS 探针：{endpoint} 桶={bucket} workers={workers} 时长={seconds}s 对象={objKB}KB vhost={vhost}"
            + (signingHost is { Length: > 0 } ? $" signingHost={signingHost}" : ""));

        var latencies = new ConcurrentDictionary<string, ConcurrentBag<double>>();
        var errors = new ConcurrentBag<(string Op, string Error)>();
        var opCounts = new ConcurrentDictionary<string, long>();
        long totalOps = 0;

        var rand = new Random(42);
        var payloadTemplate = new byte[objKB * 1024L];
        rand.NextBytes(payloadTemplate);

        cutoff.Start();
        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            var payload = new byte[objKB * 1024L];
            Array.Copy(payloadTemplate, payload, payload.Length);
            long seq = 0;
            while (cutoff.Elapsed < runFor)
            {
                var key = $"{prefix}w{w}-{seq++}";
                // 一轮 = PUT → HEAD → GET(整读) → DELETE：四操作各计 TPS 与延迟
                if (!await Op("PUT", latencies, errors, opCounts, () => store.PutAsync(key, payload).AsTask()))
                    continue;   // 后续操作依赖对象存在——PUT 失败跳过本轮
                if (!await Op("HEAD", latencies, errors, opCounts, () => store.HeadAsync(key).AsTask()))
                    continue;
                if (!await Op("GET", latencies, errors, opCounts, async () =>
                        {
                            var sink = new byte[payload.Length];
                            var n = await store.GetAsync(key, 0, sink);
                            if (n != sink.Length) throw new InvalidOperationException($"GET 短读 {n}/{sink.Length}");
                        }))
                    continue;
                await Op("DELETE", latencies, errors, opCounts, () => store.DeleteAsync(key).AsTask());
                Interlocked.Increment(ref totalOps);
            }
        })).ToArray();
        Task.WaitAll(tasks);
        var elapsed = cutoff.Elapsed;

        // 残留清理（DELETE 失败轮的对象）
        var leftover = store.ListAsync(prefix).AsTask().GetAwaiter().GetResult();
        foreach (var e in leftover)
            store.DeleteAsync(e.Key).AsTask().GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"── 聚合：{totalOps} 轮（PUT+HEAD+GET+DELETE） = {totalOps * 4.0 / elapsed.TotalSeconds:F0} ops/s   总耗时 {elapsed.TotalSeconds:F1}s");
        foreach (var op in new[] { "PUT", "HEAD", "GET", "DELETE" })
        {
            var n = opCounts.GetValueOrDefault(op, 0);
            Console.WriteLine($"   {op,-6} n={n,-7} {n / elapsed.TotalSeconds,8:F1} ops/s");
            if (latencies.TryGetValue(op, out var ms) && !ms.IsEmpty)
                ReportLatency($"   {op} 延迟", ms.ToList());
        }
        if (!errors.IsEmpty)
        {
            Console.WriteLine($"── 错误 {errors.Count} 个（按类型）：");
            foreach (var g in errors.GroupBy(e => $"{e.Op}:{e.Error}").OrderByDescending(g => g.Count()))
                Console.WriteLine($"   {g.Count(),6} × {g.Key}");
        }
        else
        {
            Console.WriteLine("── 错误：0");
        }
        if (leftover.Count > 0)
            Console.WriteLine($"── 清理残留对象 {leftover.Count} 个（DELETE 失败轮）");
        Console.WriteLine(leftover.Count == 0 && errors.IsEmpty
            ? "── 结论：PASS（零错误、零残留）"
            : "── 结论：FAIL（见上）");
        return errors.IsEmpty ? 0 : 2;
    }

    /// <summary>单操作执行 + 计时 + 计数；异常按类型归集不中断压测。</summary>
    private static async Task<bool> Op(string op,
        ConcurrentDictionary<string, ConcurrentBag<double>> latencies,
        ConcurrentBag<(string, string)> errors,
        ConcurrentDictionary<string, long> opCounts, Func<Task> act)
    {
        var t = Stopwatch.StartNew();
        try
        {
            await act();
            t.Stop();
            latencies.GetOrAdd(op, _ => new ConcurrentBag<double>()).Add(t.Elapsed.TotalMilliseconds);
            opCounts.AddOrUpdate(op, 1, static (_, c) => c + 1);
            return true;
        }
        catch (Exception ex)
        {
            t.Stop();
            opCounts.AddOrUpdate(op, 1, static (_, c) => c + 1);
            errors.Add((op, ex.GetType().Name switch
            {
                var n when ex.Message.Contains("403") || ex.Message.Contains("AccessDenied") => $"{n}(403)",
                var n when ex.Message.Contains("503") || ex.Message.Contains("SlowDown") => $"{n}(503)",
                var n => n,
            }));
            return false;
        }
    }

    internal static void ReportLatency(string what, List<double> ms)
    {
        ms.Sort();
        double P(double q) => ms[Math.Min(ms.Count - 1, (int)(ms.Count * q))];
        Console.WriteLine($"{what,-20} p50={P(0.50),7:F1}ms  p95={P(0.95),7:F1}ms  p99={P(0.99),7:F1}ms  （n={ms.Count}）");
    }

    internal static string Arg(string[] args, string name, string? fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return fallback ?? throw new InvalidOperationException($"required arg {name}");
    }

    internal static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
