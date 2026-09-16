using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Queue;

/// <summary>
/// 共识 apply 回收条目（tierqueue-replicated-spec §2 ExpireCmd 载荷面）——辖权节点扫描得出的
/// 确定性结果（重投计数终值 + 是否死信终结），随命令复制、全组 apply 同值。
/// </summary>
/// <param name="Address">消息地址。</param>
/// <param name="RedeliveryCount">重投计数终值（apply 后组的记账值）。</param>
/// <param name="DeadLettered">是否死信终结（skip 标记原位终结——DLQ 写入由辖权节点提案前完成，apply 零 DLQ IO）。</param>
public readonly record struct QueueExpireEntry(LogicalAddress Address, int RedeliveryCount, bool DeadLettered);

/// <summary>
/// TierQueue 复制 apply 端口（tierqueue-replicated-spec §8 定案——Products 显式公开端口，
/// 复制状态机经端口驱动写路径/组游标，避免 InternalsVisibleTo 蔓延）。
/// <para>★ 全部方法 = <b>确定性核心</b>：同一命令序列在任一副本上产生同一状态（地址分配 = apply 序
/// ——Ring 尾分配串行；判定依据全部取自命令载荷或已复制状态，禁止本地时钟/本地水位参与判定）。</para>
/// <para>★ 生命周期：由已就绪的 <see cref="TierQueue"/> 实现（显式接口实现——不出现在公共面）；
///   调用方须保证单线程串行（raft apply 管道单 worker 契约）。</para>
/// </summary>
public interface ITierQueueReplicationPort
{
    // ═══ 写路径（EnqueueCmd apply——幂等判重 + Ring 写 + 延迟/幂等索引登记）═══

    /// <summary>确定性入队：幂等命中返回首次地址（不追加）；否则 Ring 分配地址写入 +
    /// 延迟/幂等索引登记。delayed/由命令载荷携带（提案侧已校验 MaxDelay——apply 不看时钟）。</summary>
    /// <param name="delayed">是否延迟消息（命令已定案）。</param>
    /// <param name="dueTime">延迟时刻（UTC Ticks；非延迟忽略）。</param>
    /// <param name="idempotent">是否幂等生产（pid/seq 槽有效且索引开启）。</param>
    /// <param name="producerId">幂等生产者 ID。</param>
    /// <param name="seq">幂等序号。</param>
    /// <param name="payload">原 payload 字节。</param>
    /// <param name="ct">取消令牌（溢出引擎异步写路径）。</param>
    /// <returns>分配（或命中）的消息地址。</returns>
    ValueTask<LogicalAddress> ApplyEnqueueAsync(bool delayed, long dueTime, bool idempotent, long producerId,
        long seq, ReadOnlyMemory<byte> payload, CancellationToken ct);

    // ═══ 组生命周期（GroupCmd apply——已存在/不存在 = 确定性拒绝抛 InvalidOperationException）═══

    /// <summary>创建消费组（组状态域装配 + 注册表登记 + 截断下界参与）。已存在抛。</summary>
    /// <param name="opt">组配置（创建时定格；RetryBackoff 为节点本地调度面，不随命令复制）。</param>
    /// <param name="ct">取消令牌（组状态域装配 IO）。</param>
    ValueTask ApplyGroupCreateAsync(GroupOptions opt, CancellationToken ct);

