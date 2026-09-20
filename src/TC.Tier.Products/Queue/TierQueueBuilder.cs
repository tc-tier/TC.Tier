using TC.Tier.Contracts.Meta;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Lifecycle;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Contracts.Structures;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Products.Queue;

/// <summary>数据 Ring 装配工厂——Builder 内部调（fs + 由 Options 派生的 Settings → Ring 实例）。
/// 不注入 = 默认装配（new RingOfQueueKey + 透传已注入的 metaPolicyFactory/epoch/logger）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">Ring 装配规格（由 Options 派生）。</param>
/// <returns>装配完成的数据 Ring 实例。</returns>
public delegate RingOfQueueKey TierQueueRingFactory(
    IFileSystem fs, BlittableRingSettings settings);

/// <summary>组状态 meta 装配工厂——Builder 内部调（fs + 由 Options 派生的 Settings → VersionedMetadata 实例）。
/// 不注入 = 默认装配（new VersionedMetadata）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">VersionedMetadata 装配规格。</param>
/// <returns>装配完成的组状态 meta 实例。</returns>
public delegate VersionedMetadata TierQueueGroupMetaFactory(
    IFileSystem fs, VersionedMetadataSettings settings);

/// <summary>注册表 meta 装配工厂。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">VersionedMetadata 装配规格。</param>
/// <returns>装配完成的注册表 meta 实例。</returns>
public delegate VersionedMetadata TierQueueRegistryMetaFactory(
    IFileSystem fs, VersionedMetadataSettings settings);

/// <summary>延迟索引装配工厂。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">BTree 索引装配规格。</param>
/// <param name="epoch">共享读保护域（null = 各结构自管）。</param>
/// <param name="keyResolver">键解析器（读 Ring record 反解 QueueKey）。</param>
/// <param name="keyComparer">键比较器（QueueKeyComparer）。</param>
/// <returns>装配完成的延迟 BTree 索引实例。</returns>
public delegate BTreeOfQueueKey TierQueueDelayedIndexFactory(
    IFileSystem fs, BTreeIndexSettings settings, LightEpoch? epoch,
    IKeyResolver<QueueKey> keyResolver, IKeyComparer<QueueKey> keyComparer);

/// <summary>幂等索引装配工厂。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">Hash 索引装配规格。</param>
/// <param name="epoch">共享读保护域（null = 各结构自管）。</param>
/// <param name="keyResolver">键解析器（读 record envelope 反解 producerId/seq）。</param>
/// <param name="keyComparer">键比较器（QueueKeyComparer）。</param>
/// <returns>装配完成的幂等 Hash 索引实例。</returns>
public delegate HashOfQueueKey TierQueueIdempotencyIndexFactory(
    IFileSystem fs, HashIndexSettings settings, LightEpoch? epoch,
    IKeyResolver<QueueKey> keyResolver, IKeyComparer<QueueKey> keyComparer);

/// <summary>Queue 组件引擎选项变换器；可覆盖 hints、优化参数及全部 StorageEngineOptions 字段。</summary>
/// <param name="componentName">组件名（ring/groups/dlq/delay/idem——日志/诊断用）。</param>
/// <param name="defaults">由 Options 派生的默认 StorageEngineOptions。</param>
/// <returns>变换后的 StorageEngineOptions（null 变换器 = 原样 defaults）。</returns>
public delegate StorageEngineOptions TierQueueStorageOptionsFactory(
    string componentName, StorageEngineOptions defaults);

/// <summary>
/// TierQueue 构建者（tc-tier-queue-spec §3.1——三段式装配惯例同 TierWalBuilder：
/// Options 配置链 → Builder → StartAsync 一步到位）。
/// <para>★ 组合设计思想（注入优先、回落默认）：零件规格全在 <see cref="TierQueueOptions"/>；
///   可替换件全走 With\* 注入——Ring 工厂（自定义恢复/游标/快照/meta 策略经生成封闭类全参 ctor 自行装配）、
///   ring meta 策略工厂、组 meta 工厂、共享 epoch（多结构同域惯例）、logger；不注入 = 内部默认实现。</para>
/// <para>★ 组是运行时实体（CreateGroupAsync）不经 Builder——P0 单内建组 "$default" 随启动自动就绪。</para>
/// <para>★ 启动状态机同 TierWalBuilder：只能成功启动一次（重复抛）；失败可重试（销毁重建，
///   对齐 RecoveryBase "Failed 销毁重建" 哲学）。</para>
/// </summary>
public sealed class TierQueueBuilder : IDisposable, IAsyncDisposable
{
    private readonly IFileSystem _fs;
    private readonly TierQueueOptions _options;

