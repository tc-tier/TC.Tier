using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Storage;

namespace TC.Tier.Products.Queue;

/// <summary>入队结果——分配的消息地址（地址一等公民：游标/溯源/跳读直接引用）。</summary>
/// <param name="Address">入队分配的消息地址。</param>
public readonly record struct EnqueueResult(LogicalAddress Address);

/// <summary>
/// TierQueue——本地持久队列（tc-tier-queue-spec）。
/// <para>★ P0 核心流：Enqueue（append-only）/ Dequeue（游标拉取）/ Ack（位点原子推进 + 截断）/
///   Flush / Truncate。队列级面委托内建组 "$default"——地址档基线语义（at-least-once，无 token）。</para>
/// <para>★ P1 消费组：<see cref="CreateGroupAsync"/>（独立位点/独立投递簿记/定格配置）+
///   <see cref="CreateConsumerAsync"/>（token 档——fencing/deliver-once）+ 治理三模式 +
///   <see cref="GetStatsAsync"/>。</para>
/// <para>★ 交付语义（spec §5/§6.0）：at-least-once——崩溃后从持久位点重放；业务消费须幂等。</para>
/// <para>★ 分配与持久化分离（同 TierWal 惯例）：Enqueue 返回 = 已入内存；落盘由 AckAsync
///   （契约：ack 数据先于位点持久）或显式 <see cref="FlushAsync"/> 触发。</para>
/// </summary>
public interface ITierQueue : ILifecycle<TierQueueRecoveryHints>, IDisposable, IAsyncDisposable
{
    // ══ 生产（多生产者并发安全——Ring tail 分配串行化，写入序 = 地址序；治理见 MaxBacklogBytes）══

    /// <summary>入队单条消息，返回分配地址。</summary>
    /// <param name="payload">消息字节。</param>
    /// <param name="ct">取消令牌（溢出引擎异步写路径 / Block 治理等待响应）。</param>
    /// <returns>分配的消息地址。</returns>
    /// <exception cref="QueueFullException">积压治理 Reject / Block 超时。</exception>
    ValueTask<EnqueueResult> EnqueueAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>入队（全选项——P3：DueTime 延迟投递 / ProducerId+Seq 幂等生产档）。</summary>
    /// <param name="opt">入队选项。</param>
    /// <param name="payload">消息字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分配地址（幂等命中 = 首次地址）。</returns>
    /// <exception cref="InvalidOperationException">延迟未开启 Options.Delayed / 跨度超 MaxDelay / 幂等未开启。</exception>
    ValueTask<EnqueueResult> EnqueueAsync(EnqueueOptions opt, ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>入队一批消息（逐条分配，地址连续推进）。</summary>
    /// <param name="payloads">消息字节列表。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>各消息的分配地址（与入参一一对应）。</returns>
    ValueTask<IReadOnlyList<EnqueueResult>> EnqueueBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct);

    /// <summary>按同一组选项入队一批消息（Seq 存在时按批内序号递增，保证幂等键不重复）。</summary>
    /// <param name="opt">批量入队选项。</param>
    /// <param name="payloads">消息字节列表。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>各消息的分配地址（与入参一一对应）。</returns>
    ValueTask<IReadOnlyList<EnqueueResult>> EnqueueBatchAsync(
        EnqueueOptions opt, IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct);

    // ══ 队列级消费（P0 兼容面——委托 "$default" 组，地址档基线：无 token 无 fencing）══

    /// <summary>
    /// 从 "$default" 组持久位点起顺序拉取 ≤ maxCount 条（登记 pending；不推进位点）。
    /// 无可投消息 = 空列表。
    /// </summary>
    /// <param name="maxCount">单批最大条数。</param>
    /// <param name="ct">取消令牌（冷区异步回源途中响应）。</param>
    /// <returns>投递列表（地址序）。</returns>
    ValueTask<IReadOnlyList<QueueDelivery>> DequeueAsync(int maxCount, CancellationToken ct);

    /// <summary>
    /// 确认一批消息（"$default" 组——幂等；乱序 ack 空洞进 Skip 表）。
    /// <para>★ 契约（spec §5）：①payload 先于位点持久化；②返回 = 位点已持久；③随后自动截断。</para>
    /// </summary>
    /// <param name="acked">要确认的消息地址列表。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask AckAsync(IReadOnlyList<LogicalAddress> acked, CancellationToken ct);

    /// <summary>
    /// 缓冲确认（可选吞吐路径）。返回不代表已持久；达到
    /// <see cref="TierQueueOptions.BufferedAckBatchSize"/> 或调用
    /// <see cref="FlushAcknowledgementsAsync"/> 后才 durable。进程故障前的缓冲项会按 at-least-once 重投。
    /// </summary>
    ValueTask AckBufferedAsync(IReadOnlyList<LogicalAddress> acked, CancellationToken ct);

    /// <summary>持久化 "$default" 组的全部缓冲确认。</summary>
    ValueTask FlushAcknowledgementsAsync(CancellationToken ct);

    /// <summary>"$default" 组连续已确认前缀的下一地址（持久真源——恢复重放起点）。</summary>
    LogicalAddress GroupCursor { get; }

    // ══ 消费组（P1——spec §3/§5）══

