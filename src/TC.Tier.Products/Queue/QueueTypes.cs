using TC.Tier.Contracts.Storage;

namespace TC.Tier.Products.Queue;

/// <summary>入队选项（P3——延迟 + 幂等生产档）。</summary>
public sealed record EnqueueOptions
{
    /// <summary>延迟投递时刻（UTC Ticks；null/已过 = 即时）。晚于 MaxDelay 拒绝（锚定治理——spec §4.3）。</summary>
    public long? DueTime { get; init; }

    /// <summary>幂等生产者 ID（spec §6.4 档三——与 Seq 配对判重；需 Options.Idempotency 开启）。</summary>
    public long? ProducerId { get; init; }

    /// <summary>幂等序号（生产者内单调）。</summary>
    public long? Seq { get; init; }
}

/// <summary>延迟取消过滤（CancelDelayedAsync——spec §3：按地址精确 / 按 dueTime 区间批量）。</summary>
/// <param name="Address">精确取消的消息地址（null = 不按地址过滤）。</param>
/// <param name="FromDueInclusive">dueTime 区间下界（含；null = 不限下界）。</param>
/// <param name="ToDueInclusive">dueTime 区间上界（含；null = 不限上界）。</param>
public readonly record struct CancelDelayedFilter(
    LogicalAddress? Address = null,
    long? FromDueInclusive = null,
    long? ToDueInclusive = null)
{
    /// <summary>按地址精确取消。</summary>
    /// <param name="address">待取消消息的地址。</param>
    /// <returns>仅按地址过滤的 CancelDelayedFilter。</returns>
    public static CancelDelayedFilter ByAddress(LogicalAddress address) => new(Address: address);

    /// <summary>按 dueTime 区间批量取消（闭区间）。</summary>
    /// <param name="fromDueInclusive">区间下界（含）。</param>
    /// <param name="toDueInclusive">区间上界（含）。</param>
    /// <returns>按 dueTime 区间过滤的 CancelDelayedFilter。</returns>
    public static CancelDelayedFilter ByRange(long fromDueInclusive, long toDueInclusive)
        => new(FromDueInclusive: fromDueInclusive, ToDueInclusive: toDueInclusive);
}

/// <summary>延迟消息查询（ListDelayedAsync——按 dueTime 区间 + Offset/Limit 分页）。</summary>
/// <param name="FromDueInclusive">dueTime 区间下界（含；null = 不限下界）。</param>
/// <param name="ToDueInclusive">dueTime 区间上界（含；null = 不限上界）。</param>
/// <param name="Offset">分页偏移（从 0 起）。</param>
/// <param name="Limit">返回上限（null = 不限）。</param>
public readonly record struct DelayedQuery(
    long? FromDueInclusive = null,
    long? ToDueInclusive = null,
    int Offset = 0,
    int? Limit = null);

/// <summary>投递令牌（fencing——spec §6.2）：(组代次 GroupEpoch, 消息地址)。
/// claim/抢占/组重建 → epoch 递增 → 旧持有者凭旧 token 的 Ack 被确定性拒绝（StaleDeliveryException）。</summary>
/// <param name="Epoch">组代次（claim/抢占/重建递增——fencing 凭据）。</param>
/// <param name="Address">消息地址（令牌绑定的消息）。</param>
public readonly record struct DeliveryToken(long Epoch, LogicalAddress Address);

/// <summary>确认回执——AckAsync 携带 (地址, 投递令牌)，令牌落后 = 迟交拒绝。</summary>
/// <param name="Address">已确认消费的消息地址。</param>
/// <param name="Token">投递令牌（令牌落后 = 迟交拒绝）。</param>
public readonly record struct DeliveryReceipt(LogicalAddress Address, DeliveryToken Token);

/// <summary>投递出的消息——(地址, payload, 令牌, 重投计数)。</summary>
/// <param name="Address">消息地址（Ring 定位凭据）。</param>
/// <param name="Payload">消息负载字节。</param>
/// <param name="Token">投递令牌（Ack 时回传——令牌落后 = 迟交拒绝）。</param>
/// <param name="RedeliveryCount">重投计数（达上限死信——P2 生效）。</param>
public sealed record QueueDelivery(
    LogicalAddress Address, byte[] Payload, DeliveryToken Token, int RedeliveryCount);

/// <summary>pending 在途表窥视项（PEL 视图——诊断/死信排查）。</summary>
/// <param name="Address">在途消息地址。</param>
/// <param name="ConsumerId">当前持有消费者。</param>
/// <param name="IdleMs">空闲毫秒（距上次投递——超时回队重投判据）。</param>
/// <param name="RedeliveryCount">已重投次数。</param>
/// <param name="IsBackoff">当前等待是退避窗口（到期重投不再计数——辖权到期扫描的记账判据）。</param>
public readonly record struct PendingInfo(
    LogicalAddress Address, Guid ConsumerId, long IdleMs, int RedeliveryCount, bool IsBackoff = false);

/// <summary>组创建位点（spec §5.1）。</summary>
public enum GroupStartAt
{
    /// <summary>最早——从当前截断头（HeadAddress）起。</summary>
    Earliest,
    /// <summary>最新——从当前尾（TailAddress）起（跳过全部存量）。</summary>
    Latest,
    /// <summary>指定地址。</summary>
    Address,
}

/// <summary>组创建/复位配置（组配置创建时定格——spec §5.2 GroupConfig）。</summary>
public sealed record GroupOptions
{
    /// <summary>组名（非空、不含路径分隔符）。</summary>
    public required string Name { get; init; }

    /// <summary>创建位点（Address 形态时 <see cref="StartAddress"/> 生效）。</summary>
    public GroupStartAt StartAt { get; init; } = GroupStartAt.Earliest;

