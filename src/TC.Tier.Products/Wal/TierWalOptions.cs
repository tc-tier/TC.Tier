using TC.Tier.Core.Execution;

namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 配置（不可变 record + With 链——装配惯例同 StorageEngineOptions）。
/// </summary>
public sealed record TierWalOptions
{
    /// <summary>默认配置实例——全部配置项取声明缺省值（Managed meta 策略、16KB opaque 区、组提交三维度缺省）。</summary>
    public static TierWalOptions Default { get; } = new();

    /// <summary>引擎子目录名。</summary>
    public string WalName { get; private init; } = "tier-wal";

    /// <summary>段上限（引擎段生长上限——TierWAL 自管段 anchor 表按引擎段记录）。</summary>
    public long SegmentGrowthLimit { get; private init; } = 256L * 1024 * 1024;

    /// <summary>★ 默认回落最优模式（零决策设计）：Managed（独立 .meta 引擎、恒单段、单槽覆盖原子语义、O(1) 恢复）。</summary>
    public MetaPolicyKind MetaPolicyKind { get; private init; } = MetaPolicyKind.Managed;

    /// <summary>
    /// ★ opaque 容量 TierWAL 自配（配置无固定上限）——默认 16KB。
    /// <para>容器布局 = [TailIndex 8B][HeadIndex 8B][段表条目数 4B][pad 4B][段 anchor 表 N×24B][raft 元数据预留区]；
    ///   段表容量 = (MetaOpaqueBytes − 24 − raft 区字节) / 24；raft 预留区 = opaque 剩余（配置表达）。</para>
    /// <para>★ 底层 Settings 基类默认 0——TierWAL 必须显式配置 &gt; 0（0 = 无 opaque 区，搭车通道不可用）。</para>
    /// </summary>
    public int MetaOpaqueBytes { get; private init; } = 16 * 1024;

    /// <summary>
    /// ★ IO hints——默认 <see cref="FileOpenHints.NoBuffering"/>（DIO）。
    /// <para>★ 依据（storage-engine-perf-baseline.md §3.5 写矩阵）：TierWal 页模型默认 4MB 页
    ///   → 页刷 = 大块顺序写，落在 DIO 甜区（写交叉点 ~256K；4M 块 DIO = 缓存写 4.3×，免双缓冲
    ///   拷贝 + 免内核 writeback 线程竞争）。mem 等不支持介质自动降级（UnbufferedSupport=Ignored，
    ///   零分支）。</para>
    /// <para>★ 磁盘介质调优（tierwal.md §2 写矩阵实测）：HDD 上 DIO 单独是反模式（p99=178ms），
    ///   须叠加 <see cref="FileOpenHints.WriteThrough"/>（DIO+WT p99=0.43ms，全介质最优）。
    ///   raft 磁盘部署建议：<c>.WithHints(FileOpenHints.NoBuffering | FileOpenHints.WriteThrough)</c>。
    ///   mem 介质 WriteThrough 无副作用（no-op）但会令引擎 Flush 短路——当前仅磁盘显式选。</para>
    /// <para>★ 读侧权衡：DIO 直读比页缓存慢 1.6-3.6×——TierWal 复制/重放读经游标自管页缓存
    ///   （usePageCache）命中吸收，尾部热读不受影响。</para>
    /// </summary>
    public FileOpenHints Hints { get; private init; } = FileOpenHints.NoBuffering;

    /// <summary>
    /// 持久化质量地板验证（StartAsync 零 IO fail-fast）：Network 介质直接拒绝；Virtual 介质须
    /// CarrierWriteThrough 挂载（契约① 选举窗口——基线档实测 p99.9 ≥150ms 不达标）。默认 true；
    /// 基准探针测量基线档 / 非 raft 用途时显式关闭。
    /// </summary>
    public bool DurabilityValidation { get; private init; } = true;

    // === 组提交三维度（两提交形态由配置表达）===