    /// <summary>创建消费组（独立持久位点 + 独立投递簿记；组配置创建时定格）。已存在 → 抛。</summary>
    /// <param name="opt">组配置（名/起点/可见性/重投上限）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>消费组句柄（token 档消费面）。</returns>
    ValueTask<ITierQueueConsumer> CreateGroupAsync(GroupOptions opt, CancellationToken ct);

    /// <summary>已注册组名清单。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>组名列表。</returns>
    ValueTask<IReadOnlyList<string>> ListGroupsAsync(CancellationToken ct);

    /// <summary>删除组（状态销毁 + 引擎目录回收 + 截断下界抬升）。"$default" 不可删。</summary>
    /// <param name="name">组名。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask DeleteGroupAsync(string name, CancellationToken ct);

    /// <summary>复位组（fence 解除 + 位点按 startAt 重置 + 代次 +1）——EvictOldest 淘汰后的显式续命路。</summary>
    /// <param name="name">组名。</param>
    /// <param name="startAt">复位起点形态。</param>
    /// <param name="startAddress">Address 形态起点。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask ResetGroupAsync(string name, GroupStartAt startAt, LogicalAddress startAddress, CancellationToken ct);

    /// <summary>取既有组的消费句柄（token 档——多消费者 = 多句柄同组）。</summary>
    /// <param name="groupName">组名。</param>
    /// <returns>消费组句柄。</returns>
    ITierQueueConsumer CreateConsumerAsync(string groupName);

    /// <summary>★ 组状态域参与者（spec §6.3 档二——Session 域注册：
    /// SessionManager.Create(fs, name, ("biz", 业务存储), (name, queue.GetGroupParticipant(g)))，
    /// AckInRoundAsync 的确认与业务效果同域 2PC 原子）。</summary>
    /// <param name="groupName">组名。</param>
    /// <returns>组状态 VersionedMetadata 参与者。</returns>
    Contracts.Transactions.ITransactionParticipant GetGroupParticipant(string groupName);

    /// <summary>队列观测（各组滞后/在途/fence/积压水位——spec §8.3）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>队列统计快照。</returns>
    ValueTask<QueueStats> GetStatsAsync(CancellationToken ct);

    // ══ 死信（P2——spec §7：独立实例 + envelope 溯源 + 回放）══

    /// <summary>死信子队列（独立实例 {name}.dlq——单组；Options.DeadLetter = null 时为 null）。</summary>
    ITierQueue? DeadLetterQueue { get; }

    /// <summary>回放单条死信：解 envelope → 原 payload 入目标队列 → DLQ 确认（spec §7.3）。</summary>
    /// <param name="deadLetterAddress">死信地址（DLQ 投递面获得）。</param>
    /// <param name="target">目标队列（通常是业务主队列）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>目标队列分配的地址。</returns>
    ValueTask<EnqueueResult> ReplayAsync(LogicalAddress deadLetterAddress, ITierQueue target, CancellationToken ct);

    /// <summary>回放全部死信到目标队列（批处理——读空为止），返回回放条数。</summary>
    /// <param name="target">目标队列。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回放条数。</returns>
    ValueTask<int> ReplayAllAsync(ITierQueue target, CancellationToken ct);

    // ══ 延迟管理（P3——spec §4）══

    /// <summary>取消延迟消息（索引删除 + 组 skip 标记——永不投递、截断解锚）。</summary>
    /// <param name="filter">按地址精确 / 按 dueTime 区间。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>取消条数。</returns>
    ValueTask<int> CancelDelayedAsync(CancelDelayedFilter filter, CancellationToken ct);

    /// <summary>排队中延迟消息分页窥视（索引扫，不动 Ring）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>延迟消息视图（dueTime 序）。</returns>
    ValueTask<IReadOnlyList<DelayedInfo>> ListDelayedAsync(CancellationToken ct);

    /// <summary>排队中延迟消息按 dueTime 区间和 Offset/Limit 分页窥视（索引扫，不动 Ring）。</summary>
    /// <param name="query">查询范围与分页参数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>延迟消息视图（dueTime 序）。</returns>
    ValueTask<IReadOnlyList<DelayedInfo>> ListDelayedAsync(DelayedQuery query, CancellationToken ct);

    // ══ 持久化（分配与持久化分离）══

    /// <summary>显式落盘到当前尾（组提交攒批后的同步点）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask FlushAsync(CancellationToken ct);

    /// <summary>等待/推进落盘水位覆盖指定地址（无效地址直接返回）。</summary>
    /// <param name="address">需要覆盖的地址。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask WaitForDurableAsync(LogicalAddress address, CancellationToken ct);

    /// <summary>当前截断头地址。</summary>
    LogicalAddress HeadAddress { get; }

    /// <summary>已分配尾地址（含未落盘）。</summary>
    LogicalAddress TailAddress { get; }

    /// <summary>已落盘水位（= Ring.FlushedUntilAddress）。</summary>
    LogicalAddress DurableTail { get; }

    // ══ 空间回收（截断下界守卫：min 组 Cursor——不截未确认数据）══

    /// <summary>
    /// 截断前缀到 <paramref name="uptoExclusive"/>（开区间）——强制下界 = min(组 Cursor)：
    /// 越界目标被夹取（不截未 ack 数据）。物理段回收失败不阻断逻辑截断。
    /// </summary>
    /// <param name="uptoExclusive">截断目标（夹取到下界）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际生效的截断头地址。</returns>
    ValueTask<LogicalAddress> TruncateAsync(LogicalAddress uptoExclusive, CancellationToken ct);
}