    /// <summary>Address 形态的起点（仅 <see cref="GroupStartAt.Address"/> 用）。</summary>
    public LogicalAddress StartAddress { get; init; }

    /// <summary>可见性超时（在途未 ack 的重投等待——超时回队计数 +1；spec §5.3）。默认 60s。</summary>
    public TimeSpan VisibilityTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>重投上限（达上限死信——P2 生效；P1 仅计数透出）。默认 16。</summary>
    public int MaxRedeliveries { get; init; } = 16;

    /// <summary>
    /// 重试退避策略（spec §7.1——默认立即重投）。
    /// <para>输入 = 当前重投计数（1 起，首次 nack/超时后 count=1）；输出 = 下次可投延迟。
    ///   null 或 <see cref="TimeSpan.Zero"/> = 立即重投（回队 ReadPoint 回卷）。</para>
    /// <para>★ 实现复用 pending 可见性窗口（DeliverAtTick 推到未来 = 下次可投时刻），
    ///   到期由 ExpireOverdueAsync 回收重投——零新结构（同 spec "redelivery 退避 = 短延迟消息，零新机制" 精神）。
    ///   崩溃丢失 pending = 退避失效、立即重投（at-least-once 兜底，可接受）。</para>
    /// <para>示例：指数退避 <c>n => TimeSpan.FromSeconds(Math.Pow(2, n))</c>；
    ///   固定退避 <c>_ => TimeSpan.FromSeconds(5)</c>。</para>
    /// </summary>
    public Func<int, TimeSpan>? RetryBackoff { get; init; }
}

/// <summary>Claim 抢占过滤（XCLAIM 同源——spec §3）：minIdle 超时的 pending 改派调用方。</summary>
/// <param name="MinIdle">最小空闲时长（pending 空闲超此才改派）。</param>
/// <param name="MaxCount">改派上限（防无界；默认 int.MaxValue）。</param>
public readonly record struct ClaimFilter(TimeSpan MinIdle, int MaxCount = int.MaxValue);

/// <summary>积压治理模式（spec §8.2 定案①：默认 Block 无损优先）。</summary>
public enum BacklogPolicy
{
    /// <summary>阻塞生产者至回收跟上（有界等待 <see cref="TierQueueOptions.BacklogBlockTimeout"/>，
    /// 超时抛 QueueFullException）——无损。</summary>
    Block,
    /// <summary>立即拒绝（抛 QueueFullException）——无损，生产方自决。</summary>
    Reject,
    /// <summary>淘汰最老——最慢组被 fence（DataLostFencedException，显式 Reset 才能续）——破坏显式化不静默。</summary>
    EvictOldest,
}

/// <summary>单组观测（GetStatsAsync 产物）。</summary>
/// <param name="Name">组名。</param>
/// <param name="Cursor">消费游标（下次 Read 起点）。</param>
/// <param name="Pending">在途消息数（未 ack）。</param>
/// <param name="Fenced">是否被 fence（淘汰/数据丢失）。</param>
/// <param name="LagBytes">滞后字节数（消费游标与尾的差距）。</param>
/// <param name="BacklogFloorWeight">积压治理地板权重（EvictOldest 选最慢组用）。</param>
public sealed record GroupStats(
    string Name, LogicalAddress Cursor, int Pending, bool Fenced, long LagBytes, int BacklogFloorWeight);

/// <summary>队列观测（GetStatsAsync 产物——spec §8.3 水位指标面）。</summary>
/// <param name="Head">队列头地址（截断边界）。</param>
/// <param name="Tail">队列尾地址（追加边界）。</param>
/// <param name="DurableTail">已持久化尾地址（fsync 边界）。</param>
/// <param name="BacklogBytes">积压字节数（Tail − Head）。</param>
/// <param name="Groups">各消费组观测列表。</param>
public sealed record QueueStats(
    LogicalAddress Head, LogicalAddress Tail, LogicalAddress DurableTail,
    long BacklogBytes, IReadOnlyList<GroupStats> Groups);

/// <summary>迟交拒绝（fencing——spec §6.2）：投递令牌的组代次落后于当前（已被抢占/重建）。</summary>
/// <param name="address">迟交消息地址。</param>
/// <param name="tokenEpoch">投递令牌的组代次（落后值）。</param>
/// <param name="currentEpoch">当前组代次（已被抢占/重建递增）。</param>
public sealed class StaleDeliveryException(LogicalAddress address, long tokenEpoch, long currentEpoch)
    : InvalidOperationException(
        $"Stale delivery {address}: token epoch {tokenEpoch} < group epoch {currentEpoch}——已被抢占/重建，迟交确定性拒绝");

/// <summary>队列积压满（治理——spec §8.2）：Reject 即抛 / Block 有界等待超时即抛。</summary>
/// <param name="backlogBytes">当前积压字节数。</param>
/// <param name="limitBytes">积压上限（配置阈值）。</param>
public sealed class QueueFullException(long backlogBytes, long limitBytes)
    : InvalidOperationException(
        $"Queue backlog {backlogBytes} bytes exceeds limit {limitBytes}——治理策略生效（Reject/Block 超时）");

/// <summary>组被淘汰 fence（EvictOldest——spec §8.2）：数据已被治理回收，显式 ResetGroupAsync 才能续。</summary>
/// <param name="groupName">被淘汰 fence 的组名。</param>
public sealed class DataLostFencedException(string groupName)
    : InvalidOperationException(
        $"Group '{groupName}' fenced by EvictOldest governance——未确认数据已被回收；ResetGroupAsync(显式复位) 后才能继续消费");
