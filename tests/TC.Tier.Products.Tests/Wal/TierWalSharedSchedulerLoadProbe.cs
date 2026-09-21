using System.Collections.Concurrent;
using System.Diagnostics;
using TC.Tier.Core.Execution;
using Xunit;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 共享调度器多实例负载对照探针（#505 低资源档的前置证据——LowResource 推向生产的验证面）：
/// N 个 WAL 实例并发写（每实例 1 顺序写者——AppendSingleAsync 无跨任务串行化契约），对照
/// 自建形态（每引擎 2~4 专用线程）vs 共享形态（全实例一组 2 线程）的吞吐 / p99 / 线程账。
/// <para>★ 断言只守悬崖（共享形态 p99 有界 + 全部落盘），吞吐数字打印不设门——机器噪声不进 CI 红绿；
///   磁盘介质对照设 <c>TC_TEST_FS_SPEC=local:///...</c>（TestVolume 介质切换零重编译）。</para>
/// </summary>
public class TierWalSharedSchedulerLoadProbe
{
    private const int InstanceCount = 8;      // 多实例（Multi-Raft/多节点同进程形态）
    private const int OpsPerInstance = 300;   // 每实例顺序写者 op 数（append + commit 逐条持久）

    [Fact]
    public async Task Probe_SharedVsSelfBuilt_MultiInstanceLoad()
    {
        // ① 自建形态（现网缺省——每引擎自建 2~4 线程）
        var selfBuilt = await RunTopology(sharedScheduler: null);

        // ② 共享形态（LowResource 档形态——全实例一组 2 线程）
        using var shared = IsolatedTaskScheduler.Create(
            new IsolatedSchedulerOptions { Name = "probe-shared", ThreadCount = 2 });
        var sharedResult = await RunTopology(shared);

        var line =
        $"#PROBE shared-vs-selfbuilt instances={InstanceCount} ops={OpsPerInstance * InstanceCount} medium={Environment.GetEnvironmentVariable("TC_TEST_FS_SPEC") ?? "memory:"} | " +
        $"selfbuilt: ops/s={selfBuilt.opsPerSec:F0} p99={selfBuilt.p99Ms}ms liveThreads={selfBuilt.liveThreads} | " +
        $"shared(2): ops/s={sharedResult.opsPerSec:F0} p99={sharedResult.p99Ms}ms liveThreads={sharedResult.liveThreads}";

        // 悬崖守卫（宽松——只拦「配置性饿死」形态，机器噪声不进门）：共享形态 p99 有界
        sharedResult.p99Ms.Should().BeLessThan(30_000, "共享 2 线程不得出现配置性饿死（p99 失控）");

        Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory("test_out");
            File.AppendAllText("test_out/probe-shared-scheduler.log", line + Environment.NewLine);
        }
        catch { /* 观测落盘容错——文件系统不可写不影响探针结论 */ }
    }

    private static async Task<(double opsPerSec, long p99Ms, int liveThreads)> RunTopology(
        IsolatedTaskScheduler? sharedScheduler)
    {
        var vols = new List<TestVolume>();
        var wals = new List<TierWal>();
        try
        {
            var options = WalTestFactory.ManualCommit(TierWalOptions.Default);
            options = sharedScheduler is null
                ? options
                : options.WithWorkerScheduler(sharedScheduler);
            for (var i = 0; i < InstanceCount; i++)
            {
                var vol = new TestVolume();
                vols.Add(vol);
                wals.Add(await WalTestFactory.StartAsync(vol, _ => options));
            }

            var latencies = new ConcurrentQueue<long>();
            var liveThreads = 0;
            var sw = Stopwatch.StartNew();
            // 每实例 1 顺序写者（AppendSingleAsync 无跨任务串行化契约——并发写不进契约面）
            await Task.WhenAll(wals.Select(async wal =>
            {
                for (var i = 0; i < OpsPerInstance; i++)
                {
                    var opStart = Stopwatch.GetTimestamp();
                    await wal.AppendSingleAsync(new byte[64], default);
                    await wal.CommitAsync(default);
                    latencies.Enqueue(Stopwatch.GetTimestamp() - opStart);
                }
                Interlocked.Exchange(ref liveThreads, Process.GetCurrentProcess().Threads.Count);
            }));
            sw.Stop();

            var p99 = latencies.OrderBy(t => t).ElementAt(latencies.Count * 99 / 100);
            var p99Ms = p99 * 1000 / Stopwatch.Frequency;
            var opsPerSec = latencies.Count / sw.Elapsed.TotalSeconds;
            return (opsPerSec, p99Ms, liveThreads);
        }
        finally
        {
            foreach (var wal in wals) { try { await wal.DisposeAsync(); } catch { } }
            foreach (var vol in vols) vol.Dispose();
        }
    }
}