    /// <summary>恢复态建组/更新组（快照导入路径——upsert：已存在组（本地恢复自举的 $default）直载
    /// 状态覆写；不存在组按恢复核心同构装配。两路均零 epoch 递增）。</summary>
    /// <param name="name">组名。</param>
    /// <param name="visibilityTimeout">可见性超时。</param>
    /// <param name="maxRedeliveries">重投上限。</param>
    /// <param name="state">导入的组持久状态。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask ApplyGroupRestoreAsync(string name, TimeSpan visibilityTimeout, int maxRedeliveries,
        QueueGroupState.State state, CancellationToken ct);

    /// <summary>延迟索引对账（快照导入后调用——清理已终结条目 + 重建快照窗口缺失的索引项）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask ReconcileDelayIndexAsync(CancellationToken ct);

    /// <summary>Ring 全量落盘（快照导入后调用——导入页进设备面，冷读路径可服务）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask FlushRingAsync(CancellationToken ct);

    /// <summary>删除消费组（状态域销毁 + 注册表移除 + 截断下界抬升）。不存在抛。</summary>
    /// <param name="name">组名。</param>
    ValueTask ApplyGroupDeleteAsync(string name);

    /// <summary>复位组（fence 解除 + 位点按 startAt 重置 + 代次 +1）。不存在抛。</summary>
    /// <param name="name">组名。</param>
    /// <param name="startAt">复位起点形态。</param>
    /// <param name="startAddress">Address 形态起点。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask ApplyGroupResetAsync(string name, GroupStartAt startAt, LogicalAddress startAddress, CancellationToken ct);

    // ═══ 消费推进（AckCmd / ExpireCmd apply——epoch 落后 = 确定性拒绝 StaleDeliveryException）═══

    /// <summary>确认推进（游标/skip 前缀计算 + 数据 flush 先行 + 原子持久 + 截断下界推进）。
    /// <paramref name="epoch"/> 落后当前组代次抛 <see cref="StaleDeliveryException"/>（接管 fencing——spec ③）。</summary>
    /// <param name="name">组名。不存在抛。</param>
    /// <param name="epoch">命令携带的组代次（提案时辖权节点视图）。</param>
    /// <param name="addresses">确认地址列表（缓冲确认已由提案侧并入批）。</param>
    /// <param name="ct">取消令牌（flush/持久 IO）。</param>
    ValueTask ApplyAckAsync(string name, long epoch, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct);

    /// <summary>到期/否认记账 apply：计数置为命令终值 + 死信项 skip 原位终结（DLQ 写入已由提案侧完成）。</summary>
    /// <param name="name">组名。不存在抛。</param>
    /// <param name="entries">回收条目（确定性结果——spec §2 ExpireCmd）。</param>
    /// <param name="ct">取消令牌（持久 IO）。</param>
    ValueTask ApplyExpireAsync(string name, IReadOnlyList<QueueExpireEntry> entries, CancellationToken ct);

    /// <summary>组代次 +1（Claim 抢占/HomeCmd 接管的 fencing 递增面——spec ③/⑤）。不存在抛。</summary>
    /// <param name="name">组名。</param>
    ValueTask ApplyEpochBumpAsync(string name);

    // ═══ 空间回收（RetentionCmd apply）═══

    /// <summary>回收推进：截断下界 = min(组 Cursor) 与 Durable 尾取小（spec ⑥——慢组钉住回收线）。
    /// target 非空时夹取到目标（TruncateAsync 守卫语义继承）。</summary>
    /// <param name="target">截断目标（null = 仅按回收线推进）。</param>
    void ApplyRetention(LogicalAddress? target);

    // ═══ 观测面（状态机校验/快照导出的数据源）═══

    /// <summary>组当前代次（fencing 校验数据源——已复制状态的本地镜像）。不存在抛。</summary>
    /// <param name="name">组名。</param>
    /// <returns>当前组代次。</returns>
    long GetGroupEpoch(string name);

    /// <summary>组游标（连续已确认前缀的下一地址——已复制状态的本地镜像）。不存在抛。</summary>
    /// <param name="name">组名。</param>
    /// <returns>组游标。</returns>
    LogicalAddress GetGroupCursor(string name);

    /// <summary>读 record 原 payload（解 envelope 剥头——死信回放面）。地址无效/不可读抛。</summary>
    /// <param name="address">消息地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>原 payload 字节。</returns>
    ValueTask<byte[]> ReadPayloadAsync(LogicalAddress address, CancellationToken ct);

    /// <summary>已注册组名快照。</summary>
    /// <returns>组名列表。</returns>
    IReadOnlyList<string> GetGroupNames();

    /// <summary>组配置窥视（快照导出面）。不存在 = null。</summary>
    /// <param name="name">组名。</param>
    /// <returns>(可见性超时, 重投上限)；组不存在 = null。</returns>
    (TimeSpan VisibilityTimeout, int MaxRedeliveries)? TryGetGroupConfig(string name);

    /// <summary>组持久状态捕获（快照导出面——gate 内一致切片）。不存在抛。</summary>
    /// <param name="name">组名。</param>
    /// <returns>组持久状态（cursor/skip/retry/epoch/fenced）。</returns>
    QueueGroupState.State GetGroupState(string name);

    /// <summary>Ring 地址界（快照导出面）。</summary>
    /// <returns>(数据头, 已分配尾)。</returns>
    (LogicalAddress Begin, LogicalAddress Tail) GetRingBounds();

    /// <summary>打开 Ring 快照读取器（地址区间像导出——[begin, end) 原始字节）。</summary>
    /// <param name="begin">区间起点。</param>
    /// <param name="end">区间终点（不含）。</param>
    /// <returns>快照读取器。</returns>
    IRingSnapshotReader OpenRingReader(LogicalAddress begin, LogicalAddress end);

    /// <summary>打开 Ring 快照写入器（地址区间像导入——绝对地址回填 + 写尾推进）。</summary>
    /// <param name="begin">区间起点。</param>
    /// <param name="end">区间终点（不含）。</param>
    /// <returns>快照写入器。</returns>
    IRingSnapshotWriter OpenRingWriter(LogicalAddress begin, LogicalAddress end);

    // ═══ 入口侧治理/幂等窥视（提案前置校验——本地最新已应用视图，非复制语义）═══

    /// <summary>积压治理（MaxBacklogBytes——Block/Reject/EvictOldest 本地视图执行；副本场景建议 Reject 语义）。</summary>
    /// <param name="ct">取消令牌（Block 等待响应）。</param>
    ValueTask EnforceBacklogAsync(CancellationToken ct);

    /// <summary>延迟档窗口（Options.Delayed 未开启 = null；开启 = MaxDelay 锚定治理上限）。</summary>
    /// <returns>延迟跨度上限；null = 延迟未开启。</returns>
    TimeSpan? DelayWindow { get; }

    /// <summary>幂等生产是否开启（Options.Idempotency）。</summary>
    /// <returns>true = (ProducerId, Seq) 判重可用。</returns>
    bool IdempotencyEnabled { get; }

    /// <summary>幂等命中窥视（入口快路径——已应用视图判重；未命中仍须提案由 apply 终判）。</summary>
    /// <param name="producerId">幂等生产者 ID。</param>
    /// <param name="seq">幂等序号。</param>
    /// <param name="address">输出：命中地址。</param>
    /// <returns>true = 命中（首次地址）。</returns>
    bool TryFindIdempotent(long producerId, long seq, out LogicalAddress address);

    /// <summary>延迟消息地址解析（CancelDelayed 前置——按过滤条件解析命中地址，本地已应用视图）。</summary>
    /// <param name="filter">取消过滤。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中地址列表（空 = 无命中）。</returns>
    ValueTask<IReadOnlyList<LogicalAddress>> ResolveDelayedAddressesAsync(CancelDelayedFilter filter, CancellationToken ct);

    // ═══ 辖权节点本地消费面（pending 纯内存簿记——不进 raft，spec §2）═══

    /// <summary>辖权出队（扫描可投递 + 登记 pending + 发 token——<b>无 inline 到期回收</b>：
    /// 计数/死信是复制状态，到期处理经 ExpireCmd 提案走共识，禁本地直改）。</summary>
    /// <param name="name">组名。不存在抛。</param>
    /// <param name="maxCount">单批最大条数。</param>
    /// <param name="consumerId">消费端标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>投递列表。</returns>
    ValueTask<IReadOnlyList<QueueDelivery>> DequeueLocalAsync(string name, int maxCount, Guid consumerId, CancellationToken ct);

    /// <summary>pending 在途窥视（辖权到期扫描数据源）。</summary>
    /// <param name="name">组名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>在途项列表。</returns>
    ValueTask<IReadOnlyList<PendingInfo>> PendingLocalAsync(string name, CancellationToken ct);

    /// <summary>摘除 pending（ExpireCmd apply 完成后的辖权本地簿记收尾——回卷投递游标）。</summary>
    /// <param name="name">组名。</param>
    /// <param name="addresses">已终局（重投/死信）的地址列表。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask RemovePendingLocalAsync(string name, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct);
}
