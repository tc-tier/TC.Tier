using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.Ring;
using System.Buffers;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;

namespace TC.Tier.Products.Queue;

/// <summary>
/// 消费组运行时（tc-tier-queue-spec §5）——每组一个独立持久状态域 + 独立投递簿记。
/// <para>★ 持久状态（VersionedMetadata 版本链原子提交）：Cursor/Skip/Epoch/Fenced；
///   投递簿记 pending（纯内存——崩溃丢失 = at-least-once 重放窗口，spec §5.3）。</para>
/// <para>★ fencing（spec §6.2 定案⑤）：组级 GroupEpoch 单调——claim/抢占/复位 +1；
///   迟交（token epoch &lt; 当前）确定性拒绝 StaleDeliveryException。</para>
/// <para>★ 线程模型：组内 Dequeue/Ack/Claim/Nack 经 <c>_gate</c> 串行（多消费者并发安全——
///   pending 登记与位面推进无锁竞态）；组间天然并行。</para>
/// </summary>
internal sealed class QueueGroup : IDisposable, IAsyncDisposable
{
    private readonly VersionedMetadata _meta;
    private readonly int _maxInFlight;
    private readonly int _bufferedAckBatchSize;
    private readonly TimeSpan _bufferedAckCommitInterval;
    private readonly TaskSink? _bufferedAckFlushes;
    private readonly IDisposable? _bufferedAckDeadlineSubscription;
    private readonly SemaphoreSlim _gate = new(1, 1);   // 组内 op 串行（扫描跨 await 不能持锁）

    // === 持久状态镜像（_gate 内变更 + 提交）===
    private LogicalAddress _cursor = LogicalAddress.Invalid;
    private readonly List<LogicalAddress> _skip = [];
    private readonly HashSet<LogicalAddress> _skipSet = [];
    private long _epoch;
    private bool _fenced;

    // === 投递簿记（pending 纯内存——spec §5.3；重试计数持久——spec §5.2 RetryCounts）===
    private readonly Dictionary<LogicalAddress, PendingEntry> _pending = [];
    private readonly Dictionary<LogicalAddress, int> _redelivery = [];
    private readonly HashSet<LogicalAddress> _bufferedAcknowledgements = [];
    private long _bufferedAckDeadline = long.MaxValue;
    private RingOfQueueKey? _ringForBufferedFlush;

    /// <summary>死信目标（DLQ 实例——宿主 TierQueue 装配后接线；null = DLQ 关闭档：
    /// 达上限仅推进组位点 + 宿主告警，spec §3.1/§7.2）。</summary>
    internal TierQueue? DeadLetterTarget { private get; set; }

    /// <summary>延迟就绪索引（宿主装配后接线；null = 纯即时队列——无延迟段投递）。</summary>
    internal DelayedIndex? DelayIndex { private get; set; }

    /// <summary>组配置（创建时定格）。</summary>
    public GroupOptions Config { get; }

    private readonly TimeProvider _clock;         // 时钟供给源（时钟缝 件一——到期扫描/可见性/退避统一）
    private readonly DeadlineRegistry _ackRegistry; // 缓冲确认节拍（与 _clock 同钟）

    /// <summary>组名。</summary>
    public string Name => Config.Name;

    /// <summary>组状态持久域（宿主 TierQueue 在组落账 <c>_groups</c> 后注册到 Resources——
    /// 注册失败路径必须先 Dispose 本组；暴露给宿主做资源登记）。</summary>
    internal VersionedMetadata Meta => _meta;

    /// <summary>
    /// 构造（已 Initialize + WaitForReady 的 meta 由 TierQueue 交付——启动/运行时建组两路同构）。
    /// initial：建组时的初始状态（载入态忽略之）。
    /// </summary>
    /// <param name="meta">已就绪的 VersionedMetadata（组状态持久域）。</param>
    /// <param name="config">组配置（名/起点/可见性/重投上限——创建时定格）。</param>
    /// <param name="maxInFlight">在途窗口上限（Skip + pending 容量）。</param>
    /// <param name="bufferedAckBatchSize">缓冲确认提交阈值（1=禁用缓冲）。</param>
    /// <param name="bufferedAckCommitInterval">缓冲确认最长驻留时间。</param>
    /// <param name="logger">日志（缺省 null）。</param>
    /// <param name="initial">建组初始状态（载入态忽略之——null = 空状态）。</param>
    /// <param name="clock">时钟供给源（时钟缝 件一——到期扫描/可见性/退避；缺省 <see cref="TimeProvider.System"/>）。</param>
    /// <param name="registry">节拍注册表（缺省 System 下用进程级 Shared；假钟下自动新建同钟实例）。</param>
    internal QueueGroup(VersionedMetadata meta, GroupOptions config, int maxInFlight, int bufferedAckBatchSize,
        TimeSpan bufferedAckCommitInterval, ILogger? logger,
        QueueGroupState.State? initial = null,
        TimeProvider? clock = null, DeadlineRegistry? registry = null)
    {
        _meta = meta;
        Config = config;
        _clock = clock ?? TimeProvider.System;
        // ★ 假钟注入档（时钟缝 件一）：注入时钟须配同钟节拍注册表（Shared 是 System 单例）。
        _ackRegistry = registry ?? (_clock == TimeProvider.System
            ? DeadlineRegistry.Shared
            : new DeadlineRegistry(clock: _clock));
        _maxInFlight = maxInFlight;
        _bufferedAckBatchSize = Math.Max(1, bufferedAckBatchSize);
        _bufferedAckCommitInterval = NormalizeBufferedAckInterval(bufferedAckCommitInterval);
        if (_bufferedAckCommitInterval != Timeout.InfiniteTimeSpan && _bufferedAckBatchSize > 1)
        {
            _bufferedAckFlushes = new TaskSink($"queue-ack-flush-{config.Name}", logger: logger);
            _bufferedAckDeadlineSubscription = _ackRegistry.Subscribe(
                () => Interlocked.Read(ref _bufferedAckDeadline),
                OnBufferedAckDeadline);
        }
        if (initial is { } s)
        {
            _cursor = s.Cursor;
            _skip.AddRange(s.Skip);
            _skipSet.UnionWith(s.Skip);
            _epoch = s.Epoch;
            _fenced = s.Fenced;
            foreach (var r in s.Retry) _redelivery[r.Address] = r.Count;
        }
    }

