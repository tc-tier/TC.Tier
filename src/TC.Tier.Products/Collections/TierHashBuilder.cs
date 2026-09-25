using TC.Tier.Contracts.Meta;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Collections;

/// <summary>数据 Ring 装配工厂——Builder 内部调（fs + 由 Options 派生的 Settings → Ring 实例）。
/// 不注入 = 默认装配（new RingOfHashKey + 透传已注入的 metaPolicyFactory/epoch/logger）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">Ring 装配规格（由 Options 派生）。</param>
/// <returns>装配完成的数据 Ring 实例。</returns>
public delegate RingOfHashKey TierHashRingFactory(IFileSystem fs, BlittableRingSettings settings);

/// <summary>点查索引装配工厂——Builder 内部调（HashOfHashKey）。
/// 不注入 = 默认装配（new HashOfHashKey + 注入的 resolver/epoch/comparer）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">Hash 索引装配规格。</param>
/// <param name="ring">数据 Ring（resolver 依赖其 Ready）。</param>
/// <param name="epoch">共享读保护域（null = 各结构自管）。</param>
/// <param name="keyResolver">键解析器（record key 直读适配）。</param>
/// <param name="keyComparer">键比较器（HashKeyComparer）。</param>
/// <returns>装配完成的点查索引实例。</returns>
public delegate HashOfHashKey TierHashIndexFactory(
    IFileSystem fs, HashIndexSettings settings, RingOfHashKey ring,
    LightEpoch? epoch, IKeyResolver<HashKey> keyResolver, IKeyComparer<HashKey> keyComparer);

/// <summary>域账 meta 装配工厂——Builder 内部调（VersionedMetadata 实例）。
/// 不注入 = 默认装配（new VersionedMetadata）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">VersionedMetadata 装配规格。</param>
/// <returns>装配完成的域账 meta 实例。</returns>
public delegate VersionedMetadata TierHashWatermarkMetaFactory(
    IFileSystem fs, VersionedMetadataSettings settings);

/// <summary>TierHash 组件引擎选项变换器；可覆盖 hints、优化参数及全部 StorageEngineOptions 字段。</summary>
/// <param name="componentName">组件名（ring/index/water——日志/诊断用）。</param>
/// <param name="defaults">由 Options 派生的默认 StorageEngineOptions。</param>
/// <returns>变换后的 StorageEngineOptions（null 变换器 = 原样 defaults）。</returns>
public delegate StorageEngineOptions TierHashStorageOptionsFactory(
    string componentName, StorageEngineOptions defaults);

/// <summary>
/// TierHash 构建者（tc-tier-collections-spec §3——四段式装配惯例同 TierTimeSeriesBuilder：
/// Options 配置链 → Builder → StartAsync 一步到位）。
/// <para>★ 组合设计思想（注入优先、回落默认）：零件规格全在 <see cref="HashOptions"/>；
///   可替换件全走 With\* 注入——Ring 工厂、Ring meta 策略工厂、域账 meta 工厂、索引工厂、
///   共享 epoch（多结构同域惯例）、引擎选项变换器、worker 调度器、logger；不注入 = 内部默认实现。</para>
/// <para>★ 启动状态机同 TierQueueBuilder：只能成功启动一次（重复抛）；失败可重试（销毁重建）。</para>
/// </summary>
public sealed class TierHashBuilder : IDisposable, IAsyncDisposable
{
    private readonly IFileSystem _fs;
    private HashOptions _options;

    // === 注入面（不注入回落默认）===
    private TierHashRingFactory? _ringFactory;
    private TierHashIndexFactory? _indexFactory;
    private TierHashWatermarkMetaFactory? _watermarkMetaFactory;
    private TierHashStorageOptionsFactory? _storageOptionsFactory;
    private MetaPolicyFactory<RingMetaHeader, RingMetaPayload>? _ringMetaPolicyFactory;
    private IKeyComparer<HashKey>? _indexComparer;
    private LightEpoch? _epoch;
    private IsolatedTaskScheduler? _workerScheduler;   // 调度器共享注入（#505——多实例一组线程）
    private ILogger? _logger;