    /// <summary>时间维度（距上次提交）。默认 10ms；-1ms 禁用时间维度。</summary>
    public TimeSpan CommitInterval { get; private init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>数据量维度（未提交字节 ≥ 此值触发）。默认 64KB。</summary>
    public long MaxUnflushedBytes { get; private init; } = AlignmentConst.Alignment64K;

    /// <summary>条数维度（未提交条数 ≥ 此值触发）。默认 1000。</summary>
    public int MaxUnflushedCount { get; private init; } = 1000;

    /// <summary>
    /// 引擎 worker 调度器（共享注入形态，null = 缺省自建）。
    /// <para>★ 一个 TierWal = 主日志 + Managed meta + 镜像快照（+快照 meta）多个引擎：null = 各引擎
    ///   自建专用调度线程（2~4 条/引擎——多实例线程数与实例数线性绑定）；注入 = 全部引擎共用该实例
    ///   （所有权 Referenced——TierWal 释放不回收，生命周期归注入方），多实例线程数恒定。</para>
    /// <para>嵌入式/多节点同进程（Multi-Raft、测试拓扑）注入 <c>IsolatedTaskScheduler.Shared</c>
    ///   或自建小容量实例——一组线程服务全部引擎。磁盘介质高频写场景慎用小容量（worker 排队
    ///   会拖慢组提交），2~4 线程起步按压测定。显式实例恒优先于 <see cref="WorkerSchedulerOptions"/>
    ///   配置形态（引擎构造：实例参数先于选项配置判定）。</para>
    /// </summary>
    public IsolatedTaskScheduler? WorkerScheduler { get; private init; }

    /// <summary>
    /// 引擎 worker 调度器选项（配置形态——每引擎按此**自建**专用线程，null = 全默认
    /// <see cref="IsolatedTaskScheduler.RecommendedThreadCount"/>）。单实例想压缩线程数用
    /// <see cref="WithSchedulerThreads"/>；跨实例共享一组线程用 <see cref="WithWorkerScheduler"/>
    /// 实例形态。实例 <see cref="WorkerScheduler"/> 非空时本项被忽略。
    /// </summary>
    public IsolatedSchedulerOptions? WorkerSchedulerOptions { get; private init; }

    /// <summary>日志页大小位宽（PageSize = 1 &lt;&lt; 此值）。默认 22（4MB——DIO 大块顺序写甜区，
    /// 见 <see cref="Hints"/> 依据）。内存受限/小写入场景可调小（≥ 扇区位宽）。</summary>
    public int LogPageSizeBits { get; private init; } = 22;

    /// <summary>引擎优化参数（worker 消费者数/节流双阈/段表初始容量等——
    /// <see cref="StorageEngineOptimization"/> 全族）。缺省 = 引擎默认。</summary>
    public StorageEngineOptimization Optimization { get; private init; } = new();

    /// <summary>引擎 meta 元组耐久化泵周期。默认 200ms（引擎默认）。</summary>
    public TimeSpan MetaTupleFlushInterval { get; private init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>引擎时钟（节流自旋窗/meta 耐久化泵周期驱动源）。缺省 <see cref="TimeProvider.System"/>。</summary>
    public TimeProvider Clock { get; private init; } = TimeProvider.System;

    /// <summary>镜像快照引擎段增长上限。默认 64MB。</summary>
    public long SnapshotSegmentGrowthLimit { get; private init; } = 64L * 1024 * 1024;

    // ★ 单条提交形态 = 三维度全 0（每次 Append 即触发提交）；显式同步点仍可随时调 ITierWal.CommitAsync
    //   （策略自动提交与显式提交并存）。

    /// <summary>With 链——换名。</summary>
    /// <param name="name">引擎子目录名。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithWalName(string name) => this with { WalName = name };

    /// <summary>With 链——段上限。</summary>
    /// <param name="limit">引擎段生长上限（字节）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithSegmentGrowthLimit(long limit) => this with { SegmentGrowthLimit = limit };

    /// <summary>With 链——meta 策略（Managed/Transport/Disabled；Transport + <see cref="TierWalBuilder.WithMetaTransport"/>
    /// 注入 <see cref="TC.Tier.Runtime.Meta.MetadataMetaTransport"/> = 元数据托管版本链（meta.md §7 推荐形态——构建期一行注入，
    /// EntryLog 生命周期自动托管）。</summary>
    /// <param name="kind">meta 策略种类。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithMetaPolicyKind(MetaPolicyKind kind) => this with { MetaPolicyKind = kind };

    /// <summary>With 链——opaque 容器容量（段表 + raft 元数据共用）。</summary>
    /// <param name="bytes">opaque 容器容量（字节，须 &gt; 0）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithMetaOpaqueBytes(int bytes) => this with { MetaOpaqueBytes = bytes };

    /// <summary>With 链——IO hints。</summary>
    /// <param name="hints">IO hints（默认 DIO）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithHints(FileOpenHints hints) => this with { Hints = hints };

    /// <summary>With 链——持久化质量地板验证开关（默认 true）。</summary>
    /// <param name="enabled">是否启用启动期持久化地板验证。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithDurabilityValidation(bool enabled) => this with { DurabilityValidation = enabled };

    /// <summary>With 链——组提交时间维度。</summary>
    /// <param name="interval">距上次提交的时间阈值（-1ms 禁用时间维度）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithCommitInterval(TimeSpan interval) => this with { CommitInterval = interval };

    /// <summary>With 链——组提交数据量维度。</summary>
    /// <param name="bytes">未提交字节阈值。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithMaxUnflushedBytes(long bytes) => this with { MaxUnflushedBytes = bytes };

    /// <summary>With 链——组提交条数维度。</summary>
    /// <param name="count">未提交条数阈值。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithMaxUnflushedCount(int count) => this with { MaxUnflushedCount = count };

    /// <summary>With 链——引擎 worker 调度器共享注入（null = 回落缺省自建）。</summary>
    /// <param name="scheduler">全部引擎共用的调度器实例（生命周期归注入方——TierWal 释放不回收）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithWorkerScheduler(IsolatedTaskScheduler? scheduler) => this with { WorkerScheduler = scheduler };

    /// <summary>With 链——调度器配置形态：每引擎自建专用线程数（其余调度器选项全默认）。
    /// 实例 <see cref="WithWorkerScheduler"/> 注入时本项被忽略。</summary>
    /// <param name="threadCount">每引擎专用线程数（1 ≤ M ≤ ProcessorCount）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithSchedulerThreads(int threadCount)
        => this with { WorkerSchedulerOptions = new IsolatedSchedulerOptions { Name = "engine-worker", ThreadCount = threadCount } };

    /// <summary>With 链——调度器配置形态完整旋钮（队列容量/watchdog/重启策略等）。</summary>
    /// <param name="schedulerOptions">调度器选项（诊断名建议保留 "engine-worker" 前缀）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithWorkerSchedulerOptions(IsolatedSchedulerOptions? schedulerOptions)
        => this with { WorkerSchedulerOptions = schedulerOptions };

    /// <summary>With 链——日志页大小位宽（默认 22 = 4MB；≥ 扇区位宽）。</summary>
    /// <param name="pageSizeBits">页大小位宽。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithLogPageSizeBits(int pageSizeBits) => this with { LogPageSizeBits = pageSizeBits };

    /// <summary>With 链——引擎优化参数（worker 消费者数/节流双阈等全族）。</summary>
    /// <param name="optimization">引擎优化参数（<see cref="StorageEngineOptimization"/>）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithOptimization(StorageEngineOptimization optimization) => this with { Optimization = optimization };

    /// <summary>With 链——引擎 meta 元组耐久化泵周期（默认 200ms）。</summary>
    /// <param name="interval">耐久化泵周期。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithMetaTupleFlushInterval(TimeSpan interval) => this with { MetaTupleFlushInterval = interval };

    /// <summary>With 链——引擎时钟（节流自旋窗/meta 耐久化泵驱动源）。</summary>
    /// <param name="clock">时钟供给源（非空）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithClock(TimeProvider clock) => this with { Clock = clock };

    /// <summary>With 链——镜像快照引擎段增长上限（默认 64MB）。</summary>
    /// <param name="limit">快照引擎段增长上限（字节）。</param>
    /// <returns>新的 TierWalOptions（不可变）。</returns>
    public TierWalOptions WithSnapshotSegmentGrowthLimit(long limit) => this with { SnapshotSegmentGrowthLimit = limit };

    /// <summary>完整 builder——注入面开放 + StartAsync 一步到位。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <returns>配置好的 TierWalBuilder 实例。</returns>
    public TierWalBuilder Builder(IFileSystem fs) => new(fs, this);
}
