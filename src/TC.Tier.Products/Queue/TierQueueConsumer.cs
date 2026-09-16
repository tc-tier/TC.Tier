using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Transactions;

namespace TC.Tier.Products.Queue;

/// <summary>
/// 消费组句柄（tc-tier-queue-spec §3 ITierQueueConsumer）——一个组一个消费端实例
/// （多消费者 = 多句柄同组；ClaimAsync 抢占改派）。组本体归 TierQueue 宿主，句柄 Dispose 轻量。
/// </summary>
public sealed class TierQueueConsumer : ITierQueueConsumer
{
    private readonly QueueGroup _group;
    private readonly RingOfQueueKey _ring;
    private readonly Guid _consumerId = Guid.NewGuid();

    /// <summary>构造（internal——外部经 ITierQueue.CreateConsumerAsync/CreateGroupAsync）。</summary>
    /// <param name="group">所属消费组（组本体归 TierQueue 宿主——句柄轻量引用）。</param>
    /// <param name="ring">数据 Ring（消息 record 定位）。</param>
    internal TierQueueConsumer(QueueGroup group, RingOfQueueKey ring)
    {
        _group = group;
        _ring = ring;
    }

    /// <inheritdoc/>
    public string Group => _group.Name;

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<QueueDelivery>> DequeueAsync(int maxCount, CancellationToken ct)
    {
        var (deliveries, _) = await _group.DequeueAsync(_ring, maxCount, _consumerId, ct).ConfigureAwait(false);
        return deliveries;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<QueueDelivery>> ClaimAsync(ClaimFilter filter, CancellationToken ct)
        => _group.ClaimAsync(_ring, filter, _consumerId, ct);

    /// <inheritdoc/>
    public ValueTask AckAsync(IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct)
        => AwaitNothing(_group.AckAsync(_ring, receipts, ct));

    /// <summary>缓冲确认；返回不代表 durable，达到配置阈值或显式 flush 后持久化。</summary>
    /// <param name="receipts">确认回执列表。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>确认已写入本消费组缓冲（非 durable）后完成的 <see cref="ValueTask"/>。</returns>
    public async ValueTask AckBufferedAsync(IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (await _group.AckBufferedAsync(_ring, receipts, ct).ConfigureAwait(false))
            return;
    }

    /// <summary>持久化本消费组的全部缓冲确认。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>全部缓冲确认落盘（durable）后完成的 <see cref="ValueTask"/>。</returns>
    public async ValueTask FlushAcknowledgementsAsync(CancellationToken ct)
    {
        await _group.FlushBufferedAcknowledgementsAsync(_ring, ct).ConfigureAwait(false);
    }

    /// <summary>内联 await 适配器（AckAsync 委托返回 ValueTask&lt;LogicalAddress&gt; → 转 ValueTask）。</summary>
    /// <param name="t">组 AckAsync 返回的位点推进任务。</param>
    private static async ValueTask AwaitNothing(ValueTask<LogicalAddress> t)
    {
        await t.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask AckInRoundAsync(TierSession session, IReadOnlyList<DeliveryReceipt> receipts,
        CancellationToken ct)
        => _group.AckInRoundAsync(_ring, receipts, a => session.Stage(a), ct);

    /// <inheritdoc/>
    public ValueTask NackAsync(IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
        => _group.NackAsync(_ring, addresses, ct);

    /// <inheritdoc/>
    public LogicalAddress GroupCursor => _group.Cursor;

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<PendingInfo>> PendingAsync(CancellationToken ct)
        => _group.PendingAsync(ct);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;   // 组归队列宿主——句柄无重资源
}

/// <summary>消费组句柄公开面（spec §3——token 档消费：fencing/deliver-once）。</summary>
public interface ITierQueueConsumer : IAsyncDisposable
{
    /// <summary>组名。</summary>
    string Group { get; }

    /// <summary>拉取（登记 pending——在途表；可见性超时项先到期回收再投）。</summary>
    /// <param name="maxCount">单批最大条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>投递列表（含 token/重投计数）。</returns>
    ValueTask<IReadOnlyList<QueueDelivery>> DequeueAsync(int maxCount, CancellationToken ct);

    /// <summary>超时抢占（XCLAIM 同源）：minIdle 超时的 pending 改派本消费者 + 组代次 +1（旧持有者迟交被 fence）。</summary>
    /// <param name="filter">抢占过滤（minIdle/上限）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>改派后的投递列表（新 token——直接可 Ack）。</returns>
    ValueTask<IReadOnlyList<QueueDelivery>> ClaimAsync(ClaimFilter filter, CancellationToken ct);

    /// <summary>确认（token 档）：令牌落后 → <see cref="StaleDeliveryException"/>（迟交确定性拒绝）。</summary>
    /// <param name="receipts">确认回执（地址 + 投递令牌）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask AckAsync(IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct);

    /// <summary>缓冲确认；返回不代表 durable，达到配置阈值或显式 flush 后持久化。</summary>
    ValueTask AckBufferedAsync(IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct);

    /// <summary>持久化本消费组的全部缓冲确认。</summary>
    ValueTask FlushAcknowledgementsAsync(CancellationToken ct);

    /// <summary>
    /// ★ 档二：确认挂进 Session 回合（spec §6.3——业务同域事务，真恰好一次）：
    /// 物化（组位点 staged 写 + 业务效果）由 <see cref="TierSession.CommitAsync"/> 的 2PC 原子驱动
    /// （Prepare-all 落盘 → Confirm-all；Abort = 组位点与业务效果同回退，消息重投）。
    /// </summary>
    /// <param name="session">域会话（组参与者经 <c>ITierQueue.GetGroupParticipant</c> 注册进域）。</param>
    /// <param name="receipts">确认回执。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask AckInRoundAsync(TierSession session, IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct);

    /// <summary>显式否认：立即摘 pending 回投递面 + 计数 +1。</summary>
    /// <param name="addresses">否认地址列表。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask NackAsync(IReadOnlyList<LogicalAddress> addresses, CancellationToken ct);

    /// <summary>组连续已确认前缀的下一地址（持久真源）。</summary>
    LogicalAddress GroupCursor { get; }

    /// <summary>pending 在途表窥视（PEL——诊断/死信排查）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>在途项列表。</returns>
    ValueTask<IReadOnlyList<PendingInfo>> PendingAsync(CancellationToken ct);
}