    /// <summary>pending 在途项。NextDeliverableTick = 下次可投时刻（可见性超时 or 退避到期）；
    /// IsBackoff = 当前等待是退避窗口（到期即重投，不再二次退避/计数）。</summary>
    /// <param name="ConsumerId">持有消费者。</param>
    /// <param name="DeliverAtTick">投递时刻（TickCount64——空闲计时基准）。</param>
    /// <param name="TokenEpoch">投递时的组代次（fencing 凭据）。</param>
    /// <param name="NextDeliverableTick">下次可投时刻（可见性超时 or 退避到期）。</param>
    /// <param name="IsBackoff">当前等待是退避窗口（到期即重投，不再二次退避/计数）。</param>
    private readonly record struct PendingEntry(
        Guid ConsumerId, long DeliverAtTick, long TokenEpoch, long NextDeliverableTick, bool IsBackoff = false);

    /// <summary>组连续已确认前缀的下一地址（持久真源的内存镜像）。</summary>
    public LogicalAddress Cursor => _cursor;

    /// <summary>fenced 观测（EvictOldest 淘汰——消费前查）。</summary>
    public bool IsFenced => _fenced;

    /// <summary>组截断下界分量（fenced 组不再钉住数据——spec §8.1）。</summary>
    /// <param name="ringBegin">Ring 数据头地址（Cursor 无效时回退）。</param>
    /// <returns>截断下界地址（fenced = Invalid 不钉住；否则 Cursor 或 ringBegin）。</returns>
    public LogicalAddress Floor(LogicalAddress ringBegin)
        => _fenced ? LogicalAddress.Invalid : (_cursor.IsValid ? _cursor : ringBegin);

    // ═══════════════════════════════════════════════════════════════════
    // 共识 apply 面（tierqueue-replicated-spec——复制状态机经 TierQueue 复制端口驱动；
    // 语义与本地面同源，判定依据全部取自命令载荷——apply 确定性，全组同值）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>当前组代次（fencing 校验数据源——写侧均持 _gate，读侧弱一致够用）。</summary>
    public long CurrentEpoch => Interlocked.Read(ref _epoch);

