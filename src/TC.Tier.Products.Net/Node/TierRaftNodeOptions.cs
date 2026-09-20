using TC.Tier.Core.Execution;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Net.Node;

/// <summary>
/// TierRaftNode 装配配置（不可变 record + With 链——装配惯例同 TierWalOptions）。
/// <para>★ 全部旋钮缺省即产品形态：TierWal 生产默认（DIO hints/组提交三维度）、 raft 默认策略、
///   多源/反熵与宿主压缩调度缺省关闭（按部署显式开启）。</para>
/// </summary>
public sealed record TierRaftNodeOptions
{
    /// <summary>默认配置。</summary>
    public static TierRaftNodeOptions Default { get; } = new();

    /// <summary>TierWal 配置（存储侧——DIO hints/Managed meta/组提交三维度生产默认）。</summary>
    public TierWalOptions Wal { get; private init; } = TierWalOptions.Default;

    /// <summary>raft 策略（选举超时/复制攒批窗口等）。</summary>
    public RaftOptions Raft { get; private init; } = RaftOptions.Default;

    /// <summary>apply 管道参数（节流/背压）。</summary>
    public ApplyPipelineOptions Apply { get; private init; } = ApplyPipelineOptions.Default;

    /// <summary>多源同步（Swarm）配置——null = 不装配多源/反熵面（缺省）。</summary>
    public SwarmOptions? Swarm { get; private init; }

    /// <summary>反熵对账周期（仅 leader 发起、对端轮转——spec-12 §6.1 件 C；Zero = 关闭，缺省）。
    /// 须 <see cref="Swarm"/> 非 null 才生效。</summary>
    public TimeSpan AntiEntropyInterval { get; private init; } = TimeSpan.Zero;

    /// <summary>快照压缩宿主调度阈值（日志增长条数 ≥ 阈值 → <see cref="Products.Wal.TierWal.SnapshotAsync"/>
    /// 一体快照+截头；0 = 关闭宿主调度，缺省）。每节点独立判定（N₀ = 各自 PersistedIndex）。</summary>
    public long SnapshotGrowthThresholdEntries { get; private init; }

    /// <summary>快照最小间隔（二期-F4 限速钩子——宿主循环两次快照的最小间隔；Zero = 不限速（缺省）。
    /// 手动触发（TriggerSnapshotAsync）同样计入冷却窗。</summary>
    public TimeSpan SnapshotMinInterval { get; private init; } = TimeSpan.Zero;

    /// <summary>压缩准入钩子（二期-F4——宿主循环压缩前调用；返回 false = 本轮跳过。
    /// 参数 = (AllocatedIndex, SnapshotIndex, AppliedIndex)。null = 无附加判定。</summary>
    public Func<long, long, long, bool>? SnapshotShouldCompactHook { get; init; }

    /// <summary>快照完成回调（二期-F4 保留期/GC 钩子——N₀ = 新快照覆盖点；宿主循环与手动触发均回调）。
    /// null = 无。</summary>
    public Action<long>? SnapshotCompletedHook { get; init; }

    /// <summary>宿主调度检查周期（快照阈值判定 + 版本发布 + 反熵轮转的公共 tick）。</summary>
    public TimeSpan HostLoopInterval { get; private init; } = TimeSpan.FromSeconds(30);

    /// <summary>快照发布块大小（多源内容切片；≤ 线协议防御界）。</summary>
    public int SnapshotBlockSize { get; private init; } = 64 * 1024;

    /// <summary>高精度定时环境（进程级——装配入口启用 Windows timeBeginPeriod(1)）。Windows 默认
    /// 15.6ms 定时器量子把全部时序敏感等待（心跳/选举窗/组提交门）钉在节拍粒度上；缺省开启，
    /// 进程生命周期生效。Linux 原生高精度，此旋钮 no-op。</summary>
    public bool HighResolutionTimer { get; private init; } = true;

    /// <summary>资源档位（装配期定性——缺省 <see cref="TierRaftResourceProfile.HighPerformance"/>）。
    /// 展开语义 = 只补缺省：显式 <see cref="WithWorkerScheduler"/> 注入恒优先。
    /// 档位不触碰 raft/apply/传输协议专用线程（活性地板）。</summary>
    public TierRaftResourceProfile ResourceProfile { get; private init; } = TierRaftResourceProfile.HighPerformance;

    // === With 链 ===

    /// <summary>With 链——TierWal 配置。</summary>
    /// <param name="wal">TierWal 配置（存储侧——DIO hints/Managed meta/组提交三维度）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithWal(TierWalOptions wal) => this with { Wal = wal };

    /// <summary>With 链——raft 策略。</summary>
    /// <param name="raft">raft 策略（选举超时/复制攒批窗口等）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithRaft(RaftOptions raft) => this with { Raft = raft };

    /// <summary>With 链——apply 管道参数。</summary>
    /// <param name="apply">apply 管道参数（节流/背压）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithApply(ApplyPipelineOptions apply) => this with { Apply = apply };

