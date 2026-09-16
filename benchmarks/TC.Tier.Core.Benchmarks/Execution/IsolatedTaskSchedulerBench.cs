using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Shared;

/// <summary>
/// IsolatedTaskScheduler 性能基准——全部与公共线程池（<see cref="TaskScheduler.Default"/>）对照，量化：
/// <list type="bullet">
/// <item>单任务调度往返（入队 → 私有线程执行 → 完成）——隔离的每次派发税；</item>
/// <item>await continuation 回流（<c>Task.Yield</c> 循环——每次 yield 经 QueueTask 回本调度器）；</item>
/// <item>多生产者吞吐（4 池生产者 × 250 任务；默认有界队列背压 vs 无界）；</item>
/// <item>指标开/关的热路径开销（验证默认 Disabled 零开销声明）。</item>
/// </list>
/// 运行：`dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --filter *IsolatedTaskSchedulerBench*`
/// （对照实验加 `--job dry` 冒烟 / `--job short` 快跑）。结果存档与调参指引：
/// `src/TC.Tier.Core/docs/perf/dedicated-task-scheduler-perf.md`（Artifacts 不入库，跑完记得更新该文档）。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class IsolatedTaskSchedulerBench
{
    /// <summary>专用线程数 M。超过 ProcessorCount 自动钳到核数（Create 超核 throw）。</summary>
    [Params(1, 2, 4)]
    public int M { get; set; }

    /// <summary>continuation 回流循环次数（每次 yield 一次 QueueTask）。</summary>
    [Params(100)]
    public int YieldCount { get; set; }

    private const int Producers = 4;            // 吞吐基准：生产者数（跑在公共池上，贴近真实调用方）
    private const int BatchPerProducer = 250;   // 每 op 共 4×250=1000 任务

    private IsolatedTaskScheduler _m = null!;           // M=[Params]，默认有界队列
    private IsolatedTaskScheduler _mUnbounded = null!;  // M=[Params]，无界队列（纯调度容量）
    private IsolatedTaskScheduler _metricsOn = null!;   // M=2 固定，指标开（noop sink）

    [GlobalSetup]
    public void Setup()
    {
        var m = Math.Min(M, Environment.ProcessorCount);
        _m = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = m, Name = $"bench-m{m}" });
        _mUnbounded = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        { ThreadCount = m, QueueCapacity = -1, Name = $"bench-m{m}-unb" });
        _metricsOn = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        {
            ThreadCount = Math.Min(2, Environment.ProcessorCount), Name = "bench-metrics",
            Hub = ObservabilityHub.Create(new NoopSink(), tracer: null,
                new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = true } })
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _m.Dispose();
        _mUnbounded.Dispose();
        _metricsOn.Dispose();
    }

    // ===== 1. 单任务调度往返（入队 → 私有线程执行 → 完成）=====

    [Benchmark(Baseline = true, Description = "ThreadPool round-trip (StartNew on Default)")]
    public Task ThreadPool_RoundTrip()
        => Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);

    [Benchmark(Description = "IsolatedTaskScheduler round-trip")]
    public Task Isolated_RoundTrip()
        => Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, _m);

    [Benchmark(Description = "IsolatedTaskScheduler round-trip, metrics ON")]
    public Task Isolated_RoundTrip_MetricsOn()
        => Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, _metricsOn);

    // ===== 2. continuation 回流（每次 yield 经 QueueTask 回调度器私有线程，不经公共池）=====

    [Benchmark(Description = "ThreadPool yield-loop xN")]
    public async Task ThreadPool_YieldLoop()
    {
        await Task.Run(async () =>
        {
            for (var i = 0; i < YieldCount; i++) await Task.Yield();
        });
    }

    [Benchmark(Description = "IsolatedTaskScheduler yield-loop xN")]
    public async Task Isolated_YieldLoop()
    {
        await Task.Factory.StartNew(async () =>
        {
            for (var i = 0; i < YieldCount; i++) await Task.Yield();
        }, CancellationToken.None, TaskCreationOptions.None, _m).Unwrap();
    }

    // ===== 3. 多生产者吞吐（4 池生产者 × 250 no-op 任务；有界=默认 max(M*4,16) 满则阻塞背压）=====

    [Benchmark(Description = "ThreadPool throughput 4x250")]
    public Task ThreadPool_Throughput() => MultiProducer(TaskScheduler.Default);

    [Benchmark(Description = "IsolatedTaskScheduler throughput 4x250 (bounded queue)")]
    public Task Isolated_Throughput_Bounded() => MultiProducer(_m);

    [Benchmark(Description = "IsolatedTaskScheduler throughput 4x250 (unbounded queue)")]
    public Task Isolated_Throughput_Unbounded() => MultiProducer(_mUnbounded);

    /// <summary>4 个公共池生产者各投 BatchPerProducer 个任务到指定调度器，全量等完成。</summary>
    private static async Task MultiProducer(TaskScheduler scheduler)
    {
        var producerTasks = new Task[Producers];
        for (var p = 0; p < Producers; p++)
        {
            producerTasks[p] = Task.Run(async () =>
            {
                var tasks = new Task[BatchPerProducer];
                for (var i = 0; i < BatchPerProducer; i++)
                    tasks[i] = Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, scheduler);
                await Task.WhenAll(tasks);
            });
        }
        await Task.WhenAll(producerTasks);
    }

    /// <summary>零成本 sink——隔离指标采集本身的开销（非 sink 序列化成本）。</summary>
    private sealed class NoopSink : IMetricsSink
    {
        public bool IsEnabled => true;
        public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
        public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
        public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
    }
}
