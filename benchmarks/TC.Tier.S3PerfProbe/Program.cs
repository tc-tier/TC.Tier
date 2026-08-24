using TC.Tier.S3PerfProbe;
using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.S3;

// S3 性能探针——用仓库自带的 S3ObjectStore（SigV4 自写）实测端点：
//   PUT/GET 吞吐、Range GET 延迟分位、小对象 TPS/延迟、并发 PUT。
// 用法：dotnet run -c Release [-- endpoint <url> bucket <name> access <k> secret <k>
//                            [sizeMB n] [count n] [small n] [concurrency n]]
// 子命令：tps（高并发小对象 TPS）/ soak（时长型稳定性长跑）/ bridge（RemoteFileSystem 桥级）
// 缺省读 TIER_S3_TEST_* 环境变量（与契约套同一套配置）。
if (args.Length > 0 && args[0] == "bridge")
{
    BridgeProbe.Run(args);
    return 0;
}
if (args.Length > 0 && args[0] == "tps")
{
    return TpsProbe.Run(args);
}
if (args.Length > 0 && args[0] == "soak")
{
    return SoakProbe.Run(args);
}
var endpoint = Arg("endpoint", Environment.GetEnvironmentVariable("TIER_S3_TEST_ENDPOINT")!);
var bucket = Arg("bucket", Environment.GetEnvironmentVariable("TIER_S3_TEST_BUCKET") ?? "tier-perf");
var access = Arg("access", Environment.GetEnvironmentVariable("TIER_S3_TEST_ACCESS_KEY") ?? "tier-minio");
var secret = Arg("secret", Environment.GetEnvironmentVariable("TIER_S3_TEST_SECRET_KEY")!);
var sizeMB = ArgInt("sizeMB", 64);
var count = ArgInt("count", 6);
var small = ArgInt("small", 200);
var concurrency = ArgInt("concurrency", 4);
var vhost = Arg("vhost", Environment.GetEnvironmentVariable("TIER_S3_TEST_VHOST") ?? "0") == "1";

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
    Timeout = TimeSpan.FromMinutes(10),
    MaxRetries = 1,   // 压测口径：失败不重试掩埋
    UseVirtualHostAddressing = vhost,
});
var prefix = $"perf/{Guid.NewGuid():N}/";
var rand = new Random(42);
var payload = new byte[sizeMB * 1024L * 1024];
rand.NextBytes(payload);
var buf64k = new byte[64 * 1024];

Console.WriteLine($"── S3 性能探针：{endpoint} 桶={bucket} 对象={sizeMB}MB×{count} 小对象={small} 并发={concurrency} vhost={vhost}");
Console.WriteLine($"                 网关路径={(endpoint.Contains("mytzz.top") ? "是（公网 TLS 反代）" : "否（直连）")}");

// ① PUT 吞吐（顺序）
var keys = Enumerable.Range(0, count).Select(i => $"{prefix}big-{i:D2}").ToArray();
var putElapsed = Measure(async () =>
{
    foreach (var k in keys)
        await store.PutAsync(k, payload);
});
Report($"PUT  {sizeMB}MB×{count}", (double)payload.Length * count / putElapsed.TotalSeconds / (1024 * 1024), putElapsed);

// ② GET 吞吐（顺序整读）
var getElapsed = await MeasureAsync(async () =>
{
    var sink = new byte[1024 * 1024];
    long total = 0;
    foreach (var k in keys)
    {
        long pos = 0;
        while (true)
        {
            var n = await store.GetAsync(k, pos, sink);
            if (n <= 0) break;
            total += n;
            pos += n;
        }
    }
    return total;
});
Report($"GET  {sizeMB}MB×{count}", (double)payload.Length * count / getElapsed.TotalSeconds / (1024 * 1024), getElapsed);

// ③ Range GET 延迟分位（64KiB 随机偏移 ×100）
var latencies = new List<double>();
var sw = Stopwatch.StartNew();
for (var i = 0; i < 100; i++)
{
    var offset = (long)rand.Next((int)(payload.Length - buf64k.Length));
    var t = Stopwatch.StartNew();
    var n = await store.GetAsync(keys[rand.Next(keys.Length)], offset, buf64k);
    t.Stop();
    if (n == buf64k.Length) latencies.Add(t.Elapsed.TotalMilliseconds);
}
ReportLatency("Range GET 64KiB", latencies);

// ④ 小对象 PUT/HEAD/DELETE（顺序 TPS + 延迟）
var smallLat = new List<double>();
sw.Restart();
for (var i = 0; i < small; i++)
{
    var t = Stopwatch.StartNew();
    await store.PutAsync($"{prefix}small-{i:D4}", buf64k);
    await store.HeadAsync($"{prefix}small-{i:D4}");
    await store.DeleteAsync($"{prefix}small-{i:D4}");
    t.Stop();
    smallLat.Add(t.Elapsed.TotalMilliseconds);
}
sw.Stop();
Console.WriteLine($"{"小对象 PUT+HEAD+DELETE ×" + small,-34} {1000.0 / (sw.Elapsed.TotalMilliseconds / small),8:F1} ops/s   （{sw.Elapsed.TotalSeconds,6:F2}s）");
ReportLatency("小对象三连", smallLat);

// ⑤ 并发 PUT（concurrency 路 × sizeMB）
var concPayloads = Enumerable.Range(0, concurrency).Select(_ =>
{
    var p = new byte[sizeMB * 1024L * 1024];
    rand.NextBytes(p);
    return p;
}).ToArray();
var concElapsed = await MeasureAsync(async () =>
{
    var tasks = Enumerable.Range(0, concurrency).Select(i =>
        store.PutAsync($"{prefix}conc-{i}", concPayloads[i]).AsTask());
    await Task.WhenAll(tasks);
    return (long)sizeMB * 1024 * 1024 * concurrency;
});
Report($"并发 PUT {concurrency}×{sizeMB}MB", (double)sizeMB * 1024 * 1024 * concurrency / concElapsed.TotalSeconds / (1024 * 1024), concElapsed);

// 清理
foreach (var e in await store.ListAsync(prefix))
    await store.DeleteAsync(e.Key);

Console.WriteLine("── 完成");
return 0;

static TimeSpan Measure(Func<Task> act)
{
    var sw = Stopwatch.StartNew();
    act().GetAwaiter().GetResult();
    return sw.Elapsed;
}

static async Task<TimeSpan> MeasureAsync(Func<Task<long>> act)
{
    var sw = Stopwatch.StartNew();
    var bytes = await act();
    Debug.Assert(bytes > 0);
    return sw.Elapsed;
}

static void Report(string what, double mbPerSec, TimeSpan elapsed)
    => Console.WriteLine($"{what,-34} {mbPerSec,8:F1} MB/s   （{elapsed.TotalSeconds,6:F2}s）");

static void ReportLatency(string what, List<double> ms)
{
    ms.Sort();
    double P(double q) => ms[Math.Min(ms.Count - 1, (int)(ms.Count * q))];
    Console.WriteLine($"{what,-34} p50={P(0.50),7:F1}ms  p95={P(0.95),7:F1}ms  p99={P(0.99),7:F1}ms  （n={ms.Count}）");
}

string Arg(string name, string fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return fallback;
}

int ArgInt(string name, int fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
    return fallback;
}
