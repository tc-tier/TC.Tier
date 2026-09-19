using FluentAssertions;
using Xunit;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 引擎 worker 调度器装配契约（#486）：线程数旋钮（WorkerScheduler 配置→Create 校验链）、
/// 外部注入共享形态（多引擎共用一组专用线程——组数↑线程数恒定）与所有权二态
/// （自建 = Owned 随引擎释放；注入 = Referenced 先关引擎不夺走调度器）。
/// <para>★ mem 介质（Reserved 直址形态）——秒级单元面。</para>
/// </summary>
public sealed class EngineWorkerSchedulerTests
{
    private static StorageEngineBuilder NewBuilder(string name,
        IsolatedTaskScheduler? workerScheduler = null, int? threadCount = null)
    {
        var options = new StorageEngineOptions(name, 1 << 20);
        if (threadCount is { } tc) options = options.WithSchedulerThreads(tc);
        return options.Builder(TierFs.New("memory:", new MemoryFileSystemOptions
        {
            Allocation = MemoryAllocationMode.Reserved,
        }), workerScheduler: workerScheduler);
    }

    private static void AppendAndRead(IStorageEngine engine, byte payload)
    {
        var addr = engine.Append([payload, payload, payload]);
        Span<byte> dst = stackalloc byte[3];
        engine.Read(addr, dst).Should().Be(3);
        dst.ToArray().Should().Equal([payload, payload, payload]);
    }

    [Fact]
    public void WithSchedulerThreads_ThreadCountFlowsToScheduler()
    {
        using var builder = NewBuilder("sched-knob", threadCount: 2);
        using var engine = builder.Start();
        engine.WaitForReady();

        builder.Engine.WorkerScheduler.ThreadCount.Should().Be(2,
            "WithSchedulerThreads 旋钮经 WorkerScheduler 配置直达 IsolatedTaskScheduler.Create");
        AppendAndRead(engine, 0xAB);
    }

    [Fact]
    public void DefaultOptions_SchedulerNamedEngineWorker()
    {
        using var builder = NewBuilder("sched-default");
        using var engine = builder.Start();
        engine.WaitForReady();

        var scheduler = builder.Engine.WorkerScheduler;
        scheduler.Should().NotBeNull();
        scheduler.ThreadCount.Should().BePositive("全默认 = RecommendedThreadCount（2~4）");
        AppendAndRead(engine, 0x11);
    }

    [Fact]
    public void InjectedScheduler_SharedAcrossEngines_OutlivesFirstEngine()
    {
        using var scheduler = IsolatedTaskScheduler.Create(
            new IsolatedSchedulerOptions { Name = "sched-shared-test", ThreadCount = 2 });
        scheduler.ThreadCount.Should().Be(2);

        var engine1 = NewBuilder("sched-shared-1", workerScheduler: scheduler).Start();
        var engine2 = NewBuilder("sched-shared-2", workerScheduler: scheduler).Start();
        try
        {
            engine1.WaitForReady();
            engine2.WaitForReady();

            // 两引擎同用一个调度器实例（共享形态——线程数随实例数恒定）
            NewEngineRef(engine1).WorkerScheduler.Should().BeSameAs(scheduler);
            NewEngineRef(engine2).WorkerScheduler.Should().BeSameAs(scheduler);

            AppendAndRead(engine1, 0x01);
            AppendAndRead(engine2, 0x02);

            // 先关引擎（Referenced 语义）：调度器不被释放，另一引擎继续服务
            engine1.Dispose();
            AppendAndRead(engine2, 0x03);
            scheduler.ThreadCount.Should().Be(2, "引擎释放不夺走注入的调度器（所有权归调用方）");
        }
        finally
        {
            engine2.Dispose();
        }

        // 调度器仍可用（调用方全权处置）
        scheduler.ThreadCount.Should().Be(2);
    }

    /// <summary>IStorageEngine → 内部实现（IVT 白盒面——调度器引用断言用）。</summary>
    private static StorageEngine NewEngineRef(IStorageEngine engine) => (StorageEngine)engine;
}
