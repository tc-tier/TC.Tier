using System.Reflection;
using TC.Tier.Core.Execution;
using TC.Tier.Products.Net.Node;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Structures.Snapshot;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 引擎 worker 调度器共享注入契约（TierWal 透传链）：WithWorkerScheduler 经 Builder →
/// 主日志/Managed meta/镜像快照（+快照 meta）全部引擎持注入实例——多实例线程数恒定；
/// 注入方生命周期不被引擎释放夺走（Referenced 语义）；产品档 TierRaftNodeOptions 一行透传。
/// <para>★ 引擎级两形态/所有权契约见 Runtime.Tests EngineWorkerSchedulerTests（#486）——
/// 本文件验"结构层 + 产品层"透传段（引擎与调度器均 internal 面——全反射取证，契约断言）。</para>
/// </summary>
public sealed class TierWalWorkerSchedulerTests
{
    private static readonly FieldInfo LogEngineField =
        typeof(LogBase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo LogMetaEngineField =
        typeof(LogBase).GetField("_metaEngine", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo SnapshotEngineField =
        typeof(SnapshotBase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo SnapshotMetaEngineField =
        typeof(SnapshotBase).GetField("_metaEngine", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo WalSnapshotField =
        typeof(TierWal).GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
    // 引擎 WorkerScheduler 内部属性（引擎类型 internal——按运行时类型取，不点名）
    private static readonly PropertyInfo EngineSchedulerProperty =
        typeof(LogBase).Assembly.GetTypes()
            .Single(t => t.Name == "StorageEngine")
            .GetProperty("WorkerScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static IsolatedTaskScheduler NewSharedScheduler() => IsolatedTaskScheduler.Create(
        new IsolatedSchedulerOptions { Name = "tierwal-shared-sched", ThreadCount = 2 });

    private static IsolatedTaskScheduler? SchedulerOf(object engine) =>
        (IsolatedTaskScheduler?)EngineSchedulerProperty.GetValue(engine);

    private static object LogEngineOf(TierWal wal, FieldInfo field) =>
        field.GetValue(wal.DiagnosticLog)!;

    private static object SnapshotEngineOf(TierWal wal, FieldInfo field) =>
        field.GetValue((IncrementalSnapshot)WalSnapshotField.GetValue(wal)!)!;

    [Fact]
    public async Task InjectedScheduler_FlowsToAllEngines_AndFunctionalPass()
    {
        using var vol = new TestVolume();
        using var scheduler = NewSharedScheduler();
        await using var wal = await WalTestFactory.StartAsync(vol,
            o => WalTestFactory.ManualCommit(o.WithWorkerScheduler(scheduler)));

        // 一个 TierWal 的四个引擎（主日志 + Managed meta + 镜像快照 + 快照 meta）全部持注入实例
        SchedulerOf(LogEngineOf(wal, LogEngineField)).Should().BeSameAs(scheduler, "主日志引擎");
        SchedulerOf(LogEngineOf(wal, LogMetaEngineField)).Should().BeSameAs(scheduler, "Managed meta 引擎");
        SchedulerOf(SnapshotEngineOf(wal, SnapshotEngineField)).Should().BeSameAs(scheduler, "镜像快照引擎");
        SchedulerOf(SnapshotEngineOf(wal, SnapshotMetaEngineField)).Should().BeSameAs(scheduler, "快照 Managed meta 引擎");

        // 功能面零回归：append → commit → 持久化 → 快照压缩
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        await wal.CommitAsync(default);
        wal.IsPersisted(1).Should().BeTrue();
        var n0 = await wal.SnapshotAsync(default);
        n0.Should().Be(1);
        scheduler.ThreadCount.Should().Be(2, "注入实例线程数不被引擎侧改动");
    }

    [Fact]
    public async Task TwoWals_ShareOneScheduler_OutlivesFirstDispose()
    {
        using var vol1 = new TestVolume();
        using var vol2 = new TestVolume();
        using var scheduler = NewSharedScheduler();

        var wal1 = await WalTestFactory.StartAsync(vol1,
            o => WalTestFactory.ManualCommit(o.WithWorkerScheduler(scheduler)));
        await using var wal2 = await WalTestFactory.StartAsync(vol2,
            o => WalTestFactory.ManualCommit(o.WithWorkerScheduler(scheduler)));

        try
        {
            // 两实例同用一组线程（组数↑线程数恒定——多节点同进程的核心收益）
            SchedulerOf(LogEngineOf(wal1, LogEngineField)).Should().BeSameAs(scheduler);
            SchedulerOf(LogEngineOf(wal2, LogEngineField)).Should().BeSameAs(scheduler);

            await wal1.AppendSingleAsync(WalTestFactory.Entry(1), default);
            await wal1.CommitAsync(default);
            wal1.IsPersisted(1).Should().BeTrue();
        }
        finally
        {
            await wal1.DisposeAsync();
        }

        // 先关 WAL1（Referenced 语义）：调度器不被夺走，WAL2 继续服务
        // （WAL2 全新实例——首条 entry 的 index = allocated+1 = 1）
        await wal2.AppendSingleAsync(WalTestFactory.Entry(2), default);
        await wal2.CommitAsync(default);
        wal2.IsPersisted(1).Should().BeTrue(
            $"alloc={wal2.AllocatedIndex} persisted={wal2.PersistedIndex} snapshot={wal2.SnapshotIndex}");
        scheduler.ThreadCount.Should().Be(2, "引擎释放不回收注入的调度器（所有权归注入方）");
    }

    [Fact]
    public async Task NullScheduler_FallsBackToSelfBuilt()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol,
            o => WalTestFactory.ManualCommit(o.WithWorkerScheduler(null)));

        // null = 缺省自建形态（每引擎独立调度器，非空但非注入实例）
        SchedulerOf(LogEngineOf(wal, LogEngineField)).Should().NotBeNull();
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        await wal.CommitAsync(default);
        wal.IsPersisted(1).Should().BeTrue();
    }

    [Fact]
    public void RaftNodeOptions_WithWorkerScheduler_ComposesWal()
    {
        using var scheduler = NewSharedScheduler();
        var o1 = TierRaftNodeOptions.Default.WithWorkerScheduler(scheduler);
        o1.Wal.WorkerScheduler.Should().BeSameAs(scheduler, "产品档一行透传至 Wal 配置");

        // 不可变——原实例不变
        TierRaftNodeOptions.Default.Wal.WorkerScheduler.Should().BeNull();

        // null = 回落缺省自建
        o1.WithWorkerScheduler(null).Wal.WorkerScheduler.Should().BeNull();
    }

    [Fact]
    public async Task ConfigForm_SchedulerThreads_FlowsToAllEngines()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol,
            // 工厂缺省注入 Shared 实例——配置形态断言需显式清空实例（引擎构造先判实例后读配置）
            o => WalTestFactory.ManualCommit(o.WithSchedulerThreads(2).WithWorkerScheduler(null)));

        // 配置形态 = 每引擎各自自建 2 线程（互不同实例——与共享注入形态的判别点）
        var main = SchedulerOf(LogEngineOf(wal, LogEngineField));
        var meta = SchedulerOf(LogEngineOf(wal, LogMetaEngineField));
        var snapshot = SchedulerOf(SnapshotEngineOf(wal, SnapshotEngineField));
        main.Should().NotBeNull();
        main!.ThreadCount.Should().Be(2, "主日志引擎按配置自建");
        meta.Should().NotBeNull();
        meta!.ThreadCount.Should().Be(2, "meta 引擎继承配置形态（Runtime 透传）");
        snapshot.Should().NotBeNull();
        snapshot!.ThreadCount.Should().Be(2, "快照引擎按配置自建");
        main.Should().NotBeSameAs(meta, "配置形态 = 各引擎独立自建（非共享）");
        main.Should().NotBeSameAs(snapshot, "配置形态 = 各引擎独立自建（非共享）");

        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        await wal.CommitAsync(default);
        wal.IsPersisted(1).Should().BeTrue();
    }

    [Fact]
    public async Task LogPageSizeBits_FlowsToLog()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol,
            o => WalTestFactory.ManualCommit(o.WithLogPageSizeBits(18)));

        wal.DiagnosticLog.PageSize.Should().Be(1 << 18, "页模型旋钮直达 LogBase");
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        await wal.CommitAsync(default);
        wal.IsPersisted(1).Should().BeTrue();
    }

    [Fact]
    public async Task EngineKnobs_ComposedSet_FunctionalPass()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, o => WalTestFactory.ManualCommit(o
            .WithOptimization(new StorageEngineOptimization { WorkerConsumers = 1 })
            .WithMetaTupleFlushInterval(TimeSpan.FromMilliseconds(500))
            .WithClock(TimeProvider.System)
            .WithSnapshotSegmentGrowthLimit(8L << 20)));

        // 引擎全旋钮组合下功能面零回归（append/commit/持久化/快照）
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        await wal.CommitAsync(default);
        wal.IsPersisted(1).Should().BeTrue();
        (await wal.SnapshotAsync(default)).Should().Be(1);
    }
}
