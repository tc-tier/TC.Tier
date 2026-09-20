using TC.Tier.Contracts.Structures;
using TC.Tier.Core.Execution;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Kv;

/// <summary>
/// 主索引选择（tierkv-design.md §0.1 三套索引——运行装配层；编译期声明经 [KvStore(IndexKind=...)]）。
/// <para>★ 切换数据安全（派生重放教义）：Ring 唯一数据真源，索引可重建——重启跨索引切换走
///   Ring 重放全量重建；同索引加载持久化帧快速恢复（W5）。</para>
/// </summary>
public enum KvIndexKind
{
    /// <summary>HashIndex（缺省——FasterKV 对齐：点查 O(1) 无范围；范围扫描 W7 经辅助有序索引）。</summary>
    Hash,

    /// <summary>BTree（操作闸单写多读——原生 key 序范围扫描）。</summary>
    BTree,

    /// <summary>SkipList（CAS 无锁——原生 key 序范围扫描，高并发写）。</summary>
    SkipList,
}

/// <summary>
/// TierKv 配置（不可变 record + With 链——TierWalOptions 同构装配惯例）。
/// </summary>
public sealed record TierKvOptions
{
    /// <summary>默认配置实例。</summary>
    public static TierKvOptions Default { get; } = new();

    /// <summary>引擎子目录名（Ring 数据引擎名前缀）。</summary>
    public string KvName { get; private init; } = "tier-kv";

    /// <summary>主索引选择（缺省 Hash——D 系裁定 FasterKV 对齐）。</summary>
    public KvIndexKind IndexKind { get; private init; } = KvIndexKind.Hash;

    /// <summary>哈希表初始容量（2 的幂——ProbingIndex 族校验）。默认 64K 槽：覆盖百万级 key
    /// 免启动期扩容（扩容=函数式重建+单引用发布，数据集增长期反复触发徒增稳态前开销）。</summary>
    public int HashTableCapacity { get; private init; } = 1 << 16;

    /// <summary>溢出池容量。</summary>
    public int OverflowPoolCapacity { get; private init; } = 1 << 8;

    /// <summary>Ring 段生长上限（数据引擎段生长上限）。</summary>
    public long SegmentGrowthLimit { get; private init; } = 256L * 1024 * 1024;

    /// <summary>Ring 页池页大小（字节，2 的幂）。默认 256KB——增量 flush 的写穿粒度：
    /// 页越大单次 flush 拷贝越多（结构默认 32MB 巨页会让 Committed 档单次退化到 ms 级；
    /// 1MB=44µs、256KB=9µs，实测矩阵见 perf/tierkv.md）。与 <see cref="RingMemorySize"/>
    /// 联合约束页槽数（须低于 Ring 校验上限）。</summary>
    public int RingPageSize { get; private init; } = 1 << 18;

    /// <summary>Ring 页池总容量（字节，2 的幂）。默认 64MB；页槽数 = 容量/页大小。</summary>
    public long RingMemorySize { get; private init; } = 64L * 1024 * 1024;

    /// <summary>Ring 冷读缓存占页槽数比例 [0,1]。默认 0.25——大扫描/冷读工作集建议 1.0（全页缓存）。</summary>
    public double RingColdReadRatio { get; private init; } = 0.25;

    /// <summary>Ring mutable 区占比 (0,1)。默认 0.9。</summary>
    public double RingMutableFraction { get; private init; } = 0.9;

    /// <summary>Ring 页槽数上界。默认 8192。</summary>
    public int RingMaxPageCount { get; private init; } = 8192;

    /// <summary>Ring 页池全量预分配。默认 true——启动时分配全部页，运行时写穿/驱逐
    /// 为稳定复写（零运行时 buffer 分配竞争——「预分配+稳定复写」形态）。</summary>
    public bool RingPreallocate { get; private init; } = true;

