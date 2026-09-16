using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Queue;

/// <summary>
/// TierQueue 配置参数（tc-tier-queue-spec §3.1——Options 只管零件规格，装配归 Builder，
/// 组是运行时实体不经 Options）。
/// </summary>
public sealed class TierQueueOptions
{
    /// <summary>默认队列名。</summary>
    public const string DefaultName = "tc.queue";

    /// <summary>队列名（引擎空间隔离：数据 Ring = {name}.ring，组注册表 = {name}.groups，
    /// 组状态域 = {name}.group.{组名}）。</summary>
    public string QueueName { get; init; } = DefaultName;

    /// <summary>数据 Ring 段增长上限（字节）。默认 64MB。</summary>
    public long SegmentGrowthLimit { get; init; } = 64L << 20;

    /// <summary>Ring 页大小（字节，2 的幂 [4KB, 1GB]）。默认 1MB——消息 record 粒度远小于 KV，
    /// 小页 = 更细的环绕/驱逐粒度（32MB 页是 KV 大 value 甜区）。</summary>
    public int PageSize { get; init; } = 1 << 20;

    /// <summary>Ring 内存容量（字节，≥ PageSize 且页数为 2 的幂）。
    /// 默认 64MB（64 页）。★ 不要照抄 RingSettings 的 16GB 缺省——Ring 首写 EnsureSpace 会
    /// Allocate 整个 PageCount×PageSize 跨度，mem 卷（测试/dev）把它物化成真实内存：
    /// 16GB 缺省 = 每实例 16GB 实占（2026-08-27 并行套件 OOM 实锤 RSS 31.5GB）。
    /// 磁盘大积压部署按需调大（lazy 页按需物化）。</summary>
    public long MemorySize { get; init; } = 64L << 20;

    /// <summary>组在途窗口上限（乱序 ack 空洞 Skip 表 + pending 容量——超出拒绝 = 消费侧背压）。
    /// 默认 4096（spec §5.2）。</summary>
    public int MaxInFlight { get; init; } = 4096;

    /// <summary>
    /// 缓冲确认的自动提交阈值。1（默认）禁用缓冲；大于 1 时 <c>AckBufferedAsync</c>
    /// 累积至该数量才同步持久化。普通 <c>AckAsync</c> 始终立即 durable。
    /// </summary>
    public int BufferedAckBatchSize { get; init; } = 1;