    // === 注入面（不注入回落默认）===
    private TierQueueRingFactory? _ringFactory;
    private TierQueueGroupMetaFactory? _groupMetaFactory;
    private TierQueueRegistryMetaFactory? _registryMetaFactory;
    private TierQueueDelayedIndexFactory? _delayedIndexFactory;
    private TierQueueIdempotencyIndexFactory? _idempotencyIndexFactory;
    private TierQueueStorageOptionsFactory? _storageOptionsFactory;
    private MetaPolicyFactory<RingMetaHeader, RingMetaPayload>? _ringMetaPolicyFactory;
    private LightEpoch? _epoch;
    private IsolatedTaskScheduler? _workerScheduler;   // 调度器共享注入（#505——多实例一组线程）
    private ILogger? _logger;

    private TierQueue? _queue;
    private bool _ownershipTransferred;
    private int _started;   // 0=未启动 1=已启动/启动中（CAS 抢启动权）

    /// <summary>构造（= 配置，零 IO）。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TierQueue 选项（队列名/容量/治理/索引开关）。</param>
    /// <exception cref="ArgumentNullException">fs 或 options 为 null。</exception>
    public TierQueueBuilder(IFileSystem fs, TierQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(options);
        _fs = fs;
        _options = options;
    }

    /// <summary>注入数据 Ring 装配工厂（自定义整件替换——恢复算法/扫描游标/快照/meta 策略全参自控）。
    /// 不注入 = 默认装配（透传 WithMetaPolicyFactory/WithEpoch/WithLogger 到生成封闭类全参 ctor）。</summary>
    /// <param name="factory">Ring 工厂。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithRingFactory(TierQueueRingFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringFactory = factory;
        return this;
    }

    /// <summary>注入组状态 meta 装配工厂（自定义持久化策略/meta 传输/恢复替换）。
    /// 不注入 = 默认装配（new VersionedMetadata）。</summary>
    /// <param name="factory">组 meta 工厂。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithGroupMetaFactory(TierQueueGroupMetaFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _groupMetaFactory = factory;
        return this;
    }

    /// <summary>注入组注册表 metadata 工厂。</summary>
    /// <param name="factory">注册表 meta 工厂。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithRegistryMetaFactory(TierQueueRegistryMetaFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _registryMetaFactory = factory;
        return this;
    }

    /// <summary>注入延迟 BTree 工厂。</summary>
    /// <param name="factory">延迟索引工厂。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithDelayedIndexFactory(TierQueueDelayedIndexFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _delayedIndexFactory = factory;
        return this;
    }

    /// <summary>注入幂等 HashIndex 工厂。</summary>
    /// <param name="factory">幂等索引工厂。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithIdempotencyIndexFactory(TierQueueIdempotencyIndexFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _idempotencyIndexFactory = factory;
        return this;
    }

    /// <summary>注入各组件的 StorageEngineOptions 变换器。</summary>
    /// <param name="factory">引擎选项变换器。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithStorageOptionsFactory(TierQueueStorageOptionsFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _storageOptionsFactory = factory;
        return this;
    }

    /// <summary>注入 Ring meta 策略工厂（默认回落 Managed 模式——水位持久化，P0 已证实必需：
    /// Disabled 下重启丢水位回退引擎假尾）。</summary>
    /// <param name="factory">meta 策略工厂。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithMetaPolicyFactory(MetaPolicyFactory<RingMetaHeader, RingMetaPayload> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringMetaPolicyFactory = factory;
        return this;
    }

    /// <summary>注入共享 epoch（组合域多结构共用同一读保护域的惯例——Ring 与组 meta 同域时注入）。
    /// 不注入 = 各结构自管。</summary>
    /// <param name="epoch">共享 epoch 实例。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithEpoch(LightEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        _epoch = epoch;
        return this;
    }

    /// <summary>注入引擎 worker 调度器共享实例（#505——ring/延迟/幂等/组注册表全部引擎共用一组线程；
    /// 多实例/嵌入式/测试拓扑线程数恒定；所有权 Referenced 归注入方——Queue 释放不回收）。
    /// 不注入 = 各引擎按 StorageOptionsFactory 配置自建。</summary>
    /// <param name="scheduler">共享调度器实例（如 <see cref="IsolatedTaskScheduler.Shared"/>）。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithWorkerScheduler(IsolatedTaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _workerScheduler = scheduler;
        return this;
    }

    /// <summary>注入日志器。</summary>
    /// <param name="logger">日志器实例。</param>
    /// <returns>TierQueueBuilder（链式）。</returns>
    public TierQueueBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// 一步到位：构建 + 恢复（Ring/meta join + 组状态载入 + 恢复自动截断）+ WaitForReady。
    /// </summary>
    /// <param name="hints">恢复 hints（P0 无注入项——预留）。</param>
    /// <param name="ct">取消令牌——透传 WaitForReadyAsync。</param>
    /// <returns>已恢复就绪的 TierQueue 实例。</returns>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public async Task<TierQueue> StartAsync(TierQueueRecoveryHints hints = default, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("TierQueueBuilder 只能成功启动一次（StartAsync）——已启动或正在启动");

        try
        {
            var ring = BuildRing();
            var registry = BuildRegistry();
            var dlq = await BuildDeadLetterAsync(ct).ConfigureAwait(false);   // 先于主队列（组恢复期即接线）
            var delayIndex = await BuildDelayedIndexAsync(ring, ct).ConfigureAwait(false);
            var idemIndex = await BuildIdempotencyIndexAsync(ring, ct).ConfigureAwait(false);

            // ★ 生命周期模板：TierQueue 构造（Ring + 注册表 + DLQ/索引进资源组）→ Initialize
            //   （OnInitializeBegin 并行启动 Ring/注册表 → 后台恢复核心 join + 组装配 + 延迟对账 +
            //   恢复自动截断）→ WaitForReady
            var queue = new TierQueue(ring, registry, _fs, dlq, delayIndex, idemIndex,
                _groupMetaFactory, _storageOptionsFactory, _workerScheduler, _options, _logger);
            queue.Initialize(hints);
            await queue.WaitForReadyAsync(ct).ConfigureAwait(false);

            _ownershipTransferred = true;   // 所有权转移（Dispose 由 TierQueue 负责）
            _queue = queue;
            _logger?.LogInformation("TierQueue started: {Name} tail={Tail} cursor={Cursor}",
                _options.QueueName, queue.TailAddress, queue.GroupCursor);
            return queue;
        }
        catch (Exception ex)
        {
            // ★ 失败可重试：销毁重建（Failed 哲学——重试 = 新实例）
            _logger?.LogWarning(ex, "TierQueueBuilder.StartAsync 启动失败——销毁重建（可重试 StartAsync）");
            try
            {
                if (_queue is not null) await _queue.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "TierQueueBuilder.StartAsync: 启动失败后销毁旧 TierQueue DisposeAsync 异常");
            }
            _queue = null;
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    /// <summary>装配数据 Ring：注入工厂优先；默认 = 生成封闭类全参 ctor（透传 meta 策略/epoch/logger）。</summary>
    /// <returns>装配完成的数据 Ring 实例。</returns>
    private RingOfQueueKey BuildRing()
    {
        var settings = new BlittableRingSettings(EngineOptions("ring", new StorageEngineOptions(
            $"{_options.QueueName}.ring", _options.SegmentGrowthLimit,
            enableSegmentation: true, preallocateFile: _fs is not TC.Tier.Core.IO.Mem.MemoryFileSystem, deleteOnClose: false)))
        {
            PageSize = _options.PageSize,
            MemorySize = _options.MemorySize,
            OverflowPolicy = _options.OverflowPolicy,
            MinOverflowSize = _options.MinOverflowSize,
            ColdReadRatio = _options.ColdReadRatio,
            // ★ 水位持久化（Settings 基类默认 Disabled=no-op——不显式开则重启丢水位，
            //   回退 engine.CommittedTail=预分配假尾；2026-08-27 裸 Ring 探针实锤）
            MetaPolicyKind = MetaPolicyKind.Managed,
            WorkerScheduler = _workerScheduler,
        };
        if (_ringFactory is { } f) return f(_fs, settings);
        return new RingOfQueueKey(settings, _fs, metaPolicyFactory: _ringMetaPolicyFactory,
            epoch: _epoch, logger: _logger);
    }

    /// <summary>装配组注册表（{name}.groups——组名 + 定格配置的版本链）。
    /// 多段追加流语义同组状态域（P0 教训入档：追加流消费者单段重开滚段炸）。</summary>
    /// <returns>装配完成的组注册表 VersionedMetadata 实例。</returns>
    private VersionedMetadata BuildRegistry()
    {
        var settings = new VersionedMetadataSettings(EngineOptions("groups", new StorageEngineOptions(
            $"{_options.QueueName}.groups", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            PayloadSize = _options.GroupRegistryPayloadSize,
            WorkerScheduler = _workerScheduler,
        };
        return _registryMetaFactory?.Invoke(_fs, settings) ?? new VersionedMetadata(_fs, settings, epoch: _epoch);
    }

    /// <summary>装配死信子队列（spec §7.3 定案③——独立 TierQueue 实例 {name}.dlq：单组、无嵌套）。
    /// Options.DeadLetter = null → 不装配（关闭档：达上限仅推进组位点 + 告警）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>死信子队列实例（null = 关闭档）。</returns>
    private async Task<TierQueue?> BuildDeadLetterAsync(CancellationToken ct)
    {
        if (_options.DeadLetter is not { } o) return null;
        var dlqOptions = new TierQueueOptions
        {
            QueueName = $"{_options.QueueName}.dlq",
            PageSize = o.PageSize,
            MemorySize = o.MemorySize,
            SegmentGrowthLimit = o.SegmentGrowthLimit,
            OverflowPolicy = _options.OverflowPolicy,   // 透传溢出：大 payload 死信也走溢出引擎
            MinOverflowSize = _options.MinOverflowSize,
            DeadLetter = null,   // 无嵌套（DLQ 自身的死信溢出 = 推进位点 + 告警）
        };
        var builder = new TierQueueBuilder(_fs, dlqOptions);
        if (_ringFactory is not null) builder.WithRingFactory(_ringFactory);
        if (_groupMetaFactory is not null) builder.WithGroupMetaFactory(_groupMetaFactory);
        if (_registryMetaFactory is not null) builder.WithRegistryMetaFactory(_registryMetaFactory);
        if (_delayedIndexFactory is not null) builder.WithDelayedIndexFactory(_delayedIndexFactory);
        if (_idempotencyIndexFactory is not null) builder.WithIdempotencyIndexFactory(_idempotencyIndexFactory);
        if (_storageOptionsFactory is not null) builder.WithStorageOptionsFactory(_storageOptionsFactory);
        if (_ringMetaPolicyFactory is not null) builder.WithMetaPolicyFactory(_ringMetaPolicyFactory);
        if (_epoch is not null) builder.WithEpoch(_epoch);
        if (_logger is not null) builder.WithLogger(_logger);
        return await builder.StartAsync(ct: ct).ConfigureAwait(false);
    }

    /// <summary>装配延迟就绪索引（spec §4——BTreeOfQueueKey，键 (dueTime, addr)，经
    /// <see cref="DelayedKeyResolver"/> 适配键空间）。Options.Delayed = null → 不装配（纯即时队列零结构开销）。
    /// 索引 Initialize 启动后台恢复（载帧/增量重放）；主队列恢复核心随后做延迟对账（清 + 重建崩溃窗口）。</summary>
    /// <param name="ring">已装配的数据 Ring（键解析器依赖 Ring Ready）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>延迟索引包装实例（null = 关闭档）。</returns>
    private ValueTask<DelayedIndex?> BuildDelayedIndexAsync(RingOfQueueKey ring, CancellationToken ct)
    {
        if (_options.Delayed is null) return ValueTask.FromResult<DelayedIndex?>(null);
        var settings = new BTreeIndexSettings(EngineOptions("delay", new StorageEngineOptions(
            $"{_options.QueueName}.delay", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            // 持久化策略透传（锚点帧后台 dump 间隔/增量阈值——测试可收紧；null = BTree 缺省——与字段缺省同值）
            PersistencePolicy = _options.Delayed.PersistencePolicy ?? new(),
            NodeSize = _options.Delayed.NodeSize,
            MinFillPercent = _options.Delayed.MinFillPercent,
            WorkerScheduler = _workerScheduler,
        };
        // ★ 构造零 IO——Initialize 延迟到主队列恢复核心（Ring 就绪后；resolver 依赖 Ring Ready）
        var resolver = new DelayedKeyResolver(ring);
        var comparer = new QueueKeyComparer();
        var index = _delayedIndexFactory?.Invoke(_fs, settings, _epoch, resolver, comparer)
            ?? new BTreeOfQueueKey(_fs, settings, epoch: _epoch, keyComparer: comparer, keyResolver: resolver);
        return ValueTask.FromResult<DelayedIndex?>(new DelayedIndex(index));
    }

    /// <summary>装配幂等生产索引（spec §6.4——HashOfQueueKey，键 (producerId, seq)）。
    /// ★ 判等闭环/恢复重放经 <see cref="IdemKeyResolver"/>（读 record envelope 的 pid/seq 槽——
    /// record key 是延迟标记位，两键空间分离）；Options.Idempotency = null → 不装配。</summary>
    /// <param name="ring">已装配的数据 Ring（键解析器依赖 Ring Ready）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>幂等索引实例（null = 关闭档）。</returns>
    private ValueTask<HashOfQueueKey?> BuildIdempotencyIndexAsync(RingOfQueueKey ring, CancellationToken ct)
    {
        if (_options.Idempotency is not { } o) return ValueTask.FromResult<HashOfQueueKey?>(null);
        var settings = new TC.Tier.Runtime.Structures.ProbingIndex.HashIndexSettings(
            EngineOptions("idempotency", new StorageEngineOptions($"{_options.QueueName}.idem", 4L << 20,
                enableSegmentation: true, preallocateFile: false)))
        {
            HashTableCapacity = o.HashTableCapacity,
            OverflowPoolCapacity = o.OverflowPoolCapacity,
            WorkerScheduler = _workerScheduler,
        };
        // ★ 构造零 IO——Initialize 延迟到主队列恢复核心（Ring 就绪后；resolver 依赖 Ring Ready）
        var resolver = new IdemKeyResolver(ring);
        var comparer = new QueueKeyComparer();
        var index = _idempotencyIndexFactory?.Invoke(_fs, settings, _epoch, resolver, comparer)
            ?? new HashOfQueueKey(_fs, settings, keyResolver: resolver, epoch: _epoch, keyComparer: comparer);
        return ValueTask.FromResult<HashOfQueueKey?>(index);
    }

    /// <summary>引擎选项变换（注入工厂优先；null = 原样 defaults）。</summary>
    /// <param name="componentName">组件名（ring/groups/dlq/delay/idem）。</param>
    /// <param name="defaults">由 Options 派生的默认 StorageEngineOptions。</param>
    /// <returns>变换后的引擎选项。</returns>
    private StorageEngineOptions EngineOptions(string componentName, StorageEngineOptions defaults)
        => _storageOptionsFactory?.Invoke(componentName, defaults)
            ?? defaults;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_ownershipTransferred) _queue?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownershipTransferred && _queue is not null)
            await _queue.DisposeAsync().ConfigureAwait(false);
    }
}