    /// <summary>With 链——多源同步配置（null = 不装配）。</summary>
    /// <param name="swarm">多源同步配置（null = 不装配多源/反熵面）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithSwarm(SwarmOptions? swarm) => this with { Swarm = swarm };

    /// <summary>With 链——反熵对账周期（Zero = 关闭；须 WithSwarm 启用）。</summary>
    /// <param name="interval">反熵对账周期（仅 leader 发起、对端轮转；Zero = 关闭）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithAntiEntropyInterval(TimeSpan interval) => this with { AntiEntropyInterval = interval };

    /// <summary>With 链——快照压缩调度阈值（条数增长；0 = 关闭）。</summary>
    /// <param name="entries">快照压缩宿主调度阈值（日志增长条数；0 = 关闭宿主调度）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithSnapshotGrowthThresholdEntries(long entries) => this with { SnapshotGrowthThresholdEntries = entries };

    /// <summary>快照调度策略（二期-F4）：最小间隔限速 + 压缩准入钩子 + 完成回调。</summary>
    /// <param name="minInterval">两次快照的最小间隔（Zero = 不限速；手动触发同样计入冷却窗）。</param>
    /// <param name="shouldCompactHook">压缩准入钩子（宿主循环压缩前调用；参数 = (AllocatedIndex, SnapshotIndex, AppliedIndex)；返回 false = 本轮跳过；null = 无附加判定）。</param>
    /// <param name="completedHook">快照完成回调（参数 = 新快照覆盖点 N₀；宿主循环与手动触发均回调；null = 无）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithSnapshotSchedule(TimeSpan minInterval,
        Func<long, long, long, bool>? shouldCompactHook = null, Action<long>? completedHook = null) =>
        this with { SnapshotMinInterval = minInterval, SnapshotShouldCompactHook = shouldCompactHook, SnapshotCompletedHook = completedHook };

    /// <summary>With 链——宿主调度检查周期。</summary>
    /// <param name="interval">宿主调度检查周期（快照阈值判定 + 版本发布 + 反熵轮转的公共 tick）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithHostLoopInterval(TimeSpan interval) => this with { HostLoopInterval = interval };

    /// <summary>With 链——快照发布块大小。</summary>
    /// <param name="blockSize">快照发布块大小（多源内容切片；≤ 线协议防御界）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithSnapshotBlockSize(int blockSize) => this with { SnapshotBlockSize = blockSize };

    /// <summary>With 链——高精度定时环境（进程级，缺省开启；false = 保持 OS 默认定时器分辨率）。</summary>
    /// <param name="enabled">是否启用 Windows timeBeginPeriod(1)（缺省 true；false = 保持 OS 默认 15.6ms）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithHighResolutionTimer(bool enabled = true) => this with { HighResolutionTimer = enabled };

    /// <summary>With 链——引擎 worker 调度器共享注入（经 <see cref="Wal"/> 透传至节点全部存储引擎）。
    /// <para>多节点同进程（Multi-Raft/测试拓扑）注入 <c>IsolatedTaskScheduler.Shared</c> 或自建小容量
    /// 实例——引擎 worker 线程从"节点数 × 每引擎 2~4 条"收敛为一组恒定线程；null = 回落缺省自建。</para></summary>
    /// <param name="scheduler">全部引擎共用的调度器实例（生命周期归注入方——TierWal 释放不回收）。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithWorkerScheduler(IsolatedTaskScheduler? scheduler)
        => this with { Wal = Wal.WithWorkerScheduler(scheduler) };

    /// <summary>With 链——资源档位（装配期定性；缺省高性能档）。
    /// <para>低资源档 = 引擎调度器收敛到进程级共享一组线程（只补缺省——显式
    /// <see cref="WithWorkerScheduler"/> 注入恒优先）。需要进一步省电可链式
    /// <see cref="WithHighResolutionTimer"/>（false = 撤销 Windows 1ms 定时器提升——
    /// 心跳/选举窗时序粒度回落 15.6ms，共识活性边际变薄，自行权衡）。</para></summary>
    /// <param name="profile">资源档位。</param>
    /// <returns>新配置实例（不可变 With 链）。</returns>
    public TierRaftNodeOptions WithResourceProfile(TierRaftResourceProfile profile)
        => this with { ResourceProfile = profile };

    /// <summary>时钟供给源（故障注入面 件一——时钟缝 P1 落点；缺省 <see cref="TimeProvider.System"/> 行为零变化）。
    /// <para>宿主 tick 循环/快照限速窗/切换 deadline 经本源驱动——假钟下由快进确定性触发。</para></summary>
    public TimeProvider Clock { get; private init; } = TimeProvider.System;

    /// <summary>With 链——时钟供给源。</summary>
    /// <param name="clock">时钟供给源（非空）。</param>
    /// <returns>替换 Clock 后的新实例。</returns>
    public TierRaftNodeOptions WithClock(TimeProvider clock) => this with { Clock = clock };
}