    /// <summary>checkpoint 周期泵间隔。默认 Infinite（关——持久化只由显式 CheckpointAsync /
    /// 逐写 Committed 旋钮承担）。配置后后台泵按周期自动 checkpoint（FASTER 形态编排面：
    /// 写路径内存档 + checkpoint 持久化，RPO ≈ 间隔——checkpoint 是全量落盘+索引帧，
    /// 持久化成本按周期摊薄而非逐写支付）。</summary>
    public TimeSpan CheckpointInterval { get; private init; } = Timeout.InfiniteTimeSpan;

    /// <summary>范围扫描预扫批粒度（条）。默认 512——BTree 游标每批产出的 (key, addr) 数。</summary>
    public int ScanBatchSize { get; private init; } = 512;

    /// <summary>顺序模式地址跨度阈值（Ring 页数）。批内 addr 跨度 ≤ 此值走 Ring 顺序区间扫
    /// （聚集负载最优），否则走 addr 排序批量回表（MRR）。</summary>
    public int ScanSpanPageThreshold { get; private init; } = 8;

    /// <summary>meta 策略（默认 Managed 单槽覆盖原子语义——水位自管的正路，禁 Disabled）。</summary>
    public MetaPolicyKind MetaPolicyKind { get; private init; } = MetaPolicyKind.Managed;

