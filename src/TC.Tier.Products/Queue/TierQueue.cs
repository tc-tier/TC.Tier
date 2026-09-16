using System.Buffers;
using System.Collections.Concurrent;
using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Lifecycle;
using TC.Tier.Core.Resources;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Queue;

/// <summary>
/// TierQueue——本地持久队列（tc-tier-queue-spec：P0 核心流 + P1 消费组全量）。
/// <para>★ 组合配方（Ring 骨架）：RingOfQueueKey（数据真相源）+ 组注册表 VersionedMetadata
///   （{name}.groups——组名 + 定格配置）+ 每组一个 VersionedMetadata（{name}.group.{g}——位点/代次
///   原子提交域）。组 = 独立持久位点 + 独立投递簿记（pending 纯内存）。</para>
/// <para>★ 治理（spec §8.2 定案①）：Enqueue 前积压检查（MaxBacklogBytes）——Block（默认，有界等待）/
///   Reject / EvictOldest（fence 最慢组，显式 ResetGroupAsync 续命）。</para>
/// <para>★ 队列级 P0 兼容面（DequeueAsync/AckAsync/GroupCursor）委托 "$default" 组——地址档基线语义
///   （无 token 无 fencing）；token 档（fencing/deliver-once）经 <see cref="CreateConsumerAsync"/>。</para>
/// <para>★ 线程模型：多生产者并发（Ring tail 串行）；组内 Dequeue/Ack/Claim 经组 _gate 串行；组间并行。</para>
/// <para>★ 复制面（tierqueue-replicated-spec §8）：显式实现 <see cref="ITierQueueReplicationPort"/>
///   （不出现在公共面——复制状态机经端口驱动确定性核心；端口方法串行调用契约，apply 管道单 worker 满足）。</para>
/// </summary>
public sealed class TierQueue : LifecycleBase<TierQueueRecoveryHints>, ITierQueue, ITierQueueReplicationPort
{
    /// <summary>P0 单内建组名。</summary>
    public const string DefaultGroupName = "$default";

    private readonly RingOfQueueKey _ring;
    private readonly VersionedMetadata _registry;
    private readonly TierQueueGroupMetaFactory? _groupMetaFactory;
    private readonly TierQueueStorageOptionsFactory? _storageOptionsFactory;
    private readonly TierQueueOptions _options;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, QueueGroup> _groups = [];
    private readonly Guid _defaultConsumerId = Guid.NewGuid();   // 队列级 P0 面的隐式消费者
    private readonly IFileSystem _fs;   // 组状态域运行时装配/目录回收用（Builder 交付）
    private readonly TierQueue? _dlq;   // 死信子队列（独立实例——定案③；null = 关闭档）
    private readonly DelayedIndex? _delayIndex;   // 延迟就绪索引（null = 纯即时队列）
    private readonly HashOfQueueKey? _idempotencyIndex;   // 幂等生产索引（null = 档三关闭）
    private readonly TimeSpan _maxDelay;   // 锚定治理上限（spec §4.3）
    private readonly TimeProvider _clock;  // 时钟供给源（时钟缝 件一——DueTime 墙钟/背压单调窗）

    // === 积压治理（Block 等待信号——ack/delete/reset/evict 后脉冲）===
    private TaskCompletionSource _backlogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>构造 internal——外部只能经 <see cref="TierQueueBuilder.StartAsync"/>。</summary>
    /// <param name="ring">数据 Ring（消息 record 真相源）。</param>
    /// <param name="registry">组注册表 VersionedMetadata（组名 + 定格配置）。</param>
    /// <param name="fs">文件系统抽象（组状态域运行时装配/目录回收用）。</param>
    /// <param name="dlq">死信子队列实例（null = 关闭档）。</param>
    /// <param name="delayIndex">延迟就绪索引（null = 纯即时队列）。</param>
    /// <param name="idempotencyIndex">幂等生产索引（null = 档三关闭）。</param>
    /// <param name="groupMetaFactory">组状态 meta 装配工厂（null = 默认装配）。</param>
    /// <param name="storageOptionsFactory">引擎选项变换器（null = 原样 defaults）。</param>
    /// <param name="options">TierQueue 选项（队列名/容量/治理/索引开关）。</param>
    /// <param name="logger">日志（缺省 null）。</param>
    internal TierQueue(RingOfQueueKey ring, VersionedMetadata registry, IFileSystem fs,
        TierQueue? dlq, DelayedIndex? delayIndex, HashOfQueueKey? idempotencyIndex,
        TierQueueGroupMetaFactory? groupMetaFactory, TierQueueStorageOptionsFactory? storageOptionsFactory,
        TierQueueOptions options, ILogger? logger)
        : base(recovery: null, logger)
    {
        _ring = ring;
        _registry = registry;
        _fs = fs;
        _dlq = dlq;
        _delayIndex = delayIndex;
        _idempotencyIndex = idempotencyIndex;
        _maxDelay = options.Delayed is { } d ? d.MaxDelay : TimeSpan.Zero;
        _clock = options.Clock;   // 时钟供给源（时钟缝 件一）
        _groupMetaFactory = groupMetaFactory;
        _storageOptionsFactory = storageOptionsFactory;
        _options = options;
        _logger = logger;
        Resources.Add(ring, ownership: ResourceOwnership.Owned);
        Resources.Add(_registry, ownership: ResourceOwnership.Owned);
        if (_dlq is not null)
            Resources.Add(_dlq, ownership: ResourceOwnership.Owned);   // 随主队列统一析构
        if (_delayIndex is not null)
            Resources.Add(_delayIndex, ownership: ResourceOwnership.Owned);
        if (_idempotencyIndex is not null)
            Resources.Add(_idempotencyIndex, ownership: ResourceOwnership.Owned);
    }

    /// <summary>死信子队列（spec §7.3——独立实例单组；Options.DeadLetter = null 时为 null）。</summary>
    public ITierQueue? DeadLetterQueue => _dlq;