    private TierHash? _hash;
    private bool _ownershipTransferred;
    private int _started;   // 0=未启动 1=已启动/启动中（CAS 抢启动权）

    /// <summary>构造（= 配置，零 IO）。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">Hash 选项（实例名/几何/TTL/容量护栏）。</param>
    /// <exception cref="ArgumentNullException">fs 或 options 为 null。</exception>
    public TierHashBuilder(IFileSystem fs, HashOptions options)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(options);
        _fs = fs;
        _options = options;
    }

    /// <summary>注入数据 Ring 装配工厂（自定义整件替换）。不注入 = 默认装配
    /// （透传 WithMetaPolicyFactory/WithEpoch/WithLogger 到生成封闭类全参 ctor）。</summary>
    /// <param name="factory">Ring 工厂。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithRingFactory(TierHashRingFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringFactory = factory;
        return this;
    }

    /// <summary>注入点查索引装配工厂。</summary>
    /// <param name="factory">索引工厂。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithIndexFactory(TierHashIndexFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _indexFactory = factory;
        return this;
    }

    /// <summary>注入域账 meta 装配工厂（自定义持久化策略/meta 传输/恢复替换）。</summary>
    /// <param name="factory">域账 meta 工厂。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithWatermarkMetaFactory(TierHashWatermarkMetaFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _watermarkMetaFactory = factory;
        return this;
    }

    /// <summary>注入各组件的 StorageEngineOptions 变换器。</summary>
    /// <param name="factory">引擎选项变换器。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithStorageOptionsFactory(TierHashStorageOptionsFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _storageOptionsFactory = factory;
        return this;
    }

    /// <summary>注入 Ring meta 策略工厂（默认回落 Managed 模式——水位持久化；Disabled 下重启丢水位回退引擎假尾）。</summary>
    /// <param name="factory">meta 策略工厂。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithMetaPolicyFactory(MetaPolicyFactory<RingMetaHeader, RingMetaPayload> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringMetaPolicyFactory = factory;
        return this;
    }

    /// <summary>注入索引键比较器（默认 = <see cref="HashKeyComparer"/> 字段字典序）。</summary>
    /// <param name="comparer">键比较器。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithIndexComparer(IKeyComparer<HashKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        _indexComparer = comparer;
        return this;
    }

    /// <summary>注入共享 epoch（组合域多结构共用同一读保护域的惯例）。不注入 = 各结构自管。</summary>
    /// <param name="epoch">共享 epoch 实例。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithEpoch(LightEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        _epoch = epoch;
        return this;
    }

    /// <summary>注入引擎 worker 调度器共享实例（#505——ring/索引/域账 meta 全部引擎共用一组线程；
    /// 所有权 Referenced 归注入方。不注入 = 各引擎按 StorageOptionsFactory 配置自建）。</summary>
    /// <param name="scheduler">共享调度器实例（如 <see cref="IsolatedTaskScheduler.Shared"/>）。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithWorkerScheduler(IsolatedTaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _workerScheduler = scheduler;
        return this;
    }

    /// <summary>注入日志器。</summary>
    /// <param name="logger">日志器实例。</param>
    /// <returns>TierHashBuilder（链式）。</returns>
    public TierHashBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// 一步到位：构建 + 恢复（Ring/域账 join + 域账载入 + 索引重放 + 对账）+ WaitForReady。
    /// </summary>
    /// <param name="hints">恢复 hints（无注入项——预留）。</param>
    /// <param name="ct">取消令牌——透传 WaitForReadyAsync。</param>
    /// <returns>已恢复就绪的 TierHash 实例。</returns>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public async Task<TierHash> StartAsync(CollectionRecoveryHints hints = default, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("TierHashBuilder 只能成功启动一次（StartAsync）——已启动或正在启动");

        try
        {
            var ring = BuildRing();
            var index = BuildIndex(ring);             // 构造零 IO——Initialize 延迟到恢复核心（Ring 就绪后）
            var watermark = BuildWatermarkMeta();     // 构造零 IO——Initialize 延迟到恢复核心

            var hash = new TierHash(ring, index, watermark, _fs, _options, _logger);
            hash.Initialize(hints);
            await hash.WaitForReadyAsync(ct).ConfigureAwait(false);

            _ownershipTransferred = true;   // 所有权转移（Dispose 由 TierHash 负责）
            _hash = hash;
            _logger?.LogInformation("TierHash started: {Name} tail={Tail}", _options.HashName, hash.TailAddress);
            return hash;
        }
        catch (Exception ex)
        {
            // ★ 失败可重试：销毁重建（Failed 哲学——重试 = 新实例）
            _logger?.LogWarning(ex, "TierHashBuilder.StartAsync 启动失败——销毁重建（可重试 StartAsync）");
            try
            {
                if (_hash is not null) await _hash.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "TierHashBuilder.StartAsync: 启动失败后销毁旧 TierHash DisposeAsync 异常");
            }
            _hash = null;
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    /// <summary>装配数据 Ring（{name}.hash.ring——水位持久化 MetaPolicyKind.Managed 必开）。</summary>
    /// <returns>装配完成的数据 Ring 实例。</returns>
    private RingOfHashKey BuildRing()
    {
        var settings = new BlittableRingSettings(EngineOptions("ring", new StorageEngineOptions(
            $"{_options.HashName}.hash.ring", _options.SegmentGrowthLimit,
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
        return _ringFactory?.Invoke(_fs, settings)
            ?? new RingOfHashKey(settings, _fs, metaPolicyFactory: _ringMetaPolicyFactory,
                epoch: _epoch, logger: _logger);
    }

    /// <summary>装配点查索引（{name}.hash.index——HashOfHashKey，record key 直读 resolver）。</summary>
    /// <param name="ring">已装配的数据 Ring。</param>
    /// <returns>点查索引实例。</returns>
    private HashOfHashKey BuildIndex(RingOfHashKey ring)
    {
        var settings = new HashIndexSettings(EngineOptions("index", new StorageEngineOptions(
            $"{_options.HashName}.hash.index", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            WorkerScheduler = _workerScheduler,
        };
        var resolver = new HashKeyResolver(ring);
        var comparer = _indexComparer ?? new HashKeyComparer();
        return _indexFactory?.Invoke(_fs, settings, ring, _epoch, resolver, comparer)
            ?? new HashOfHashKey(_fs, settings, keyResolver: resolver, epoch: _epoch, keyComparer: comparer);
    }

    /// <summary>装配域账 meta（{name}.hash.water——域账 keyed 块；payload 分区 = BlockCount 4B +
    /// n × 28B，变长写入上限 = DomainCapacity 满配尺寸）。</summary>
    /// <returns>装配完成的域账 meta 实例。</returns>
    private VersionedMetadata BuildWatermarkMeta()
    {
        var settings = new VersionedMetadataSettings(EngineOptions("water", new StorageEngineOptions(
            $"{_options.HashName}.hash.water", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            PayloadSize = CollectionsWatermarkDoc.HeaderSize,
            MaxPayloadSize = CollectionsWatermarkDoc.MaxPayloadSize(_options.DomainCapacity),
            WorkerScheduler = _workerScheduler,
        };
        return _watermarkMetaFactory?.Invoke(_fs, settings) ?? new VersionedMetadata(_fs, settings, epoch: _epoch);
    }

    /// <summary>引擎选项变换（注入工厂优先；null = 原样 defaults）。</summary>
    /// <param name="componentName">组件名（ring/index/water）。</param>
    /// <param name="defaults">由 Options 派生的默认 StorageEngineOptions。</param>
    /// <returns>变换后的引擎选项。</returns>
    private StorageEngineOptions EngineOptions(string componentName, StorageEngineOptions defaults)
        => _storageOptionsFactory?.Invoke(componentName, defaults)
            ?? defaults;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_ownershipTransferred) _hash?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownershipTransferred && _hash is not null)
            await _hash.DisposeAsync().ConfigureAwait(false);
    }
}