    /// <summary>
    /// 缓冲确认最长驻留时间。默认 Infinite，仅按 <see cref="BufferedAckBatchSize"/> 提交；
    /// 设为正值后，即使后续没有新确认，也会在该上限内后台提交。
    /// </summary>
    public TimeSpan BufferedAckCommitInterval { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>组注册表持久 payload 容量（字节）。默认 1MB，超过时建组明确失败，禁止 VersionedMetadata 静默截断。</summary>
    public int GroupRegistryPayloadSize { get; init; } = 1 << 20;

    /// <summary>溢出策略（超大 payload 分离到溢出引擎）。默认 Disabled（Value 内联）。</summary>
    public OverflowPolicy OverflowPolicy { get; init; } = OverflowPolicy.Disabled;

    /// <summary>Value 溢出阈值（字节；OverflowPolicy.Enabled 时生效）。默认 0 = 全部溢出。</summary>
    public int MinOverflowSize { get; init; }

    /// <summary>冷页缓存比例（冷读回源 ClockCache 占页池比例）。默认 0.25（对齐 RingSettings）。</summary>
    public double ColdReadRatio { get; init; } = 0.25;

    /// <summary>幂等生产配置（spec §6.4 档三）。null = 关闭（默认）；开启后 Enqueue 带 (ProducerId, Seq) 判重。</summary>
    public IdempotencyOptions? Idempotency { get; init; }

    // === 积压治理（spec §8.2 定案①——默认 Block 无损优先）===

    /// <summary>未确认积压上限（字节；[min 组下界, 尾) 距离）。null = 不限（默认）。</summary>
    public long? MaxBacklogBytes { get; init; }

    /// <summary>积压超限治理模式。默认 <see cref="BacklogPolicy.Block"/>（定案①）。</summary>
    public BacklogPolicy Backlog { get; init; } = BacklogPolicy.Block;

    /// <summary>Block 模式有界等待上限。默认 10s（超时抛 QueueFullException）。</summary>
    public TimeSpan BacklogBlockTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // === 死信（spec §7.3 定案③——独立 TierQueue 实例 {name}.dlq）===

    /// <summary>死信配置。null = 关闭（达上限仅推进组位点 + 告警）；默认开启（小几何——死信低流量）。</summary>
    public DeadLetterOptions? DeadLetter { get; init; } = new();

    // === 延迟队列（spec §4——SortedIndex 就绪索引搭配件）===

    /// <summary>延迟队列配置。null = 不装配索引（纯即时队列——零结构开销）；默认开启（BTree 小几何）。</summary>
    public DelayedOptions? Delayed { get; init; } = new();

    /// <summary>时钟供给源（故障注入面 件一——时钟缝；缺省 <see cref="TimeProvider.System"/> 行为零变化）。
    /// <para>延迟队列 DueTime 解析走墙钟（GetUtcNow——跳变可注入）；背压等待窗走单调戳；到期扫描/
    /// 重投退避（QueueGroup）经本源驱动——假钟下由快进确定性触发。</para></summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>With 链——时钟供给源（延迟队列/背压时间语义注入；其余属性逐项克隆）。</summary>
    /// <param name="clock">时钟供给源（非空）。</param>
    /// <returns>时钟替换后的新 TierQueueOptions。</returns>
    public TierQueueOptions WithClock(TimeProvider clock)
        => new()
        {
            QueueName = QueueName,
            SegmentGrowthLimit = SegmentGrowthLimit,
            PageSize = PageSize,
            MemorySize = MemorySize,
            MaxInFlight = MaxInFlight,
            BufferedAckBatchSize = BufferedAckBatchSize,
            BufferedAckCommitInterval = BufferedAckCommitInterval,
            GroupRegistryPayloadSize = GroupRegistryPayloadSize,
            OverflowPolicy = OverflowPolicy,
            MinOverflowSize = MinOverflowSize,
            ColdReadRatio = ColdReadRatio,
            Idempotency = Idempotency,
            MaxBacklogBytes = MaxBacklogBytes,
            Backlog = Backlog,
            BacklogBlockTimeout = BacklogBlockTimeout,
            DeadLetter = DeadLetter,
            Delayed = Delayed,
            Clock = clock,
        };
}

/// <summary>延迟子队列配置（就绪索引 BTree 的几何——索引条目小，缺省小几何）。</summary>
public sealed record DelayedOptions
{
    /// <summary>BTree 节点大小（字节）。默认 256，延迟条目密集时可增大以减少树高。</summary>
    public int NodeSize { get; init; } = 256;

    /// <summary>BTree 节点最小填充率（百分比）。默认 50。</summary>
    public int MinFillPercent { get; init; } = 50;

    /// <summary>最大延迟跨度（入队校验：DueTime - now 超限拒绝——未就绪消息钉住截断的锚定治理，spec §4.3）。
    /// 默认 24 小时。</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromHours(24);

    /// <summary>就绪索引锚点帧持久化策略（后台 dump 间隔/条目增量阈值——任一命中触发；
    /// null = BTree 缺省（30 秒/1 万条）。恢复时载帧 + 增量重放 (W, Tail)，帧无效 fail-safe 全量重放）。</summary>
    public TC.Tier.Runtime.Structures.SortedIndex.SortedIndexPersistencePolicy? PersistencePolicy { get; init; }
}

/// <summary>幂等生产配置（spec §6.4 档三——HashIndex (producerId, seq) → 首次地址判重）。</summary>
public sealed record IdempotencyOptions
{
    /// <summary>哈希表初始容量（2 的幂）。默认 1024 桶。</summary>
    public int HashTableCapacity { get; init; } = 1024;

    /// <summary>幂等 Hash 初始溢出池容量。默认 1024。</summary>
    public int OverflowPoolCapacity { get; init; } = 1024;
}

/// <summary>死信子队列配置（独立实例 {name}.dlq 的几何——流量低，小缺省）。</summary>
public sealed record DeadLetterOptions
{
    /// <summary>DLQ Ring 页大小。默认 64KB。</summary>
    public int PageSize { get; init; } = 64 << 10;

    /// <summary>DLQ Ring 内存容量。默认 8MB（mem 卷物化教训——勿抄大缺省）。</summary>
    public long MemorySize { get; init; } = 8L << 20;

    /// <summary>DLQ 段增长上限。默认 16MB。</summary>
    public long SegmentGrowthLimit { get; init; } = 16L << 20;
}
