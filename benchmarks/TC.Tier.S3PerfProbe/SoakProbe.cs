using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.S3;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.S3PerfProbe;

/// <summary>
/// 稳定性 soak 探针（时长型长跑——远程云介质形态）。
/// <para>workers 路并发循环混合负载：小对象六件套（PUT→GET 校验→HEAD→Range→DELETE）+ Copy/CopyRange
/// + 周期性 multipart 全生命周期（完成/中止）——到时长截止。逐操作计数/错误归集/SHA256 读回校验
/// + 周期性进度行（含线程数/分配量漂移观测）+ 终态 multipart 残留检查。</para>
/// <para>★ 稳定性口径 MaxRetries=3（生产语义）：重试内部不可见，仅计穿透失败——探针观测的是
///   客户端可见稳定性。idleSeconds&gt;0 时轮间空闲（&gt;60s 即越过连接池空闲回收线——云端点
///   死连接防线的行为验证）。</para>
/// 用法：<c>dotnet run -c Release -- soak endpoint https://... bucket b access k secret s
///       [minutes 30] [workers 2] [objKB 64] [idleSeconds 0] [reportSeconds 60]
///       [vhost 0|1] [signingHost host]</c>
/// </summary>
internal static class SoakProbe
{
    private sealed class Stats
    {
        public long Rounds;
        public readonly ConcurrentDictionary<string, long> Ops = new();
        public long IntegrityFailures;
        public readonly ConcurrentDictionary<string, ConcurrentQueue<double>> Window = new();
        public long LastReportMs;
        public readonly ConcurrentDictionary<string, ErrorGroup> Errors = new();
        public void Sample(string op, double ms) => Window.GetOrAdd(op, _ => new ConcurrentQueue<double>()).Enqueue(ms);
        /// <summary>错误归集：key=op:类型——计数 + 首例 message（200 字截断，诊断线索不丢失）。</summary>
        public void AddError(string op, Exception ex)
        {
            var g = Errors.GetOrAdd($"{op}:{ex.GetType().Name}",
                _ => new ErrorGroup(ex.Message.Length > 200 ? ex.Message[..200] : ex.Message));
            Interlocked.Increment(ref g.Count);
        }
        public long ErrorCount => Errors.Values.Sum(static g => Volatile.Read(ref g.Count));
    }

    private sealed class ErrorGroup(string firstMessage)
    {
        public readonly string FirstMessage = firstMessage;
        public long Count;
    }