    /// <summary>meta 元组回扫泵周期。默认 200ms（引擎缺省）；窗口内的多次 meta 变更合并为一次落盘。</summary>
    public TimeSpan MetaTupleFlushInterval { get; private init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Ring 溢出策略（WiscKey 式 KV 分离）。默认 Disabled（Value 内联）；
    /// Enabled 时超 <see cref="RingMinOverflowSize"/> 的 Value 分离到溢出引擎——大 value 场景的
    /// 写放大/页池占用调优轴。</summary>
    public OverflowPolicy RingOverflowPolicy { get; private init; } = OverflowPolicy.Disabled;

    /// <summary>Ring 溢出阈值（字节）——Value 超过此值分离到溢出引擎（<see cref="RingOverflowPolicy"/>=Enabled 时生效）。</summary>
    public int RingMinOverflowSize { get; private init; }

    /// <summary>范围索引（W7 F1——key 字节序 BTree 辅助索引，与主索引并存双写；缺省关）。
    /// <para>★ 开启后 Scan(prefix/range) 可用；代价 = 写放大（双索引）+ 恢复多一帧。</para></summary>
    public bool EnableRangeIndex { get; private init; }

    /// <summary>Watch 订阅通道容量（F5——每订阅者有界事件通道；写满即断连不反压写路径）。</summary>
    public int WatchChannelCapacity { get; private init; } = 8192;

    /// <summary>后台过期回收扫描间隔（A.2a——ExpirySweep 水位推进式循环）。默认 60s；
    /// 非正值（Zero/Infinite/负）= 关闭后台循环（仅 <c>ForceExpirySweepAsync</c> 手动触发）。
    /// <para>★ sweep = 增量水位推进：自上次扫描水位起找最老未过期记录地址，其下前缀
    /// 复用回收线路径物理强删（过期不发事件裁定自洽；与手动 ReclaimAsync 同路不冲突）。</para></summary>
    public TimeSpan ExpiryScanInterval { get; private init; } = TimeSpan.FromSeconds(60);

    /// <summary>IO hints（默认 DIO——TierWal 同款介质适配；磁盘部署建议叠加 WriteThrough）。</summary>
    public FileOpenHints Hints { get; private init; } = FileOpenHints.NoBuffering;

    /// <summary>
    /// 引擎 worker 调度器（共享注入形态，null = 缺省自建；跨 KV 实例共享一组线程——
    /// 注入 <see cref="IsolatedTaskScheduler.Shared"/> 或自建实例，所有权 Referenced 归注入方）。
    /// 实例恒优先于 <see cref="WorkerSchedulerOptions"/> 配置形态。
    /// </summary>
    public IsolatedTaskScheduler? WorkerScheduler { get; private init; }

    /// <summary>
    /// 引擎 worker 调度器选项（配置形态——每引擎按此**自建**专用线程，null = 全默认
    /// <see cref="IsolatedTaskScheduler.RecommendedThreadCount"/>）。单实例压缩线程数用
    /// <see cref="WithSchedulerThreads"/>；跨实例共享用 <see cref="WithWorkerScheduler"/>
    /// 实例形态。实例 <see cref="WorkerScheduler"/> 非空时本项被忽略。
    /// <para>★ TierKv 一个实例装配两个引擎（-data/-index），配置形态双引擎同用。</para>
    /// </summary>
    public IsolatedSchedulerOptions? WorkerSchedulerOptions { get; private init; }

    /// <summary>With 链——引擎 worker 调度器共享注入（跨实例一组线程；null = 回落缺省自建）。
    /// 实例恒优先于配置形态（<see cref="WithSchedulerThreads"/>/<see cref="WithWorkerSchedulerOptions"/>）。</summary>
    /// <param name="scheduler">全部引擎共用的调度器实例（生命周期归注入方——TierKv 释放不回收）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithWorkerScheduler(IsolatedTaskScheduler? scheduler)
        => this with { WorkerScheduler = scheduler };

    /// <summary>With 链——引擎 worker 调度器专用线程数（配置形态——同实例双引擎同用该配置）。</summary>
    /// <param name="threadCount">专用线程数 M（1 ≤ M ≤ ProcessorCount——越界由引擎建调度器时校验抛出）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithSchedulerThreads(int threadCount)
        => this with { WorkerSchedulerOptions = new IsolatedSchedulerOptions { Name = "engine-worker", ThreadCount = threadCount } };

    /// <summary>With 链——引擎 worker 调度器选项（配置形态完整旋钮：队列容量/watchdog/重启策略等）。</summary>
    /// <param name="schedulerOptions">调度器选项；null = 清除（回落引擎默认）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithWorkerSchedulerOptions(IsolatedSchedulerOptions? schedulerOptions)
        => this with { WorkerSchedulerOptions = schedulerOptions };

    /// <summary>With 链——引擎名。</summary>
    /// <param name="name">引擎子目录名（Ring 数据引擎名前缀）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithKvName(string name) => this with { KvName = name };

    /// <summary>With 链——主索引选择。</summary>
    /// <param name="kind">主索引种类（Hash/BTree/SkipList）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithIndexKind(KvIndexKind kind) => this with { IndexKind = kind };

    /// <summary>With 链——哈希表容量。</summary>
    /// <param name="capacity">哈希表初始容量（2 的幂）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithHashTableCapacity(int capacity) => this with { HashTableCapacity = capacity };

    /// <summary>With 链——溢出池容量。</summary>
    /// <param name="capacity">溢出池容量。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithOverflowPoolCapacity(int capacity) => this with { OverflowPoolCapacity = capacity };

    /// <summary>With 链——段生长上限。</summary>
    /// <param name="limit">Ring 段生长上限（字节）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithSegmentGrowthLimit(long limit) => this with { SegmentGrowthLimit = limit };

    /// <summary>With 链——Ring 页池页大小（字节，2 的幂；默认 1MB）。</summary>
    /// <param name="pageSize">页大小（字节，2 的幂）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithRingPageSize(int pageSize) => this with { RingPageSize = pageSize };

    /// <summary>With 链——Ring 页池总容量（字节，2 的幂；默认 64MB）。</summary>
    /// <param name="memorySize">页池总容量（字节，2 的幂）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithRingMemorySize(long memorySize) => this with { RingMemorySize = memorySize };

    /// <summary>With 链——Ring 冷读缓存占比（默认 0.25；扫描/冷读工作集建议 1.0）。</summary>
    /// <param name="ratio">冷读缓存占页槽数比例 [0,1]。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithRingColdReadRatio(double ratio) => this with { RingColdReadRatio = ratio };

    /// <summary>With 链——Ring mutable 区占比（默认 0.9）。</summary>
    /// <param name="fraction">mutable 区占比 (0,1)。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithRingMutableFraction(double fraction) => this with { RingMutableFraction = fraction };

    /// <summary>With 链——Ring 页槽数上界（默认 8192）。</summary>
    /// <param name="maxPageCount">页槽数上界。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithRingMaxPageCount(int maxPageCount) => this with { RingMaxPageCount = maxPageCount };

    /// <summary>With 链——Ring 页池全量预分配（默认 true——预分配+稳定复写形态）。</summary>
    /// <param name="preallocate">是否启动时全量预分配页池。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithRingPreallocate(bool preallocate) => this with { RingPreallocate = preallocate };

    /// <summary>With 链——checkpoint 周期泵间隔（默认 Infinite 关；如 <c>TimeSpan.FromSeconds(30)</c> 开启周期持久化）。</summary>
    /// <param name="interval">checkpoint 周期泵间隔（Infinite = 关闭）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithCheckpointInterval(TimeSpan interval) => this with { CheckpointInterval = interval };

    /// <summary>With 链——范围扫描预扫批粒度（默认 512）。</summary>
    /// <param name="batchSize">预扫批粒度（条）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithScanBatchSize(int batchSize) => this with { ScanBatchSize = batchSize };

    /// <summary>With 链——顺序模式地址跨度阈值（Ring 页数，默认 8）。</summary>
    /// <param name="pages">顺序模式地址跨度阈值（Ring 页数）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithScanSpanPageThreshold(int pages) => this with { ScanSpanPageThreshold = pages };

    /// <summary>With 链——meta 策略。</summary>
    /// <param name="kind">meta 策略种类。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithMetaPolicyKind(MetaPolicyKind kind) => this with { MetaPolicyKind = kind };

    /// <summary>With 链——meta 元组回扫泵周期（默认 200ms）。</summary>
    /// <param name="interval">meta 元组回扫泵周期。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithMetaTupleFlushInterval(TimeSpan interval) => this with { MetaTupleFlushInterval = interval };

    /// <summary>With 链——Ring 溢出策略（WiscKey 式 KV 分离，大 value 场景启用）。</summary>
    /// <param name="policy">Ring 溢出策略。</param>
    /// <param name="minOverflowSize">溢出阈值（字节，超此值分离到溢出引擎）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithRingOverflowPolicy(OverflowPolicy policy, int minOverflowSize = 0)
        => this with { RingOverflowPolicy = policy, RingMinOverflowSize = minOverflowSize };

    /// <summary>With 链——IO hints。</summary>
    /// <param name="hints">IO hints（默认 DIO）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithHints(FileOpenHints hints) => this with { Hints = hints };

    /// <summary>With 链——范围索引开关（W7 F1：key 字节序 BTree 辅助索引 + Scan API）。</summary>
    /// <param name="enabled">是否开启范围索引（缺省 true）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithRangeIndex(bool enabled = true) => this with { EnableRangeIndex = enabled };

    /// <summary>With 链——Watch 订阅通道容量（F5）。</summary>
    /// <param name="capacity">每订阅者事件通道容量。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithWatchChannelCapacity(int capacity) => this with { WatchChannelCapacity = capacity };

    /// <summary>With 链——后台过期回收扫描间隔（A.2a；默认 60s，非正值 = 关闭后台循环仅手动触发）。</summary>
    /// <param name="interval">扫描间隔（TimeSpan.Zero = 关闭）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithExpiryScanInterval(TimeSpan interval) => this with { ExpiryScanInterval = interval };

    /// <summary>时钟供给源（故障注入面 件一——时钟缝；缺省 <see cref="TimeProvider.System"/> 行为零变化）。
    /// <para>TTL 过期判定走墙钟（<c>GetUtcNow</c>——跳变/漂移可注入）；清扫循环与 checkpoint 泵周期走
    /// provider Delay（假钟下由快进确定性触发）；时钟缓存戳走单调钟（换源不停走）。</para></summary>
    public TimeProvider Clock { get; private init; } = TimeProvider.System;

    /// <summary>With 链——时钟供给源（TTL/租约类时间语义注入）。</summary>
    /// <param name="clock">时钟供给源（非空）。</param>
    /// <returns>新的 TierKvOptions（不可变）。</returns>
    public TierKvOptions WithClock(TimeProvider clock) => this with { Clock = clock };
}