    /// <summary>辖权出队（扫描 + 登记 pending——无 inline 到期回收：计数/死信是复制状态，
    /// 到期处理经 ExpireCmd 走共识，禁本地直改——tierqueue-replicated-spec §2）。</summary>
    /// <param name="ring">数据 Ring。</param>
    /// <param name="maxCount">单批最大条数。</param>
    /// <param name="consumerId">消费端标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>投递列表。</returns>
    internal async ValueTask<IReadOnlyList<QueueDelivery>> DequeueLocalAsync(
        RingOfQueueKey ring, int maxCount, Guid consumerId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFenced();
            return await ScanDeliverableAsync(ring, maxCount, consumerId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>确认推进（共识 apply 面）：epoch 落后确定性拒绝；其余同本地 AckCore 契约三步。</summary>
    /// <param name="ring">数据 Ring（数据 flush 先行）。</param>
    /// <param name="epoch">命令携带的组代次（提案时辖权节点视图）。</param>
    /// <param name="addresses">确认地址列表（缓冲确认已由提案侧并入批——缓冲簿记不进共识）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>确认后的新 Cursor。</returns>
    /// <exception cref="StaleDeliveryException">epoch 落后当前组代次（接管 fencing——迟交确定性拒绝）。</exception>
    internal async ValueTask<LogicalAddress> ApplyAckFromConsensusAsync(
        RingOfQueueKey ring, long epoch, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (addresses.Count > 0 && epoch < _epoch)
                throw new StaleDeliveryException(addresses[0], epoch, _epoch);
            return await AckCoreAsync(ring, addresses).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>到期/否认记账（共识 apply 面）：计数置命令终值 + 死信项 skip 原位终结
    /// （DLQ 写入已由辖权节点提案前完成——apply 零 DLQ IO，崩溃窗口 = DLQ 重复 at-least-once）。</summary>
    /// <param name="ring">数据 Ring（死信 skip 的前缀计算）。</param>
    /// <param name="entries">回收条目（确定性结果）。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask ApplyExpireFromConsensusAsync(
        RingOfQueueKey ring, IReadOnlyList<QueueExpireEntry> entries, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFenced();
            List<LogicalAddress>? deadLettered = null;
            foreach (var e in entries)
            {
                _redelivery[e.Address] = e.RedeliveryCount;
                if (e.DeadLettered)
                {
                    _pending.Remove(e.Address);   // 死信：摘 pending + 原位终结（skip）
                    (deadLettered ??= []).Add(e.Address);
                }
            }
            if (deadLettered is not null)
                await AckCoreAsync(ring, deadLettered).ConfigureAwait(false);   // skip 标记 + 持久
            else
                PersistGroupState(_cursor, _skip);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>组代次 +1（共识 apply 面——Claim 抢占/HomeCmd 接管的 fencing 递增；epoch 持久防重启回退）。</summary>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask ApplyEpochBumpFromConsensusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _epoch++;
            PersistGroupState(_cursor, _skip);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>快照导入态重建（共识 apply 面——upsert 语义：直接覆写组全部持久状态 +
    /// 原子持久；用于快照导入时已自举组（$default）的状态更新）。</summary>
    /// <param name="state">导入的组持久状态。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask RestoreStateFromConsensusAsync(QueueGroupState.State state, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cursor = state.Cursor;
            _skip.Clear();
            _skipSet.Clear();
            _skip.AddRange(state.Skip);
            _skipSet.UnionWith(state.Skip);
            _epoch = state.Epoch;
            _fenced = state.Fenced;
            _redelivery.Clear();
            foreach (var r in state.Retry) _redelivery[r.Address] = r.Count;
            _pending.Clear();   // 快照 = 已应用前缀的一致切片——pending 不在其中（纯内存簿记归零）
            _readPoint = LogicalAddress.Invalid;
            PersistGroupState(_cursor, _skip);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>摘除 pending（ExpireCmd apply 完成后的辖权本地簿记收尾——回卷投递游标）。</summary>
    /// <param name="addresses">已终局地址列表。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask RemovePendingLocalAsync(IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LogicalAddress earliest = LogicalAddress.Invalid;
            foreach (var a in addresses)
            {
                if (_pending.Remove(a) && (!earliest.IsValid || a < earliest)) earliest = a;
            }
            RewindReadPoint(earliest);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>组持久状态捕获（快照导出面——gate 内一致切片）。</summary>
    /// <returns>组持久状态（cursor/skip/retry/epoch/fenced）。</returns>
    internal QueueGroupState.State CaptureState()
    {
        _gate.Wait();
        try
        {
            List<RetryEntry> retry = [];
            foreach (var (addr, count) in _redelivery)
                if (count > 0) retry.Add(new RetryEntry(addr, count));
            return new QueueGroupState.State(_cursor, [.. _skip], retry, _epoch, _fenced);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 消费（拉取式 peek——登记 pending；可见性到期项先回收再投）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 拉取 ≤ maxCount 条（登记 pending，发 token）；可见性超时项先到期处理
    /// （计数 +1 回投递面；达上限 → 死信路由——spec §7.2）。
    /// </summary>
    /// <param name="ring">数据 Ring（消息 record 定位）。</param>
    /// <param name="maxCount">单批最大条数。</param>
    /// <param name="consumerId">消费端标识（pending 登记 + 空闲计时）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(投递列表, 过期回收列表)——过期项已计数/死信路由完成。</returns>
    internal async ValueTask<(IReadOnlyList<QueueDelivery> Deliveries, IReadOnlyList<LogicalAddress> Expired)>
        DequeueAsync(RingOfQueueKey ring, int maxCount, Guid consumerId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFenced();
            var expired = await ExpireOverdueAsync(ring, ct).ConfigureAwait(false);
            var deliveries = await ScanDeliverableAsync(ring, maxCount, consumerId, ct).ConfigureAwait(false);
            return (deliveries, expired);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>可见性到期处理：超时未 ack 的 pending 摘除 + 计数 +1（spec §5.3/§7.1）；
    /// 计数 &gt; MaxRedeliveries → 死信路由（DLQ envelope + 原位终结——spec §7.2）。
    /// ★ ReadPoint 回卷（仅未死信者——死信者 skip 标记，投递面自然过滤）。</summary>
    /// <param name="ring">数据 Ring（死信路由读载荷）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>过期回收的地址列表（已计数/死信路由完成）。</returns>
    private async ValueTask<IReadOnlyList<LogicalAddress>> ExpireOverdueAsync(
        RingOfQueueKey ring, CancellationToken ct)
    {
        long now = _clock.GetMsTimestamp();
        List<LogicalAddress>? expired = null;
        List<LogicalAddress>? deadLettered = null;
        LogicalAddress earliest = LogicalAddress.Invalid;
        bool stateChanged = false;   // 计数变更（立即重投 or 退避）需持久
        foreach (var (addr, entry) in _pending)
        {
            if (now < entry.NextDeliverableTick) continue;   // 可见性窗口 or 退避未到期

            // 退避到期：直接重投（不二次计数、不再退避——退避是 nack/超时的一次性惩罚）
            if (entry.IsBackoff)
            {
                (expired ??= []).Add(addr);
                if (!earliest.IsValid || addr < earliest) earliest = addr;
                continue;
            }

            // 可见性超时：计数 +1，超限死信，否则退避 or 立即重投
            int newCount = _redelivery.GetValueOrDefault(addr) + 1;
            if (newCount > Config.MaxRedeliveries)
            {
                (expired ??= []).Add(addr);
                (deadLettered ??= []).Add(addr);   // 死信：计数定格 finalCount，路由后原位终结
                _redelivery[addr] = newCount;
                stateChanged = true;
                continue;
            }

            // 退避：保留在 pending，NextDeliverableTick 推到未来（到期由本方法再回收——链式退避）
            if (Config.RetryBackoff is { } backoff)
            {
                var delay = backoff(newCount);
                if (delay > TimeSpan.Zero)
                {
                    _redelivery[addr] = newCount;
                    _pending[addr] = entry with { DeliverAtTick = now, NextDeliverableTick = now + (long)delay.TotalMilliseconds, IsBackoff = true };
                    stateChanged = true;
                    continue;   // 不摘 pending、不回卷 ReadPoint——退避窗口内不可投
                }
            }

            // 立即重投
            (expired ??= []).Add(addr);
            _redelivery[addr] = newCount;
            stateChanged = true;
            if (!earliest.IsValid || addr < earliest) earliest = addr;
        }
        if (expired is not null)
            foreach (var a in expired)
                _pending.Remove(a);
        if (deadLettered is not null)
            await DeadLetterAsync(ring, deadLettered, ct).ConfigureAwait(false);   // 内含状态持久
        else if (stateChanged)
            PersistGroupState(_cursor, _skip);   // 计数变更落盘（DLQ 判据/退避记账持久——spec §5.2）
        RewindReadPoint(earliest);
        return expired is not null ? expired : Array.Empty<LogicalAddress>();   // CS8600：可空强转消除
    }

    /// <summary>投递游标回卷（回队重投的地址集最小值——Nack/到期回收后必调）。</summary>
    /// <param name="addr">回卷目标地址（无效 = no-op）。</param>
    private void RewindReadPoint(LogicalAddress addr)
    {
        if (!addr.IsValid) return;
        if (!_readPoint.IsValid || addr < _readPoint) _readPoint = addr;
    }

    /// <summary>
    /// 扫描可投递面（spec §4.2 双段批契约）：即时段 [ReadPoint, tail) 地址序（跳过延迟 record）→
    /// 就绪延迟段索引 dueTime 序（delay ≤ now）。ReadPoint 只服务即时段；延迟段由索引+pending 驱动。
    /// </summary>
    /// <param name="ring">数据 Ring（即时段扫 + 读载荷）。</param>
    /// <param name="maxCount">单批最大条数。</param>
    /// <param name="consumerId">消费端标识（pending 登记）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>投递列表（≤ maxCount，含 token/重投计数）。</returns>
    private async ValueTask<IReadOnlyList<QueueDelivery>> ScanDeliverableAsync(
        RingOfQueueKey ring, int maxCount, Guid consumerId, CancellationToken ct)
    {
        LogicalAddress tail = ring.TailAddress;
        var deliveries = new List<QueueDelivery>(maxCount);

        // ── 即时段：ReadPoint（进程内投递游标）：floor = Cursor 或数据头；落后则快进 ──
        LogicalAddress floor = _cursor.IsValid ? _cursor : ring.BeginAddress;
        LogicalAddress start = _readPoint.IsValid ? _readPoint : floor;
        if (start < floor) start = floor;
        if (start < tail)
        {
            await using (var cursor = ring.OpenScanCursor(start, tail))
            {
                while (deliveries.Count < maxCount && await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                {
                    LogicalAddress addr = cursor.CurrentAddress;
                    LogicalAddress next = cursor.NextAddress;
                    if (next.IsValid && (!_readPoint.IsValid || next > _readPoint)) _readPoint = next;

                    // Skip 过滤（已确认空洞）/ Pending 过滤（在途——deliver-once 窗口，spec §6.1）
                    if (_skipSet.Contains(addr) || _pending.ContainsKey(addr))
                        continue;

                    // ★ 延迟 record 跳过（key.DueTime ≠ 0——延迟段由索引驱动，不走地址序）
                    if (await ring.TryGetKeyAsync(addr, ct).ConfigureAwait(false) is { Key.DueTime: not 0, Success: true })
                        continue;

                    await DeliverOneAsync(ring, addr, consumerId, deliveries, ct).ConfigureAwait(false);
                }
            }
        }
        if (deliveries.Count >= maxCount) return deliveries;

        // ── 就绪延迟段（索引 dueTime 序——spec 定案⑥双段批）──
        if (DelayIndex is { } delay)
        {
            long now = _clock.GetUtcNow().Ticks;
            foreach (var (_, addr) in delay.ScanReady(now))
            {
                if (deliveries.Count >= maxCount) break;
                if (addr < floor) continue;                      // 已终结前缀（< Cursor）
                if (_skipSet.Contains(addr) || _pending.ContainsKey(addr)) continue;   // 已确认/在途
                await DeliverOneAsync(ring, addr, consumerId, deliveries, ct).ConfigureAwait(false);
            }
        }
        return deliveries;
    }

    /// <summary>读载荷（解 envelope 剥头——投递/死信/抢占共用）+ 登记 pending + 发 token。</summary>
    /// <param name="ring">数据 Ring（读 record 载荷）。</param>
    /// <param name="addr">消息地址（pending 登记 + token 绑定）。</param>
    /// <param name="consumerId">消费端标识。</param>
    /// <param name="deliveries">投递列表（本方法追加到此列表）。</param>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask DeliverOneAsync(RingOfQueueKey ring, LogicalAddress addr, Guid consumerId,
        List<QueueDelivery> deliveries, CancellationToken ct)
    {
        var recordKey = await ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
        byte[] rented = ArrayPool<byte>.Shared.Rent(recordKey.ValueLength);
        byte[] payload;
        try
        {
            int read = recordKey.ValueLength == 0
                ? 0
                : await ring.GetValueAsync(addr, rented.AsMemory(0, recordKey.ValueLength), ct).ConfigureAwait(false);
            if (QueueEnvelope.TryUnwrap(rented.AsSpan(0, read), out _, out _, out _, out _, out var inner))
                payload = inner;
            else
                payload = rented.AsSpan(0, read).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        _pending[addr] = new PendingEntry(consumerId, _clock.GetMsTimestamp(), _epoch,
            _clock.GetMsTimestamp() + (long)Config.VisibilityTimeout.TotalMilliseconds);
        deliveries.Add(new QueueDelivery(addr, payload, new DeliveryToken(_epoch, addr),
            _redelivery.GetValueOrDefault(addr)));
    }

    private LogicalAddress _readPoint = LogicalAddress.Invalid;   // 投递游标（≥ Cursor；不持久——重放语义兜底）

    // ═══════════════════════════════════════════════════════════════════
    // 确认（token 档 = fencing；地址档 = 基线 at-least-once——P0 兼容面）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 确认一批回执（token 档）：epoch 落后 → <see cref="StaleDeliveryException"/>（迟交拒绝）；
    /// 其余推进 Cursor/Skip、摘 pending、持久提交。返回确认后的新 Cursor（供截断下界）。
    /// </summary>
    /// <param name="ring">数据 Ring（ack 数据 flush 先行）。</param>
    /// <param name="receipts">确认回执列表（地址 + 投递令牌）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>确认后的新 Cursor（供截断下界）。</returns>
    /// <exception cref="StaleDeliveryException">回执令牌 epoch 落后于当前组代次。</exception>
    internal async ValueTask<LogicalAddress> AckAsync(
        RingOfQueueKey ring, IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFenced();
            foreach (var r in receipts)
            {
                if (r.Token.Epoch < _epoch)
                    throw new StaleDeliveryException(r.Address, r.Token.Epoch, _epoch);
            }
            return await AckIncludingBufferedAsync(ring, receipts.Select(r => r.Address)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按地址确认（基线档——P0 兼容面：无 token，无 fencing；幂等 no-op 语义同 P0）。</summary>
    /// <param name="ring">数据 Ring（ack 数据 flush 先行）。</param>
    /// <param name="addresses">确认地址列表。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>确认后的新 Cursor（供截断下界）。</returns>
    internal async ValueTask<LogicalAddress> AckByAddressAsync(
        RingOfQueueKey ring, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFenced();
            return await AckIncludingBufferedAsync(ring, addresses).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按地址缓冲确认（基线档——无 token 无 fencing）。</summary>
    /// <param name="ring">数据 Ring。</param>
    /// <param name="addresses">确认地址列表。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 已达阈值立即持久化；false = 缓冲中待后续 flush。</returns>
    internal async ValueTask<bool> AckBufferedByAddressAsync(
        RingOfQueueKey ring, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(addresses);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFenced();
                foreach (var address in addresses)
                    if (address.IsValid) _bufferedAcknowledgements.Add(address);
                if (_bufferedAcknowledgements.Count < _bufferedAckBatchSize)
                {
                    _ringForBufferedFlush = ring;
                    ScheduleBufferedAcknowledgementFlush();
                    return false;
                }
                await FlushBufferedAcknowledgementsCoreAsync(ring).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

    /// <summary>缓冲确认（token 档——fencing 校验后缓冲）。</summary>
    /// <param name="ring">数据 Ring。</param>
    /// <param name="receipts">确认回执列表（地址 + 投递令牌）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 已达阈值立即持久化；false = 缓冲中待后续 flush。</returns>
    /// <exception cref="StaleDeliveryException">回执令牌 epoch 落后。</exception>
    internal async ValueTask<bool> AckBufferedAsync(
        RingOfQueueKey ring, IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(receipts);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFenced();
                foreach (var receipt in receipts)
                {
                    if (receipt.Token.Epoch < _epoch)
                        throw new StaleDeliveryException(receipt.Address, receipt.Token.Epoch, _epoch);
                    if (receipt.Address.IsValid) _bufferedAcknowledgements.Add(receipt.Address);
                }
                if (_bufferedAcknowledgements.Count < _bufferedAckBatchSize)
                {
                    _ringForBufferedFlush = ring;
                    ScheduleBufferedAcknowledgementFlush();
                    return false;
                }
                await FlushBufferedAcknowledgementsCoreAsync(ring).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

    /// <summary>持久化全部缓冲确认（显式 flush）。</summary>
    /// <param name="ring">数据 Ring。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 有缓冲项已持久化；false = 无缓冲项。</returns>
    internal async ValueTask<bool> FlushBufferedAcknowledgementsAsync(
        RingOfQueueKey ring, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFenced();
                if (_bufferedAcknowledgements.Count == 0) return false;
                await FlushBufferedAcknowledgementsCoreAsync(ring).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

    private async ValueTask<LogicalAddress> AckIncludingBufferedAsync(
        RingOfQueueKey ring, IEnumerable<LogicalAddress> addresses)
        {
            var merged = _bufferedAcknowledgements.Count == 0
                ? addresses.ToList()
                : [.. _bufferedAcknowledgements.Concat(addresses)];
            LogicalAddress cursor = await AckCoreAsync(ring, merged).ConfigureAwait(false);
            _bufferedAcknowledgements.Clear();
            Interlocked.Exchange(ref _bufferedAckDeadline, long.MaxValue);
            return cursor;
        }

    private async ValueTask FlushBufferedAcknowledgementsCoreAsync(RingOfQueueKey ring)
        {
            await AckCoreAsync(ring, [.. _bufferedAcknowledgements]).ConfigureAwait(false);
            _bufferedAcknowledgements.Clear();
            Interlocked.Exchange(ref _bufferedAckDeadline, long.MaxValue);
        }

        private void ScheduleBufferedAcknowledgementFlush()
        {
            if (_bufferedAckFlushes is null) return;
            long due = _clock.GetMsTimestamp() + (long)_bufferedAckCommitInterval.TotalMilliseconds;
            Interlocked.CompareExchange(ref _bufferedAckDeadline, due, long.MaxValue);
        }

        private void OnBufferedAckDeadline()
        {
            if (Interlocked.Exchange(ref _bufferedAckDeadline, long.MaxValue) == long.MaxValue) return;
            _bufferedAckFlushes!.SubmitFast(
                (Func<CancellationToken, ValueTask>)FlushBufferedAcknowledgementsAsyncForDeadline);
        }

        private async ValueTask FlushBufferedAcknowledgementsAsyncForDeadline(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_bufferedAcknowledgements.Count != 0)
                    await FlushBufferedAcknowledgementsCoreAsync(_ringForBufferedFlush!).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

    /// <summary>归一化 BufferedAckCommitInterval（Infinite 或正值通过；零/负抛）。</summary>
    /// <param name="interval">原始配置值。</param>
    /// <returns>归一化后的 interval（Infinite 或正值原样）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">interval 为零或负值。</exception>
    private static TimeSpan NormalizeBufferedAckInterval(TimeSpan interval)
    {
        if (interval == Timeout.InfiniteTimeSpan || interval > TimeSpan.Zero) return interval;
        throw new ArgumentOutOfRangeException(nameof(interval), interval,
            "BufferedAckCommitInterval 必须为正值或 Timeout.InfiniteTimeSpan。");
    }

    /// <summary>确认计划（计算段产物——档二/普通路径共用；物化前零 IO 零副作用）。</summary>
    /// <param name="NewCursor">确认后的新 Cursor。</param>
    /// <param name="NewSkip">确认后的新 Skip 表（未成前缀的空洞）。</param>
    /// <param name="FlushTarget">数据 flush 目标（契约①——ack 数据先于位点持久）。</param>
    /// <param name="AckSet">实际确认集（物化时清 pending/计数）。</param>
    internal sealed record AckPlan(
        LogicalAddress NewCursor, List<LogicalAddress> NewSkip,
        LogicalAddress FlushTarget, HashSet<LogicalAddress> AckSet);

    /// <summary>确认核心：计算计划 → 契约三步（数据落盘 → 位点原子提交 → 物化）。持 _gate 调用。</summary>
    /// <param name="ring">数据 Ring（数据 flush 先行）。</param>
    /// <param name="acked">确认地址列表。</param>
    /// <returns>确认后的新 Cursor。</returns>
    private async ValueTask<LogicalAddress> AckCoreAsync(
        RingOfQueueKey ring, IReadOnlyList<LogicalAddress> acked)
    {
        if (acked.Count == 0) return _cursor;

        AckPlan plan = ComputeAck(ring, acked);
        // 契约①：ack 数据先于位点持久；契约②：原子提交；然后物化（内存态采纳 + 清 pending/计数）
        if (plan.FlushTarget.IsValid)
            await ring.FlushUntilAsync(plan.FlushTarget, CancellationToken.None).ConfigureAwait(false);
        PersistGroupState(plan.NewCursor, plan.NewSkip);
        AckMaterialize(plan.NewCursor, plan.NewSkip, plan.AckSet);
        return plan.NewCursor;
    }

    /// <summary>★ 确认计算段（幂等去重/合并/前缀扫描——纯计算零 IO；档二 ComputeAckForRound 复用）。</summary>
    /// <param name="ring">数据 Ring（前缀扫描——Cursor 起地址序确认连续前缀）。</param>
    /// <param name="acked">确认地址列表。</param>
    /// <returns>确认计划（NewCursor/NewSkip/FlushTarget/AckSet）。</returns>
    /// <exception cref="InvalidOperationException">Skip 表超 MaxInFlight（在途窗口背压）。</exception>
    private AckPlan ComputeAck(RingOfQueueKey ring, IReadOnlyList<LogicalAddress> acked)
    {
        var ackSet = new HashSet<LogicalAddress>(acked);
        LogicalAddress cur = _cursor;
        ackSet.RemoveWhere(a => !a.IsValid || (cur.IsValid && a < cur));   // 幂等：已确认（< Cursor）no-op；== Cursor 是下一待确认

        var merged = new SortedSet<LogicalAddress>(_skip);
        merged.UnionWith(ackSet);
        if (merged.Count > _maxInFlight)
            throw new InvalidOperationException(
                $"Skip 表超限 {merged.Count} > MaxInFlight={_maxInFlight}——在途窗口背压（PendingWindowOverflow）");

        // 从当前 Cursor（或数据头）起扫：确认集内连续前缀消化为 Cursor，剩余进 Skip
        LogicalAddress scanStart = _cursor.IsValid ? _cursor : ring.BeginAddress;
        var confirmed = new HashSet<LogicalAddress>(merged);
        LogicalAddress lastConfirmedNext = LogicalAddress.Invalid;
        if (ring.TailAddress > scanStart)
        {
            var scan = ring.OpenScanCursor(scanStart, ring.TailAddress);
            try
            {
                while (confirmed.Count > 0 && scan.MoveNext())
                {
                    if (!confirmed.Remove(scan.CurrentAddress)) break;
                    lastConfirmedNext = scan.NextAddress;
                }
            }
            finally { scan.Dispose(); }
        }
        LogicalAddress newCursor = lastConfirmedNext.IsValid ? lastConfirmedNext : scanStart;
        var newSkip = confirmed.ToList();   // 未成前缀的空洞（升序）
        return new AckPlan(newCursor, newSkip, lastConfirmedNext, ackSet);
    }

    /// <summary>
    /// ★ 档二：回合计入确认（spec §6.3——业务同域事务，真恰好一次）。持 _gate：
    /// fencing 校验 + 计算计划 + 数据 flush 先行（调用方线程）；物化（内存态 + meta staged 写）
    /// 由 <paramref name="stage"/> 挂进 Session 回合——Abort 不执行 = 组位点与业务效果同生共死。
    /// </summary>
    /// <param name="ring">数据 Ring（数据 flush 先行 + 前缀扫描）。</param>
    /// <param name="receipts">确认回执列表（地址 + 投递令牌）。</param>
    /// <param name="stage">物化委托（Session 管线线程执行——Commit 才物化）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="StaleDeliveryException">回执令牌 epoch 落后。</exception>
    internal async ValueTask AckInRoundAsync(RingOfQueueKey ring,
        IReadOnlyList<DeliveryReceipt> receipts, Action<Action> stage, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFenced();
            foreach (var r in receipts)
            {
                if (r.Token.Epoch < _epoch)
                    throw new StaleDeliveryException(r.Address, r.Token.Epoch, _epoch);
            }

            AckPlan plan = ComputeAck(ring, receipts.Select(r => r.Address).ToList());
            // 契约①：ack 数据先于位点持久（管线 Prepare 更晚——保序成立）
            if (plan.FlushTarget.IsValid)
                await ring.FlushUntilAsync(plan.FlushTarget, ct).ConfigureAwait(false);

            // ★ 摘 pending（调用方线程，即刻）+ ReadPoint 回卷：Commit 前窗口 = 未确认在途
            // （单消费者契约下无并发投递）；Abort → 消息可重投（at-least-once 兜底——回卷否则
            // 单调 ReadPoint 漏投，教训④同族）；Commit → 物化推进 cursor/skip 不再投
            LogicalAddress earliest = LogicalAddress.Invalid;
            foreach (var a in plan.AckSet)
            {
                _pending.Remove(a);
                if (!earliest.IsValid || a < earliest) earliest = a;
            }
            foreach (var a in plan.NewSkip)
            {
                _pending.Remove(a);
                if (!earliest.IsValid || a < earliest) earliest = a;
            }
            RewindReadPoint(earliest);

            // 物化委托（Session 管线线程执行）：内存态采纳 + 组状态 meta staged 写（Write 不 Persist——
            // VersionedMetadata.Prepare 在管线 2PC 内落盘；Abort 版本链回退 = 位点不动）
            stage(() =>
            {
                AckMaterialize(plan.NewCursor, plan.NewSkip, plan.AckSet);
                _meta.Write(BuildStateBytes(plan.NewCursor, plan.NewSkip));
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>组状态域参与者（Session 域注册——spec §6.3：业务存储 + 组位点同域 2PC）。</summary>
    internal Contracts.Transactions.ITransactionParticipant Participant => _meta;

    /// <summary>★ 位点物化（普通路径在 Persist 后调；档二回合在 Session 物化委托内调——
    /// Abort 不执行 = 内存态与 meta 内容同生共死，spec §6.3）。</summary>
    /// <param name="newCursor">新 Cursor（连续已确认前缀的下一地址）。</param>
    /// <param name="newSkip">新 Skip 表（未成前缀的空洞）。</param>
    /// <param name="ackSet">实际确认集（清 pending + 计数记账）。</param>
    private void AckMaterialize(LogicalAddress newCursor, List<LogicalAddress> newSkip,
        HashSet<LogicalAddress> ackSet)
    {
        foreach (var a in newSkip) _pending.Remove(a);
        foreach (var a in ackSet)
        {
            _pending.Remove(a);
            _redelivery.Remove(a);   // 已确认/死信终结——计数记账清零（死信 finalCount 已进 envelope）
        }
        _cursor = newCursor;
        _skip.Clear();
        _skipSet.Clear();
        _skip.AddRange(newSkip);
        _skipSet.UnionWith(newSkip);
    }

    /// <summary>显式否认：摘 pending + 计数 +1；达上限 → 死信路由；否则立即重投或退避（spec §3/§7.1/§7.2）。</summary>
    /// <param name="ring">数据 Ring（死信路由读载荷）。</param>
    /// <param name="addresses">否认地址列表。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask NackAsync(RingOfQueueKey ring, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFenced();
            LogicalAddress earliest = LogicalAddress.Invalid;
            List<LogicalAddress>? deadLettered = null;
            foreach (var a in addresses)
            {
                if (!_pending.TryGetValue(a, out var entry)) continue;
                int newCount = _redelivery.GetValueOrDefault(a) + 1;
                _redelivery[a] = newCount;

                if (newCount > Config.MaxRedeliveries)
                {
                    _pending.Remove(a);   // 死信：摘 pending + 原位终结
                    (deadLettered ??= []).Add(a);
                    continue;
                }

                // 退避：保留在 pending，NextDeliverableTick 推到未来（到期由 ExpireOverdueAsync 回收重投）
                if (Config.RetryBackoff is { } backoff)
                {
                    var delay = backoff(newCount);
                    if (delay > TimeSpan.Zero)
                    {
                        long now = _clock.GetMsTimestamp();
                        _pending[a] = entry with { DeliverAtTick = now, NextDeliverableTick = now + (long)delay.TotalMilliseconds, IsBackoff = true };
                        continue;   // 不回卷 ReadPoint——退避窗口内不可投
                    }
                }

                // 立即重投：摘 pending + ReadPoint 回卷
                _pending.Remove(a);
                if (!earliest.IsValid || a < earliest) earliest = a;
            }
            if (deadLettered is not null)
                await DeadLetterAsync(ring, deadLettered, ct).ConfigureAwait(false);   // 内含状态持久
            else
                PersistGroupState(_cursor, _skip);   // 计数变更落盘（重试计数持久——spec §5.2）
            RewindReadPoint(earliest);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 死信路由（spec §7.2——顺序敏感）：① DLQ envelope 写入 + flush 先行（durable——失败 = 留原位抛出，
    /// 丢信不可能）→ ② 原位终结（skip 标记 + 计数定格 + 状态持久）。崩溃窗口 = DLQ 重复
    /// （at-least-once；恰好一次归档二 Session 域，spec §6.3）。DLQ 关闭档 = 跳过 ① 仅 ②（宿主告警）。
    /// </summary>
    /// <param name="ring">数据 Ring（读原消息载荷）。</param>
    /// <param name="addresses">死信地址列表（达 MaxRedeliveries 上限）。</param>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask DeadLetterAsync(
        RingOfQueueKey ring, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
    {
        foreach (var addr in addresses)
        {
            if (DeadLetterTarget is { } dlq)
            {
                var recordKey = await ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
                var buf = new byte[recordKey.ValueLength];
                if (buf.Length > 0)
                    await ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
                // 剥产品 envelope——死信 envelope 内层是原 payload（Replay 还原语义）
                byte[] payload = QueueEnvelope.TryUnwrap(buf, out _, out _, out _, out _, out var inner) ? inner : buf;
                var envelope = DeadLetterEnvelope.Wrap(addr, Name, _redelivery.GetValueOrDefault(addr), payload);
                await dlq.EnqueueAsync(envelope, ct).ConfigureAwait(false);   // ①a 写入
                await dlq.FlushAsync(ct).ConfigureAwait(false);               // ①b durable 先行（死信低频——整刷可接受）
            }
            _redelivery.Remove(addr);   // 计数定格进 envelope，原位记账清零
        }
        await AckCoreAsync(ring, addresses.ToList()).ConfigureAwait(false);   // ② skip 标记 + 持久
    }

    // ═══════════════════════════════════════════════════════════════════
    // Claim 抢占（XCLAIM 同源——spec §5.3）+ fencing
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 抢占 minIdle 超时的 pending：改派调用方 + 新 token；组代次 +1（旧持有者迟交被 fence）。
    /// 返回改派后的投递列表（含新 token——调用方直接可 Ack）。
    /// </summary>
    /// <param name="ring">数据 Ring（读改派消息载荷）。</param>
    /// <param name="filter">抢占过滤（minIdle/上限）。</param>
    /// <param name="consumerId">抢占者消费端标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>改派后的投递列表（新 token——直接可 Ack）。</returns>
    internal async ValueTask<IReadOnlyList<QueueDelivery>> ClaimAsync(
        RingOfQueueKey ring, ClaimFilter filter, Guid consumerId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFenced();
            long now = _clock.GetMsTimestamp();
            long minIdleMs = (long)filter.MinIdle.TotalMilliseconds;

            var claimedAddr = new List<LogicalAddress>();
            foreach (var (addr, entry) in _pending)
            {
                if (claimedAddr.Count >= filter.MaxCount) break;
                if (now - entry.DeliverAtTick < minIdleMs) continue;
                claimedAddr.Add(addr);
            }

            if (claimedAddr.Count == 0) return Array.Empty<QueueDelivery>();

            // ★ fencing：一次 claim 一个代次（定案⑤——组级单调）；先改派登记，再读载荷发新 token
            _epoch++;
            long claimNow = _clock.GetMsTimestamp();
            foreach (var addr in claimedAddr)
                _pending[addr] = new PendingEntry(consumerId, claimNow, _epoch,
                    claimNow + (long)Config.VisibilityTimeout.TotalMilliseconds);
            PersistGroupState(_cursor, _skip);   // epoch 持久化（防重启回退代次）

            var claimed = new List<QueueDelivery>(claimedAddr.Count);
            foreach (var addr in claimedAddr)
            {
                var recordKey = await ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
                var buf = new byte[recordKey.ValueLength];
                if (buf.Length > 0)
                    await ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
                byte[] payload = QueueEnvelope.TryUnwrap(buf, out _, out _, out _, out _, out var inner) ? inner : buf;
                claimed.Add(new QueueDelivery(addr, payload, new DeliveryToken(_epoch, addr),
                    _redelivery.GetValueOrDefault(addr)));
            }
            return claimed;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 治理与复位（EvictOldest fence / Reset——spec §8.2）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>fence（EvictOldest 治理）：置位 + 持久 + pending 清空。调用方持治理路径（队列级）。</summary>
    internal void Fence()
    {
        _fenced = true;
        _pending.Clear();
        PersistGroupState(_cursor, _skip);
    }

    /// <summary>显式复位（矩阵 15——DataLostFencedException 后唯一续命路）：fence 解除 + 位点按 startAt 重置 + 代次 +1。</summary>
    /// <param name="ring">数据 Ring（Latest 形态取 TailAddress）。</param>
    /// <param name="startAt">复位起点形态（Earliest/Latest/Address）。</param>
    /// <param name="startAddress">Address 形态起点（仅 GroupStartAt.Address 用）。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask ResetAsync(RingOfQueueKey ring, GroupStartAt startAt, LogicalAddress startAddress,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _fenced = false;
            _skip.Clear();
            _skipSet.Clear();
            _pending.Clear();
            _readPoint = LogicalAddress.Invalid;
            _cursor = startAt switch
            {
                GroupStartAt.Earliest => LogicalAddress.Invalid,   // 数据头起
                GroupStartAt.Latest => ring.TailAddress,
                GroupStartAt.Address => startAddress,
                _ => LogicalAddress.Invalid,
            };
            _epoch++;   // 组重建 → 代次 +1（spec §6.2）
            PersistGroupState(_cursor, _skip);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>pending 视图（PEL 窥视——诊断面）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>在途项列表（地址/消费者/空闲/重投计数）。</returns>
    internal async ValueTask<IReadOnlyList<PendingInfo>> PendingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = _clock.GetMsTimestamp();
            var list = new List<PendingInfo>(_pending.Count);
            foreach (var (addr, e) in _pending)
                list.Add(new PendingInfo(addr, e.ConsumerId, now - e.DeliverAtTick,
                    _redelivery.GetValueOrDefault(addr), e.IsBackoff));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>组观测（GetStats 产物）。</summary>
    /// <param name="ring">数据 Ring（计算滞后字节数）。</param>
    /// <returns>(在途数, 滞后字节数)。</returns>
    internal (int Pending, long LagBytes) Stats(RingOfQueueKey ring)
    {
        LogicalAddress floor = Floor(ring.BeginAddress);
        long lag = floor.IsValid ? ring.GetByteDistance(floor, ring.TailAddress) : 0;
        return (_pending.Count, lag);
    }

    /// <summary>fence 守卫（消费路径前置——fenced 组拒绝所有 op）。</summary>
    /// <exception cref="DataLostFencedException">组已 fence（EvictOldest 淘汰——需显式 Reset）。</exception>
    private void EnsureNotFenced()
    {
        if (_fenced) throw new DataLostFencedException(Name);
    }

    /// <summary>组状态写盘（Write + Persist——每批 Ack 一次原子提交，定案②）+ 版本链收敛。
    /// 重试计数随位点原子持久（>0 者活跃条目，spec §5.2——超 MaxInFlight 取计数最高者）。</summary>
    /// <param name="cursor">当前 Cursor（连续已确认前缀的下一地址）。</param>
    /// <param name="skip">当前 Skip 表（乱序 ack 空洞）。</param>
    private void PersistGroupState(LogicalAddress cursor, IReadOnlyList<LogicalAddress> skip)
    {
        int size = QueueGroupState.PayloadSize(_maxInFlight);
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var payload = rented.AsSpan(0, size);
            payload.Clear();
            WriteState(payload, cursor, skip);
            _meta.Write(payload);
            _meta.Persist();
            _meta.ReclaimOldVersions();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>构造组状态字节（staged 写与持久写共用——retry 表折叠 + codec）。</summary>
    /// <param name="cursor">当前 Cursor。</param>
    /// <param name="skip">当前 Skip 表。</param>
    /// <returns>组状态字节（PayloadSize(maxInFlight) 定长）。</returns>
    private byte[] BuildStateBytes(LogicalAddress cursor, IReadOnlyList<LogicalAddress> skip)
    {
        var buf = new byte[QueueGroupState.PayloadSize(_maxInFlight)];
        WriteState(buf, cursor, skip);
        return buf;
    }

    /// <summary>将当前持久化状态写入调用方提供的、已清零的固定容量缓冲。</summary>
    /// <param name="destination">目标缓冲（已清零，长度 ≥ PayloadSize(maxInFlight)）。</param>
    /// <param name="cursor">当前 Cursor。</param>
    /// <param name="skip">当前 Skip 表。</param>
    private void WriteState(Span<byte> destination, LogicalAddress cursor, IReadOnlyList<LogicalAddress> skip)
    {
        List<RetryEntry> retry = [];
        if (_redelivery.Count > 0)
        {
            retry = [.. _redelivery.Select(kv => new RetryEntry(kv.Key, kv.Value))
                .OrderByDescending(e => e.Count).ThenBy(e => e.Address)
                .Take(_maxInFlight)];
        }
        QueueGroupState.Write(destination,
            new QueueGroupState.State(cursor, skip, retry, _epoch, _fenced), _maxInFlight);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _bufferedAckDeadlineSubscription?.Dispose();
        _bufferedAckFlushes?.Dispose();
        _meta.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _bufferedAckDeadlineSubscription?.Dispose();
        if (_bufferedAckFlushes is not null) await _bufferedAckFlushes.DisposeAsync().ConfigureAwait(false);
        await _meta.DisposeAsync().ConfigureAwait(false);
    }
}