    public static int Run(string[] args)
    {
        var endpoint = TpsProbe.Arg(args, "endpoint", Environment.GetEnvironmentVariable("TIER_S3_TEST_ENDPOINT")!);
        var bucket = TpsProbe.Arg(args, "bucket", Environment.GetEnvironmentVariable("TIER_S3_TEST_BUCKET") ?? "tier-perf");
        var access = TpsProbe.Arg(args, "access", Environment.GetEnvironmentVariable("TIER_S3_TEST_ACCESS_KEY") ?? "minioadmin");
        var secret = TpsProbe.Arg(args, "secret", Environment.GetEnvironmentVariable("TIER_S3_TEST_SECRET_KEY")!);
        var minutes = TpsProbe.ArgInt(args, "minutes", 30);
        var workers = TpsProbe.ArgInt(args, "workers", 2);
        var objKB = TpsProbe.ArgInt(args, "objKB", 64);
        var idleSeconds = TpsProbe.ArgInt(args, "idleSeconds", 0);
        var reportSeconds = TpsProbe.ArgInt(args, "reportSeconds", 60);
        var vhost = TpsProbe.Arg(args, "vhost", "0") == "1";
        var signingHost = TpsProbe.Arg(args, "signingHost", Environment.GetEnvironmentVariable("TIER_S3_TEST_SIGNING_HOST"));

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
            Timeout = TimeSpan.FromMinutes(3),
            MaxRetries = 3,   // 生产语义：重试后仍失败才计为客户端可见失败
            UseVirtualHostAddressing = vhost,
            SigningHost = string.IsNullOrEmpty(signingHost) ? null : signingHost,
        });

        var prefix = $"soak/{Guid.NewGuid():N}/";
        var stats = new Stats();
        var runFor = TimeSpan.FromMinutes(minutes);
        var idle = TimeSpan.FromSeconds(idleSeconds);
        var sw = Stopwatch.StartNew();
        var allocStart = GC.GetTotalAllocatedBytes(precise: true);
        var threadsStart = System.Threading.ThreadPool.ThreadCount;

        Console.WriteLine($"── soak 探针：{endpoint} 桶={bucket} workers={workers} 时长={minutes}min 对象={objKB}KB "
            + $"idle={idleSeconds}s vhost={vhost}" + (signingHost is { Length: > 0 } ? $" signingHost={signingHost}" : ""));
        Console.WriteLine($"── 开始 {DateTime.Now:HH:mm:ss}（每 {reportSeconds}s 进度行；空闲>60s 即越过连接池空闲回收线）");

        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() => Worker(w))).ToArray();
        Task.WaitAll(tasks);

        // ── 终态验证 ──
        IReadOnlyList<MultipartUploadSession> sessions;
        try { sessions = store.ListMultipartUploadsAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { stats.AddError("list-uploads", ex); sessions = []; }
        var residue = sessions.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var leftover = SafeList(prefix);
        foreach (var e in leftover)
            TryOp("cleanup-DELETE", () => store.DeleteAsync(e.Key).AsTask());
        var allocEnd = GC.GetTotalAllocatedBytes(precise: true);
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"── 终态：耗时 {sw.Elapsed.TotalMinutes:F1}min 轮次={Interlocked.Read(ref stats.Rounds)} "
            + $"总操作={stats.Ops.Values.Sum()} 分配={(allocEnd - allocStart) / 1024.0 / 1024:F0}MB 线程 {threadsStart}→{System.Threading.ThreadPool.ThreadCount}");
        foreach (var (op, n) in stats.Ops.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"   {op,-18} n={n}");
        if (stats.ErrorCount > 0)
        {
            Console.WriteLine($"── 穿透失败 {stats.ErrorCount} 个（MaxRetries=3 后仍失败）：");
            foreach (var g in stats.Errors.Values.OrderByDescending(g => Volatile.Read(ref g.Count)))
                Console.WriteLine($"   {Volatile.Read(ref g.Count),6} × 首例: {g.FirstMessage}");
        }
        else
        {
            Console.WriteLine("── 穿透失败：0");
        }
        if (Interlocked.Read(ref stats.Rounds) == 0)
        {
            Console.WriteLine("── 结论：FAIL（零成功轮——凭证/端点配置错误嫌疑）");
            return 2;
        }
        Console.WriteLine($"── 校验失败（SHA256 不符）：{Interlocked.Read(ref stats.IntegrityFailures)}   multipart 残留：{residue.Count}   残留对象：{leftover.Count}");
        Console.WriteLine(stats.ErrorCount == 0 && stats.IntegrityFailures == 0 && residue.Count == 0
            ? "── 结论：PASS（零穿透失败、零校验失败、零残留）"
            : "── 结论：FAIL（见上）");
        return stats.ErrorCount == 0 ? 0 : 2;

        // ── 工作循环 ──
        void Worker(int w)
        {
            var rand = new Random(1000 + w);
            var payload = new byte[objKB * 1024L];
            rand.NextBytes(payload);
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
            long seq = 0;
            while (sw.Elapsed < runFor)
            {
                var round = seq++;
                var key = $"{prefix}w{w}-{round}";
                try
                {
                    // 小对象六件套（GET/Range 带 SHA256 校验——读回完整性是 soak 的核心证据）
                    TryOp("PUT", () => store.PutAsync(key, payload).AsTask());
                    var readBack = new byte[payload.Length];
                    var gotAll = TryOp("GET", async () =>
                    {
                        var n = await store.GetAsync(key, 0, readBack);
                        if (n != readBack.Length) throw new InvalidOperationException($"短读 {n}/{readBack.Length}");
                    });
                    if (gotAll && !payloadHash.Equals(Convert.ToHexString(SHA256.HashData(readBack)), StringComparison.OrdinalIgnoreCase))
                        Interlocked.Increment(ref stats.IntegrityFailures);
                    TryOp("HEAD", () => store.HeadAsync(key).AsTask());
                    var rangeBuf = new byte[32 * 1024];
                    var rangeOff = rand.Next(0, Math.Max(1, payload.Length - rangeBuf.Length));
                    var gotRange = TryOp("RangeGET", async () =>
                    {
                        var n = await store.GetAsync(key, rangeOff, rangeBuf);
                        if (n != rangeBuf.Length) throw new InvalidOperationException($"Range 短读 {n}/{rangeBuf.Length}");
                    });
                    if (gotRange && !Convert.ToHexString(SHA256.HashData(payload.AsSpan(rangeOff, rangeBuf.Length)))
                            .Equals(Convert.ToHexString(SHA256.HashData(rangeBuf)), StringComparison.OrdinalIgnoreCase))
                        Interlocked.Increment(ref stats.IntegrityFailures);
                    TryOp("Copy", () => store.CopyAsync(key, key + "-copy").AsTask());
                    TryOp("CopyRange", () => store.CopyRangeAsync(key, key + "-cr", rangeOff, rangeBuf.Length).AsTask());
                    TryOp("cleanup-DELETE", () => store.DeleteAsync(key + "-copy").AsTask());
                    TryOp("cleanup-DELETE", () => store.DeleteAsync(key + "-cr").AsTask());
                    TryOp("DELETE", () => store.DeleteAsync(key).AsTask());

                    // 周期性 multipart：每 5 轮完成一个、每 7 轮中止一个（会话治理路径）
                    if (round % 5 == 0) MultipartCycle(w, round, payload, complete: true);
                    if (round % 7 == 0) MultipartCycle(w, round, payload, complete: false);

                    Interlocked.Increment(ref stats.Rounds);
                }
                catch (Exception ex)
                {
                    stats.AddError("round", ex);
                }
                if (idle > TimeSpan.Zero)
                    Thread.Sleep(idle);
                ReportTick(w);
            }

            void MultipartCycle(int w, long round, byte[] seed, bool complete)
            {
                var mpKey = $"{prefix}w{w}-mp{(complete ? "" : "-abort")}-{round}";
                var part = new byte[5 * 1024 * 1024];
                new Random(2000 + w).NextBytes(part);
                var partHash = Convert.ToHexString(SHA256.HashData(part));
                var session = default(IMultipartUpload);
                try
                {
                    session = store.CreateMultipartUpload(mpKey);
                    var p1 = session.UploadPartAsync(1, part).AsTask().GetAwaiter().GetResult();
                    var p2 = session.UploadPartAsync(2, part).AsTask().GetAwaiter().GetResult();
                    stats.Ops.AddOrUpdate("mp-part", 2, static (_, c) => c + 2);
                    if (complete)
                    {
                        session.CompleteAsync([p1, p2]).AsTask().GetAwaiter().GetResult();
                        var back = new byte[part.Length * 2];
                        var gotMp = TryOp("mp-GET", async () =>
                        {
                            var n = await store.GetAsync(mpKey, 0, back);
                            if (n != back.Length) throw new InvalidOperationException($"mp 短读 {n}/{back.Length}");
                        });
                        // 两段同种子：按段校验（读回失败不比对——失败≠不完整）
                        if (gotMp && (!partHash.Equals(Convert.ToHexString(SHA256.HashData(back.AsSpan(0, part.Length))), StringComparison.OrdinalIgnoreCase)
                            || !partHash.Equals(Convert.ToHexString(SHA256.HashData(back.AsSpan(part.Length, part.Length))), StringComparison.OrdinalIgnoreCase)))
                            Interlocked.Increment(ref stats.IntegrityFailures);
                        TryOp("cleanup-DELETE", () => store.DeleteAsync(mpKey).AsTask());
                    }
                    else
                    {
                        session.AbortAsync().AsTask().GetAwaiter().GetResult();
                        stats.Ops.AddOrUpdate("mp-abort", 1, static (_, c) => c + 1);
                    }
                }
                catch (Exception ex)
                {
                    stats.AddError("mp", ex);
                    try { session?.AbortAsync().AsTask().GetAwaiter().GetResult(); }
                    catch { /* 残留由终态 ListMultipartUploads 检查兜底呈现 */ }
                }
            }
        }

        void ReportTick(int w)
        {
            var elapsedMs = sw.ElapsedMilliseconds;
            if (elapsedMs - Volatile.Read(ref stats.LastReportMs) < reportSeconds * 1000L)
                return;
            lock (stats)
            {
                if (sw.ElapsedMilliseconds - Volatile.Read(ref stats.LastReportMs) < reportSeconds * 1000L) return;
                Volatile.Write(ref stats.LastReportMs, sw.ElapsedMilliseconds);
                Console.WriteLine($"   [{sw.Elapsed.TotalMinutes,5:F1}min] 轮次={Interlocked.Read(ref stats.Rounds)} "
                    + $"操作={stats.Ops.Values.Sum()} 穿透失败={stats.ErrorCount} 校验失败={Interlocked.Read(ref stats.IntegrityFailures)} "
                    + $"线程={System.Threading.ThreadPool.ThreadCount} 分配={(GC.GetTotalAllocatedBytes(precise: false) - allocStart) / 1024.0 / 1024:F0}MB"
                    + WindowP95());
            }
        }

        string WindowP95()
        {
            var parts = new List<string>();
            foreach (var (op, q) in stats.Window)
            {
                if (q.IsEmpty || !q.TryDequeue(out var first)) continue;
                var samples = new List<double> { first };
                while (q.TryDequeue(out var ms)) samples.Add(ms);
                samples.Sort();
                parts.Add($"{op}p95={samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))],7:F0}ms(n={samples.Count})");
            }
            return parts.Count == 0 ? "" : "  " + string.Join(" ", parts.Take(4));
        }

        IReadOnlyList<ObjectEntry> SafeList(string p)
        {
            try { return store.ListAsync(p).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { stats.AddError("list", ex); return []; }
        }

        bool TryOp(string op, Func<Task> act)
        {
            var t = Stopwatch.StartNew();
            try
            {
                act().GetAwaiter().GetResult();
                t.Stop();
                stats.Ops.AddOrUpdate(op, 1, static (_, c) => c + 1);
                stats.Sample(op, t.Elapsed.TotalMilliseconds);
                return true;
            }
            catch (Exception ex)
            {
                t.Stop();
                stats.Ops.AddOrUpdate(op, 1, static (_, c) => c + 1);
                stats.AddError(op, ex);
                return false;
            }
        }
    }
}