    // ═══════════════════════════════════════════════════════════════════
    // 延迟管理（P3——spec §4：取消/窥视）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<int> CancelDelayedAsync(CancelDelayedFilter filter, CancellationToken ct)
    {
        EnsureReady();
        if (_delayIndex is not { } delay)
            throw new InvalidOperationException("延迟管理需要 Options.Delayed 开启");

        List<LogicalAddress> removed;
        if (filter.Address is { } addr)
        {
            removed = delay.Remove(_ring, addr) ? [addr] : [];
        }
        else if (filter.FromDueInclusive is { } from && filter.ToDueInclusive is { } to)
        {
            removed = delay.RemoveRange(from, to);
        }
        else
        {
            throw new ArgumentException("取消过滤非法：需 Address 或 FromDue/ToDue 区间");
        }

        if (removed.Count > 0)
        {
            // ★ 取消 = 索引删除 + 组 skip 标记（永不投递、组状态越过——spec §4.2）
            foreach (var g in _groups.Values)
                await g.AckByAddressAsync(_ring, removed, ct).ConfigureAwait(false);
            TryTruncateAll();   // 绊脚解除（被取消消息可能已是最小存活地址）
        }
        return removed.Count;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<DelayedInfo>> ListDelayedAsync(CancellationToken ct)
        => ListDelayedAsync(default, ct);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<DelayedInfo>> ListDelayedAsync(DelayedQuery query, CancellationToken ct)
    {
        EnsureReady();
        if (_delayIndex is not { } delay)
            throw new InvalidOperationException("延迟管理需要 Options.Delayed 开启");
        IReadOnlyList<DelayedInfo> list = [.. delay.List(query)];
        return ValueTask.FromResult(list);
    }

    /// <summary>内部诊断（测试/白盒）——延迟索引观测。</summary>
    internal long DiagnosticDelayedCount => _delayIndex?.Count ?? 0;

    /// <summary>内部诊断（测试/白盒）——延迟索引上次恢复是否走锚点帧载入（false=全量重放）。</summary>
    internal bool DiagnosticDelayedMainStorageApplied => _delayIndex?.MainStorageAppliedLastRecovery ?? false;

    /// <summary>★ Initialize 第一阶段钩子：启动 Ring + 注册表（并行后台）——组装配在恢复核心（依赖注册表清单）。</summary>
    protected override void OnInitializeBegin()
    {
        _ring.Initialize();
        _registry.Initialize();
    }

    /// <summary>★ 默认 Recovery 单一创建点——恢复核心 = 注册表载入 + 组装配 + 恢复自动截断（spec §9）。</summary>
    /// <returns>恢复算法实例（由基类持有并在 Initialize 中执行）。</returns>
    protected override IRecovery<TierQueueRecoveryHints> CreateRecovery() => new TierQueueRecovery(this);

    // ═══════════════════════════════════════════════════════════════════
    // 生产（治理三模式——spec §8.2）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> EnqueueAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => await EnqueueAsync(new EnqueueOptions(), payload, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public ValueTask<EnqueueResult> EnqueueAsync(EnqueueOptions opt, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => EnqueueCoreAsync(opt, payload, ct);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<EnqueueResult>> EnqueueBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
        => await EnqueueBatchAsync(new EnqueueOptions(), payloads, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<EnqueueResult>> EnqueueBatchAsync(
        EnqueueOptions opt, IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(payloads);
        if (CanUseRingBatch(opt))
        {
            await EnforceBacklogAsync(ct).ConfigureAwait(false);
            return EnqueueImmediateBatch(payloads, ct);
        }

        var results = new EnqueueResult[payloads.Count];
        for (int i = 0; i < payloads.Count; i++)
        {
            var itemOpt = opt.Seq.HasValue
                ? opt with { Seq = checked(opt.Seq.Value + i) }
                : opt;
            results[i] = await EnqueueCoreAsync(itemOpt, payloads[i], ct).ConfigureAwait(false);
        }
        return results;
    }

    /// <summary>
    /// Ring 的独占写窗口将 tail 锁从每条消息一次降为每页一次。延迟、幂等、溢出与积压治理各自
    /// 需要逐记录异步协调，因此明确回落到完整的逐条路径，不能混用窗口写入。
    /// </summary>
    /// <param name="opt">入队选项（含延迟/幂等/溢出字段时回落逐条）。</param>
    /// <returns>true = 可批量窗口写；false = 须逐条路径。</returns>
    private bool CanUseRingBatch(EnqueueOptions opt)
        => _options.MaxBacklogBytes is null
            && _options.OverflowPolicy != OverflowPolicy.Enabled
            && opt.DueTime is null
            && opt.ProducerId is null
            && opt.Seq is null;

    private EnqueueResult[] EnqueueImmediateBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
    {
        var results = new EnqueueResult[payloads.Count];
        using var batch = _ring.BeginWriteBatch();
        Span<byte> header = stackalloc byte[QueueEnvelope.HeaderSize];
        QueueEnvelope.WriteHeader(header, false, false, 0, 0, 0);
        for (int i = 0; i < payloads.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            results[i] = new EnqueueResult(batch.Append(default, header, payloads[i].Span));
        }
        return results;
    }

    /// <summary>入队核心（P3 全路径：即时 / 延迟（索引插入）/ 幂等（判重短路））。</summary>
    /// <param name="opt">入队选项（DueTime/ProducerId/Seq——延迟/幂等档）。</param>
    /// <param name="payload">消息字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分配的消息地址（幂等命中 = 首次地址）。</returns>
    /// <exception cref="InvalidOperationException">延迟未开启 / 跨度超 MaxDelay / 幂等未开启但带 pid/seq。</exception>
    /// <exception cref="QueueFullException">积压治理 Reject / Block 超时。</exception>
    private async ValueTask<EnqueueResult> EnqueueCoreAsync(
        EnqueueOptions opt, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await EnforceBacklogAsync(ct).ConfigureAwait(false);

        // ── 档三幂等短路：同 (pid, seq) 已入队 → 返回原地址不追加（spec §6.4）──
        QueueKey idemKey = default;
        long? pid = opt.ProducerId;
        long? seq = opt.Seq;
        bool idempotent = pid.HasValue && seq.HasValue && _idempotencyIndex is not null;
        if (idempotent)
        {
            idemKey = new QueueKey(pid ?? 0, seq ?? 0);   // CS8629：HasValue 已验——模式捕获等价（0 分支不可达）
            LogicalAddress existing = _idempotencyIndex!.Find(idemKey);   // CS8602：is not null 已验
            if (existing != LogicalAddress.Empty)
                return new EnqueueResult(existing);
        }

        // ── 延迟解析（锚定治理：DueTime-now 超 MaxDelay 拒绝——spec §4.3）──
        bool delayed = false;
        long due = 0;
        long now = _clock.GetUtcNow().Ticks;
        if (opt.DueTime is { } dt && dt > now)
        {
            if (_delayIndex is null)
                throw new InvalidOperationException("延迟入队需要 Options.Delayed 开启（就绪索引装配前提）");
            if (dt - now > _maxDelay.Ticks)
                throw new InvalidOperationException(
                    $"延迟跨度超 MaxDelay（{_maxDelay}——未就绪消息钉住截断的锚定治理，spec §4.3）；超长延迟用独立队列实例隔离");
            delayed = true;
            due = dt;
        }

        // ── 幂等校验（档三开启才允许带 pid/seq——防 envelope 槽语义漂移）──
        if (!idempotent && (pid.HasValue || seq.HasValue))
            throw new InvalidOperationException("幂等生产需要 Options.Idempotency 开启（(ProducerId, Seq) 判重）");

        // ── 写 Ring（record key 二态：(due,0) 延迟标记 / (0,0) 即时；payload = envelope 化——spec §2）──
        var key = delayed ? new QueueKey(due, 0) : default(QueueKey);
        LogicalAddress addr = _options.OverflowPolicy == OverflowPolicy.Enabled
            && checked(QueueEnvelope.HeaderSize + payload.Length) > _options.MinOverflowSize
            ? await WriteOverflowEnvelopeAsync(key, delayed, idempotent, pid ?? 0, seq ?? 0, due, payload, ct)
                .ConfigureAwait(false)
            : WriteInlineEnvelope(key, delayed, idempotent, pid ?? 0, seq ?? 0, due, payload.Span);

        // ── 延迟：索引插入（写后即知 addr——索引键 (due, addr)）──
        if (delayed)
            _delayIndex!.Insert(due, addr);

        // ── 档三：幂等映射登记（同 key 覆写语义——Insert 覆盖 value 不增计数）──
        if (idempotent)
            _idempotencyIndex!.Insert(idemKey, addr, _idempotencyIndex.BeginAddress);

        return new EnqueueResult(addr);
    }

    private LogicalAddress WriteInlineEnvelope(QueueKey key, bool delayed, bool idempotent,
        long producerId, long seq, long dueTime, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[QueueEnvelope.HeaderSize];
        QueueEnvelope.WriteHeader(header, delayed, idempotent, producerId, seq, dueTime);
        return _ring.Write(key, header, payload);
    }

    private async ValueTask<LogicalAddress> WriteOverflowEnvelopeAsync(QueueKey key, bool delayed, bool idempotent,
        long producerId, long seq, long dueTime, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        int length = checked(QueueEnvelope.HeaderSize + payload.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            QueueEnvelope.WriteHeader(rented, delayed, idempotent, producerId, seq, dueTime);
            payload.Span.CopyTo(rented.AsSpan(QueueEnvelope.HeaderSize, payload.Length));
            return await _ring.WriteAsync(key, rented.AsMemory(0, length), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>读 record 的原 payload（解 envelope 剥头——投递/死信共用）。</summary>
    /// <param name="addr">消息地址（Ring 定位）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>解 envelope 后的原 payload 字节。</returns>
    private async ValueTask<byte[]> ReadPayloadAsync(LogicalAddress addr, CancellationToken ct)
    {
        var recordKey = await _ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
        var buf = new byte[recordKey.ValueLength];
        if (buf.Length > 0)
            await _ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
        if (QueueEnvelope.TryUnwrap(buf, out _, out _, out _, out _, out var payload))
            return payload;
        return buf;   // 非本格式（防御——理论不可达：写入恒经 Wrap）
    }

    /// <summary>积压治理：超 MaxBacklogBytes 时按策略 Block/Reject/EvictOldest（spec §8.2）。</summary>
    /// <param name="ct">取消令牌（Block 模式等待响应）。</param>
    /// <exception cref="QueueFullException">Reject / Block 超时 / EvictOldest 无可 fence 组。</exception>
    private async ValueTask EnforceBacklogAsync(CancellationToken ct)
    {
        if (!_options.MaxBacklogBytes.HasValue) return;
        long limit = _options.MaxBacklogBytes.Value;

        while (true)
        {
            long backlog = ComputeBacklog();
            if (backlog <= limit) return;

            switch (_options.Backlog)
            {
                case BacklogPolicy.Reject:
                    throw new QueueFullException(backlog, limit);

                case BacklogPolicy.EvictOldest:
                    if (!EvictSlowestGroup())
                        throw new QueueFullException(backlog, limit);   // 无可 fence 组 → 等价 Reject
                    continue;   // 淘汰后重测

                case BacklogPolicy.Block:
                default:
                    long deadline = _clock.GetMsTimestamp() + (long)_options.BacklogBlockTimeout.TotalMilliseconds;
                    TaskCompletionSource wait = Volatile.Read(ref _backlogSignal);
                    long remaining = deadline - _clock.GetMsTimestamp();
                    if (remaining <= 0)
                        throw new QueueFullException(ComputeBacklog(), limit);
                    try
                    {
                        await wait.Task.WaitAsync(TimeSpan.FromMilliseconds(remaining), ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        throw new QueueFullException(ComputeBacklog(), limit);
                    }
                    continue;   // 被脉冲（回收推进）→ 重测
            }
        }
    }

    /// <summary>积压字节 = [min 组下界, 尾) 距离（fenced 组不钉住——spec §8.1）。</summary>
    /// <returns>积压字节数（无组/全 fence = 0）。</returns>
    private long ComputeBacklog()
    {
        LogicalAddress floor = MinFloor();
        return floor.IsValid ? _ring.GetByteDistance(floor, _ring.TailAddress) : 0;
    }

    /// <summary>全部非 fence 组的截断下界 min（Invalid = 无组/全 fence → 无钉住）+ 延迟索引分量（spec §8.1）。</summary>
    /// <returns>最小存活地址（Invalid = 无组/全 fence/无延迟条目）。</returns>
    private LogicalAddress MinFloor()
    {
        LogicalAddress min = LogicalAddress.Invalid;
        foreach (var g in _groups.Values)
        {
            var floor = g.Floor(_ring.BeginAddress);
            if (!floor.IsValid) continue;
            if (!min.IsValid || floor < min) min = floor;
        }
        // ★ 延迟索引最小存活地址（未就绪消息钉住——spec §4.3/§8.1）
        if (_delayIndex is { } delay)
        {
            var dFloor = delay.MinLiveAddress(LogicalAddress.Invalid);
            if (dFloor.IsValid && (!min.IsValid || dFloor < min)) min = dFloor;
        }
        return min;
    }

    /// <summary>EvictOldest：fence 下界最小（最慢）组——数据回收 + 组显式破坏（Reset 续命）。</summary>
    /// <returns>true = 已 fence 最慢组可重测；false = 无可 fence 组（等价 Reject）。</returns>
    private bool EvictSlowestGroup()
    {
        QueueGroup? slowest = null;
        LogicalAddress min = LogicalAddress.Invalid;
        foreach (var g in _groups.Values)
        {
            var floor = g.Floor(_ring.BeginAddress);
            if (!floor.IsValid) continue;
            if (!min.IsValid || floor < min)
            {
                min = floor;
                slowest = g;
            }
        }
        if (slowest is null) return false;
        slowest.Fence();
        _logger?.LogWarning("TierQueue EvictOldest: group {Group} fenced（未确认数据回收——ResetGroupAsync 续命）",
            slowest.Name);
        TryTruncateAll();
        return true;
    }

    /// <summary>截断推进：bound = MinFloor（再夹 FlushedUntil——Ring 既有守卫前置）。</summary>
    private void TryTruncateAll()
    {
        LogicalAddress bound = MinFloor();
        if (!bound.IsValid) return;
        if (bound > _ring.FlushedUntilAddress) bound = _ring.FlushedUntilAddress;
        if (bound > _ring.BeginAddress)
            _ring.TruncatePrefix(bound);
        PulseBacklog();
    }

    /// <summary>脉冲积压等待者（Block 档——ack/delete/reset/evict 后回收可能推进）。</summary>
    private void PulseBacklog()
        => Interlocked.Exchange(ref _backlogSignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    // ═══════════════════════════════════════════════════════════════════
    // P0 兼容面（委托 "$default" 组——地址档基线语义）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<QueueDelivery>> DequeueAsync(int maxCount, CancellationToken ct)
    {
        var g = GetGroup(DefaultGroupName);
        var (deliveries, _) = await g.DequeueAsync(_ring, maxCount, _defaultConsumerId, ct).ConfigureAwait(false);
        return deliveries;
    }

    /// <inheritdoc/>
    public async ValueTask AckAsync(IReadOnlyList<LogicalAddress> acked, CancellationToken ct)
    {
        var g = GetGroup(DefaultGroupName);
        await g.AckByAddressAsync(_ring, acked, ct).ConfigureAwait(false);
        TryTruncateAll();
    }

    /// <inheritdoc/>
    public async ValueTask AckBufferedAsync(IReadOnlyList<LogicalAddress> acked, CancellationToken ct)
    {
        EnsureReady();
        var g = GetGroup(DefaultGroupName);
        if (await g.AckBufferedByAddressAsync(_ring, acked, ct).ConfigureAwait(false))
            TryTruncateAll();
    }

    /// <inheritdoc/>
    public async ValueTask FlushAcknowledgementsAsync(CancellationToken ct)
    {
        EnsureReady();
        var g = GetGroup(DefaultGroupName);
        if (await g.FlushBufferedAcknowledgementsAsync(_ring, ct).ConfigureAwait(false))
            TryTruncateAll();
    }

    /// <inheritdoc/>
    public LogicalAddress GroupCursor => GetGroup(DefaultGroupName).Cursor;

    // ═══════════════════════════════════════════════════════════════════
    // 消费组 API（P1——spec §3）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<ITierQueueConsumer> CreateGroupAsync(GroupOptions opt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opt);
        ValidateGroupName(opt.Name);
        EnsureReady();

        if (_groups.ContainsKey(opt.Name))
            throw new InvalidOperationException($"消费组已存在：{opt.Name}");

        // 注册表先行（原子提交）→ 组状态域装配（初始位点经 ResetAsync 定格持久）→ 死信接线
        // ★ 引擎全价（私有线程池+epoch+句柄）——落账 _groups 前的任何失败必须 Dispose 已建组
        //   （禁"造了就扔"；失败后重试会重新构造，泄漏线性放大——2026-09-14 引擎风暴判例）
        var group = await BuildGroupAsync(opt, ct).ConfigureAwait(false);
        group.DeadLetterTarget = _dlq;
        group.DelayIndex = _delayIndex;
        try
        {
            await group.ResetAsync(_ring, opt.StartAt, opt.StartAddress, ct).ConfigureAwait(false);
        }
        catch
        {
            await group.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (!_groups.TryAdd(opt.Name, group))
        {
            await group.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"消费组已存在：{opt.Name}");
        }
        Resources.Add(group.Meta, $"group:{opt.Name}");   // 落账后登记（组名独占——重名不可能）
        try
        {
            PersistRegistry();
        }
        catch
        {
            _groups.TryRemove(opt.Name, out _);
            await group.DisposeAsync().ConfigureAwait(false);
            TryDeleteEngineDir($"{_options.QueueName}.group.{opt.Name}");
            throw;
        }
        TryTruncateAll();   // 新组下界参与（Earliest 组钉住现存数据）
        return new TierQueueConsumer(group, _ring);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListGroupsAsync(CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<string>>([.. _groups.Keys]);

    /// <inheritdoc/>
    public async ValueTask DeleteGroupAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name == DefaultGroupName)
            throw new InvalidOperationException($"内建组 {DefaultGroupName} 不可删除——它是队列级兼容面的宿主");
        EnsureReady();

        if (!_groups.TryRemove(name, out var group))
            throw new InvalidOperationException($"消费组不存在：{name}");

        PersistRegistry();
        // 状态域销毁（flush）+ 引擎目录回收（best-effort）
        await group.DisposeAsync().ConfigureAwait(false);
        Resources.Remove($"group:{name}");   // 登记摘除——同名组重建时新引擎可注册
        TryDeleteEngineDir($"{_options.QueueName}.group.{name}");
        TryTruncateAll();   // 下界抬升（绊脚松开）
    }

    /// <inheritdoc/>
    public async ValueTask ResetGroupAsync(string name, GroupStartAt startAt, LogicalAddress startAddress,
        CancellationToken ct)
    {
        var g = GetGroup(name);
        await g.ResetAsync(_ring, startAt, startAddress, ct).ConfigureAwait(false);
        TryTruncateAll();
    }

    /// <inheritdoc/>
    public ValueTask<QueueStats> GetStatsAsync(CancellationToken ct)
    {
        EnsureReady();
        var groupStats = new List<GroupStats>(_groups.Count);
        foreach (var g in _groups.Values)
        {
            var (pending, lag) = g.Stats(_ring);
            groupStats.Add(new GroupStats(g.Name, g.Cursor, pending, g.IsFenced, lag, 0));
        }
        return ValueTask.FromResult(new QueueStats(
            _ring.BeginAddress, _ring.TailAddress, _ring.FlushedUntilAddress, ComputeBacklog(), groupStats));
    }

    /// <summary>取组句柄（token 档消费面——CreateGroupAsync 返回值同款）。</summary>
    /// <param name="groupName">组名。</param>
    /// <returns>消费组句柄（多消费者 = 多句柄同组）。</returns>
    public ITierQueueConsumer CreateConsumerAsync(string groupName)
        => new TierQueueConsumer(GetGroup(groupName), _ring);

    // ═══════════════════════════════════════════════════════════════════
    // 死信回放（spec §7.3——业务编排糖：DLQ 读出 → 解 envelope → 目标入队 → DLQ 确认）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> ReplayAsync(
        LogicalAddress deadLetterAddress, ITierQueue target, CancellationToken ct)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(target);

        var recordKey = await _ring.GetKeyAsync(deadLetterAddress, ct).ConfigureAwait(false);
        var buf = new byte[recordKey.ValueLength];
        if (buf.Length > 0)
            await _ring.GetValueAsync(deadLetterAddress, buf, ct).ConfigureAwait(false);
        // ★ DLQ 条目 = 产品 envelope（外层）包裹死信 envelope（内层）——DLQ 入队走同一条 Enqueue 路径
        if (QueueEnvelope.TryUnwrap(buf, out _, out _, out _, out _, out var inner))
            buf = inner;
        if (!DeadLetterEnvelope.TryUnwrap(buf, out _, out _, out _, out var payload))
            throw new InvalidDataException($"地址 {deadLetterAddress} 不是死信 envelope（magic 不符）");

        var result = await target.EnqueueAsync(payload, ct).ConfigureAwait(false);
        await AckAsync([deadLetterAddress], ct).ConfigureAwait(false);   // DLQ 内建组确认（本队列 $default）
        return result;
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReplayAllAsync(ITierQueue target, CancellationToken ct)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(target);

        int replayed = 0;
        while (true)
        {
            var batch = await DequeueAsync(64, ct).ConfigureAwait(false);
            if (batch.Count == 0) break;

            var payloads = new List<ReadOnlyMemory<byte>>(batch.Count);
            var addresses = new List<LogicalAddress>(batch.Count);
            foreach (var d in batch)
            {
                if (!DeadLetterEnvelope.TryUnwrap(d.Payload, out _, out _, out _, out var payload))
                    throw new InvalidDataException("DLQ 含非 envelope 条目（混入普通消息？）");
                payloads.Add(payload);
                addresses.Add(d.Address);
            }
            await target.EnqueueBatchAsync(payloads, ct).ConfigureAwait(false);
            await AckAsync(addresses, ct).ConfigureAwait(false);
            replayed += batch.Count;
        }
        return replayed;
    }

    private QueueGroup GetGroup(string name)
    {
        EnsureReady();
        if (!_groups.TryGetValue(name, out var g))
            throw new InvalidOperationException($"消费组不存在：{name}");
        return g;
    }

    private static void ValidateGroupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ArgumentException("组名非法（空/超长）", nameof(name));
        if (name.Contains('/') || name.Contains('\\') || name.StartsWith('.'))
            throw new ArgumentException($"组名含路径字符或以点开头：{name}", nameof(name));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 水位 / 截断公开面（P0 保持）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public LogicalAddress HeadAddress => _ring.BeginAddress;

    /// <inheritdoc/>
    public LogicalAddress TailAddress => _ring.TailAddress;

    /// <inheritdoc/>
    public LogicalAddress DurableTail => _ring.FlushedUntilAddress;

    /// <inheritdoc/>
    public ValueTask FlushAsync(CancellationToken ct) => _ring.FlushUntilAsync(_ring.TailAddress, ct);

    /// <inheritdoc/>
    public ValueTask WaitForDurableAsync(LogicalAddress address, CancellationToken ct)
        => address.IsValid && address >= _ring.FlushedUntilAddress
            ? _ring.FlushUntilAsync(_ring.TailAddress, ct)
            : ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<LogicalAddress> TruncateAsync(LogicalAddress uptoExclusive, CancellationToken ct)
    {
        EnsureReady();
        LogicalAddress bound = MinFloor();   // ★ 下界守卫（spec §8.1）：多组 min——不截未确认数据
        LogicalAddress target = uptoExclusive;
        if (bound.IsValid && target > bound) target = bound;
        if (target > _ring.FlushedUntilAddress) target = _ring.FlushedUntilAddress;
        if (target > _ring.BeginAddress)
            _ring.TruncatePrefix(target);
        return ValueTask.FromResult(_ring.BeginAddress);
    }

    /// <inheritdoc/>
    public ITransactionParticipant GetGroupParticipant(string groupName)
        => GetGroup(groupName).Participant;

    /// <summary>内部诊断（测试/白盒）——组表观测。</summary>
    internal IReadOnlyDictionary<string, QueueGroup> DiagnosticGroups => _groups;

    // ═══════════════════════════════════════════════════════════════════
    // 组装配 + 注册表
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>组状态域装配（引擎多段追加流——P0 教训入档；注入工厂优先）。</summary>
    /// <param name="groupName">组名（引擎名 = {QueueName}.group.{groupName}）。</param>
    /// <param name="ct">取消令牌（当前实现零 IO——保留参数对齐）。</param>
    /// <returns>装配完成的组状态 VersionedMetadata 实例（未 Initialize）。</returns>
    private ValueTask<VersionedMetadata> BuildGroupMetaAsync(string groupName, CancellationToken ct)
    {
        var defaults = new StorageEngineOptions(
            $"{_options.QueueName}.group.{groupName}", 4L << 20,
            enableSegmentation: true, preallocateFile: false);
        var settings = new VersionedMetadataSettings(
            _storageOptionsFactory?.Invoke("group", defaults) ?? defaults)
        {
            PayloadSize = QueueGroupState.PayloadSize(_options.MaxInFlight),
        };
        if (_groupMetaFactory is { } f) return ValueTask.FromResult(f(_fs, settings));
        return ValueTask.FromResult(new VersionedMetadata(_fs, settings));
    }

    private async ValueTask<QueueGroup> BuildGroupAsync(GroupOptions opt, CancellationToken ct)
    {
        // ★ 引擎全价（私有线程池+epoch+句柄）——装配失败必须 Dispose 已建实例（禁"造了就扔"：
        //   重放 at-least-once 下失败重试会反复构造，泄漏即线性爆炸，2026-09-14 引擎风暴判例）
        var meta = await BuildGroupMetaAsync(opt.Name, ct).ConfigureAwait(false);
        try
        {
            meta.Initialize();
            await meta.WaitForReadyAsync(ct).ConfigureAwait(false);
            return new QueueGroup(meta, opt, _options.MaxInFlight, _options.BufferedAckBatchSize,
                _options.BufferedAckCommitInterval, _logger, clock: _options.Clock);
        }
        catch
        {
            await meta.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>注册表持久（原子提交——组清单 + 定格配置）。</summary>
    private void PersistRegistry()
    {
        var entries = new List<GroupRegistryEntry>(_groups.Count);
        foreach (var g in _groups.Values)
            entries.Add(new GroupRegistryEntry(g.Name,
                (long)g.Config.VisibilityTimeout.TotalMilliseconds, g.Config.MaxRedeliveries));
        var payload = QueueGroupRegistry.Write(entries);
        if (payload.Length > _options.GroupRegistryPayloadSize)
            throw new InvalidOperationException(
                $"消费组注册表超限：{payload.Length} > GroupRegistryPayloadSize={_options.GroupRegistryPayloadSize}");
        _registry.Write(payload);
        _registry.Persist();
        _registry.ReclaimOldVersions();
    }

    /// <summary>引擎目录回收（best-effort——失败不阻断，下次启动扫盘自愈）。</summary>
    /// <param name="engineName">引擎名（{QueueName}.group.{groupName} 等）。</param>
    private void TryDeleteEngineDir(string engineName)
    {
        try
        {
            _fs.DeleteDirectory(engineName);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TierQueue: 组引擎目录回收失败（不阻断）{Dir}", engineName);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 复制 apply 端口（tierqueue-replicated-spec §8——显式接口实现，公共面零暴露；
    //   全部方法 = 确定性核心：判定依据取自命令载荷或已复制状态，不看本地时钟）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    async ValueTask<LogicalAddress> ITierQueueReplicationPort.ApplyEnqueueAsync(bool delayed, long dueTime,
        bool idempotent, long producerId, long seq, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // 幂等命中短路：返回首次地址不追加（apply 确定性——索引状态 = 已复制命令序的函数）
        if (idempotent && _idempotencyIndex is { } idem)
        {
            var key = new QueueKey(producerId, seq);
            LogicalAddress existing = idem.Find(key);
            if (existing != LogicalAddress.Empty)
                return existing;
        }

        // Ring 写（record key 二态：(due,0) 延迟 / (0,0) 即时——delayed 由命令定案，apply 不看时钟）
        var recordKey = delayed ? new QueueKey(dueTime, 0) : default(QueueKey);
        LogicalAddress addr = _options.OverflowPolicy == OverflowPolicy.Enabled
            && checked(QueueEnvelope.HeaderSize + payload.Length) > _options.MinOverflowSize
            ? await WriteOverflowEnvelopeAsync(recordKey, delayed, idempotent, producerId, seq, dueTime, payload, ct)
                .ConfigureAwait(false)
            : WriteInlineEnvelope(recordKey, delayed, idempotent, producerId, seq, dueTime, payload.Span);

        if (delayed)
            _delayIndex!.Insert(dueTime, addr);   // 延迟索引登记（命令携带 delayed=true ⇒ 索引已装配——提案侧校验）
        if (idempotent && _idempotencyIndex is { } idemInsert)
            idemInsert.Insert(new QueueKey(producerId, seq), addr, idemInsert.BeginAddress);
        return addr;
    }

    /// <inheritdoc/>
    async ValueTask ITierQueueReplicationPort.ApplyGroupCreateAsync(GroupOptions opt, CancellationToken ct)
    {
        ValidateGroupName(opt.Name);
        // apply at-least-once 契约：重放窗口内同一 create 可能重复到达（首次 apply 后管道重试）——
        // 幂等 no-op；提案侧重复建组的确定性拒绝由副本 API 层承接（本地已应用视图预检）
        if (_groups.ContainsKey(opt.Name))
            return;
        // ★ 引擎全价——落账 _groups 前的任何失败必须 Dispose 已建组：否则重放重试每次重新构造
        //   一个引擎并泄漏一个（私有线程池不退 = 引擎永不回收）——楔死态下 6 个/秒的引擎风暴判例
        var group = await BuildGroupAsync(opt, ct).ConfigureAwait(false);
        group.DelayIndex = _delayIndex;   // 死信目标不接线（复制面死信写入由辖权节点提案前完成）
        try
        {
            await group.ResetAsync(_ring, opt.StartAt, opt.StartAddress, ct).ConfigureAwait(false);
            if (!_groups.TryAdd(opt.Name, group))
            {
                await group.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"消费组已存在：{opt.Name}");
            }
        }
        catch
        {
            if (!_groups.ContainsKey(opt.Name))
                await group.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        Resources.Add(group.Meta, $"group:{opt.Name}");   // 落账后登记（组名独占——重名不可能）
        PersistRegistry();
        TryTruncateAll();
    }

    /// <inheritdoc/>
    async ValueTask ITierQueueReplicationPort.ApplyGroupRestoreAsync(string name, TimeSpan visibilityTimeout,
        int maxRedeliveries, QueueGroupState.State state, CancellationToken ct)
    {
        ValidateGroupName(name);
        var config = new GroupOptions
        {
            Name = name,
            VisibilityTimeout = visibilityTimeout,
            MaxRedeliveries = maxRedeliveries,
        };
        // upsert 语义：已存在组（本地恢复自举的 $default）直载状态覆写；不存在组按恢复核心同构装配
        if (_groups.TryGetValue(name, out var existing))
        {
            await existing.RestoreStateFromConsensusAsync(state, ct).ConfigureAwait(false);
            return;
        }
        // ★ 引擎全价——装配失败路径 Dispose 已建实例（同 ApplyGroupCreateAsync 风暴判例）
        var meta = await BuildGroupMetaAsync(name, ct).ConfigureAwait(false);
        try
        {
            meta.Initialize();
            await meta.WaitForReadyAsync(ct).ConfigureAwait(false);
            var group = new QueueGroup(meta, config, _options.MaxInFlight, _options.BufferedAckBatchSize,
                _options.BufferedAckCommitInterval, _logger, state, clock: _options.Clock)
            {
                DelayIndex = _delayIndex,
            };
            _groups[name] = group;
            Resources.Add(meta, $"group:{name}");   // 落账后登记（组名独占——重名不可能）
            PersistRegistry();
        }
        catch
        {
            if (!_groups.ContainsKey(name))
                await meta.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    async ValueTask ITierQueueReplicationPort.ReconcileDelayIndexAsync(CancellationToken ct)
    {
        if (_delayIndex is not { } delay)
            return;
        LogicalAddress floor = MinFloor();
        await delay.ReconcileAsync(_ring, floor, ct).ConfigureAwait(false);   // 清理已终结 + 重建快照窗口缺失
    }

    /// <inheritdoc/>
    ValueTask ITierQueueReplicationPort.FlushRingAsync(CancellationToken ct)
        => _ring.FlushUntilAsync(_ring.TailAddress, ct);

    /// <inheritdoc/>
    async ValueTask ITierQueueReplicationPort.ApplyGroupDeleteAsync(string name)
    {
        if (name == DefaultGroupName)
            throw new InvalidOperationException($"内建组 {DefaultGroupName} 不可删除——它是队列级兼容面的宿主");
        if (!_groups.TryRemove(name, out var group))
            throw new InvalidOperationException($"消费组不存在：{name}");
        PersistRegistry();
        await group.DisposeAsync().ConfigureAwait(false);
        Resources.Remove($"group:{name}");   // 登记摘除——同名组重建时新引擎可注册
        TryDeleteEngineDir($"{_options.QueueName}.group.{name}");
        TryTruncateAll();
    }

    /// <inheritdoc/>
    async ValueTask ITierQueueReplicationPort.ApplyGroupResetAsync(string name, GroupStartAt startAt,
        LogicalAddress startAddress, CancellationToken ct)
    {
        var g = GetGroup(name);
        await g.ResetAsync(_ring, startAt, startAddress, ct).ConfigureAwait(false);
        TryTruncateAll();
    }

    /// <inheritdoc/>
    async ValueTask ITierQueueReplicationPort.ApplyAckAsync(string name, long epoch,
        IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
    {
        var g = GetGroup(name);
        await g.ApplyAckFromConsensusAsync(_ring, epoch, addresses, ct).ConfigureAwait(false);
        // 被确认地址摘出延迟索引（skip 终局 = 永不再投——解除回收线钉住，本地 CancelDelayed 同源语义）
        if (_delayIndex is { } delay)
        {
            foreach (var a in addresses)
                delay.Remove(_ring, a);
        }
        TryTruncateAll();
    }

    /// <inheritdoc/>
    async ValueTask ITierQueueReplicationPort.ApplyExpireAsync(string name,
        IReadOnlyList<QueueExpireEntry> entries, CancellationToken ct)
    {
        var g = GetGroup(name);
        await g.ApplyExpireFromConsensusAsync(_ring, entries, ct).ConfigureAwait(false);
        TryTruncateAll();
    }

    /// <inheritdoc/>
    async ValueTask ITierQueueReplicationPort.ApplyEpochBumpAsync(string name)
    {
        var g = GetGroup(name);
        await g.ApplyEpochBumpFromConsensusAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    void ITierQueueReplicationPort.ApplyRetention(LogicalAddress? target)
    {
        if (target is { } t)
        {
            LogicalAddress bound = MinFloor();
            LogicalAddress applied = t;
            if (bound.IsValid && applied > bound) applied = bound;
            if (applied > _ring.FlushedUntilAddress) applied = _ring.FlushedUntilAddress;
            if (applied > _ring.BeginAddress)
                _ring.TruncatePrefix(applied);
            PulseBacklog();
        }
        else
        {
            TryTruncateAll();
        }
    }

    /// <inheritdoc/>
    long ITierQueueReplicationPort.GetGroupEpoch(string name) => GetGroup(name).CurrentEpoch;

    /// <inheritdoc/>
    LogicalAddress ITierQueueReplicationPort.GetGroupCursor(string name) => GetGroup(name).Cursor;

    /// <inheritdoc/>
    ValueTask<byte[]> ITierQueueReplicationPort.ReadPayloadAsync(LogicalAddress address, CancellationToken ct)
        => ReadPayloadAsync(address, ct);

    /// <inheritdoc/>
    IReadOnlyList<string> ITierQueueReplicationPort.GetGroupNames() => [.. _groups.Keys];

    /// <inheritdoc/>
    (TimeSpan VisibilityTimeout, int MaxRedeliveries)? ITierQueueReplicationPort.TryGetGroupConfig(string name)
        => _groups.TryGetValue(name, out var g)
            ? (g.Config.VisibilityTimeout, g.Config.MaxRedeliveries)
            : null;

    /// <inheritdoc/>
    QueueGroupState.State ITierQueueReplicationPort.GetGroupState(string name) => GetGroup(name).CaptureState();

    /// <inheritdoc/>
    (LogicalAddress Begin, LogicalAddress Tail) ITierQueueReplicationPort.GetRingBounds()
        => (_ring.BeginAddress, _ring.TailAddress);

    /// <inheritdoc/>
    IRingSnapshotReader ITierQueueReplicationPort.OpenRingReader(LogicalAddress begin, LogicalAddress end)
        => _ring.OpenSnapshotReader(begin, end);

    /// <inheritdoc/>
    IRingSnapshotWriter ITierQueueReplicationPort.OpenRingWriter(LogicalAddress begin, LogicalAddress end)
        => _ring.OpenSnapshotWriter(begin, end);

    /// <inheritdoc/>
    ValueTask ITierQueueReplicationPort.EnforceBacklogAsync(CancellationToken ct) => EnforceBacklogAsync(ct);

    /// <inheritdoc/>
    TimeSpan? ITierQueueReplicationPort.DelayWindow
        => _options.Delayed is { } d ? d.MaxDelay : null;

    /// <inheritdoc/>
    bool ITierQueueReplicationPort.IdempotencyEnabled => _idempotencyIndex is not null;

    /// <inheritdoc/>
    bool ITierQueueReplicationPort.TryFindIdempotent(long producerId, long seq, out LogicalAddress address)
    {
        if (_idempotencyIndex is { } idem)
        {
            address = idem.Find(new QueueKey(producerId, seq));
            return address != LogicalAddress.Empty;
        }
        address = default;
        return false;
    }

    /// <inheritdoc/>
    ValueTask<IReadOnlyList<LogicalAddress>> ITierQueueReplicationPort.ResolveDelayedAddressesAsync(
        CancelDelayedFilter filter, CancellationToken ct)
    {
        if (_delayIndex is not { } delay)
            throw new InvalidOperationException("延迟管理需要 Options.Delayed 开启");
        List<LogicalAddress> removed;
        if (filter.Address is { } addr)
        {
            removed = delay.Remove(_ring, addr) ? [addr] : [];
        }
        else if (filter.FromDueInclusive is { } from && filter.ToDueInclusive is { } to)
        {
            removed = delay.RemoveRange(from, to);
        }
        else
        {
            throw new ArgumentException("取消过滤非法：需 Address 或 FromDue/ToDue 区间");
        }
        return ValueTask.FromResult<IReadOnlyList<LogicalAddress>>(removed);
    }

    /// <inheritdoc/>
    ValueTask<IReadOnlyList<QueueDelivery>> ITierQueueReplicationPort.DequeueLocalAsync(
        string name, int maxCount, Guid consumerId, CancellationToken ct)
        => GetGroup(name).DequeueLocalAsync(_ring, maxCount, consumerId, ct);

    /// <inheritdoc/>
    ValueTask<IReadOnlyList<PendingInfo>> ITierQueueReplicationPort.PendingLocalAsync(
        string name, CancellationToken ct)
        => GetGroup(name).PendingAsync(ct);

    /// <inheritdoc/>
    ValueTask ITierQueueReplicationPort.RemovePendingLocalAsync(
        string name, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
        => GetGroup(name).RemovePendingLocalAsync(addresses, ct);



    private sealed class TierQueueRecovery(TierQueue owner) : RecoveryBase<TierQueueRecoveryHints>
    {
        /// <summary>层间 join——Ring + 注册表（OnInitializeBegin 已并行启动）。</summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>任务在 Ring 与注册表均 Ready 后完成。</returns>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._ring.WaitForReadyAsync(ct).ConfigureAwait(false);
            await owner._registry.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复核心（spec §9）：延迟/幂等索引启动（Ring 已 Ready——resolver 数据面可用）→
        /// 注册表载入 → 逐组装配（状态域 + 状态镜像）→ $default 确保 →
        /// pending 清零天然（纯内存）→ 延迟对账 → 恢复自动截断 → 放行。
        /// </summary>
        /// <param name="hints">恢复 hints（P0 无注入项——预留）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>恢复完成（异常时抛——RecoveryBase 转为 Failed 销毁重建）。</returns>
        protected override async ValueTask OnRecoveryCoreAsync(TierQueueRecoveryHints hints, CancellationToken ct)
        {
            // ★ 索引启动在 Ring join 之后（WaitForDependencies 已保证）——后台恢复的 resolver 扫描依赖 Ring Ready。
            //   幂等索引：传重放窗口 [Begin, Tail)（组合层契约——spec §6.4 档三；dump 加速未接时
            //   全量重放 = 正确性真源，幂等条目量 = 生产者窗口，重放代价有界）。
            if (owner._delayIndex is { } delayStart)
                await delayStart.StartAsync(owner._ring, ct).ConfigureAwait(false);
            if (owner._idempotencyIndex is { } idemStart)
            {
                var idemHints = new ProbingIndexRecoveryHints(
                    owner._ring.BeginAddress, owner._ring.TailAddress);
                idemStart.Initialize(idemHints);
                await idemStart.WaitForReadyAsync(ct).ConfigureAwait(false);
            }

            var buf = new byte[owner._options.GroupRegistryPayloadSize];
            int read = owner._registry.Read(buf);
            var names = new List<GroupRegistryEntry>();
            if (read > 0 && QueueGroupRegistry.TryRead(buf.AsSpan(0, read), out var entries))
                names.AddRange(entries);

            // $default 确保（首次启动 = 空注册表 → 建内建组）
            if (names.All(e => e.Name != DefaultGroupName))
                names.Add(new GroupRegistryEntry(DefaultGroupName,
                    (long)TimeSpan.FromSeconds(60).TotalMilliseconds, 16));

            foreach (var e in names)
            {
                // ★ 恢复重入幂等（取消重试路径）：已落账组跳过——重造 = 引擎泄漏 + Resources 重名异常
                if (owner._groups.ContainsKey(e.Name))
                    continue;
                var config = new GroupOptions
                {
                    Name = e.Name,
                    VisibilityTimeout = TimeSpan.FromMilliseconds(Math.Max(100, e.VisibilityTimeoutMs)),
                    MaxRedeliveries = e.MaxRedeliveries,
                };
                // ★ 引擎全价——装配失败路径 Dispose 已建实例（禁"造了就扔"，同 ApplyGroupCreateAsync 判例）
                var meta = await owner.BuildGroupMetaAsync(e.Name, ct).ConfigureAwait(false);
                QueueGroup group;
                try
                {
                    meta.Initialize();
                    await meta.WaitForReadyAsync(ct).ConfigureAwait(false);

                    var sbuf = new byte[QueueGroupState.PayloadSize(owner._options.MaxInFlight)];
                    int sread = meta.Read(sbuf);
                    QueueGroupState.State? state = null;
                    if (sread >= QueueGroupState.PayloadSize(owner._options.MaxInFlight)
                        && QueueGroupState.TryRead(sbuf.AsSpan(0, sread), owner._options.MaxInFlight, out var s))
                        state = s;

                    group = new QueueGroup(meta, config, owner._options.MaxInFlight,
                        owner._options.BufferedAckBatchSize, owner._options.BufferedAckCommitInterval,
                        owner._logger, state, clock: owner._options.Clock)
                    {
                        DeadLetterTarget = owner._dlq,
                        DelayIndex = owner._delayIndex,
                    };
                    owner._groups[e.Name] = group;
                }
                catch
                {
                    await meta.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                owner.Resources.Add(meta, $"group:{e.Name}");   // 落账后登记（组名独占——重名不可能）
            }

            owner.PersistRegistry();            // $default 补登记（幂等）
            // ★ 延迟索引对账（spec §9.4）：清理已终结条目 + 重建崩溃窗口缺失（索引持久化滞后于 Ring）
            if (owner._delayIndex is { } delay)
            {
                LogicalAddress floor = owner.MinFloor();
                var (cleaned, reinserted) = await delay.ReconcileAsync(owner._ring, floor, ct).ConfigureAwait(false);
                RaiseProgress(80, $"delay-reconcile: cleaned={cleaned} reinserted={reinserted}");
            }
            owner.TryTruncateAll();             // 恢复自动截断（契约③）
            RaiseProgress(90, $"groups={owner._groups.Count}");
        }
    }
}
