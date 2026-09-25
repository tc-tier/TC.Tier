using TC.Tier.Contracts.Meta;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Collections;

/// <summary>数据 Ring 装配工厂（RingOfSetKey）。</summary>
public delegate RingOfSetKey TierSetRingFactory(IFileSystem fs, BlittableRingSettings settings);

/// <summary>点查索引装配工厂（HashOfSetKey）。</summary>
public delegate HashOfSetKey TierSetIndexFactory(
    IFileSystem fs, HashIndexSettings settings, RingOfSetKey ring,
    LightEpoch? epoch, IKeyResolver<SetKey> keyResolver, IKeyComparer<SetKey> keyComparer);

/// <summary>域账 meta 装配工厂（VersionedMetadata）。</summary>
public delegate VersionedMetadata TierSetWatermarkMetaFactory(
    IFileSystem fs, VersionedMetadataSettings settings);

/// <summary>TierSet 组件引擎选项变换器。</summary>
public delegate StorageEngineOptions TierSetStorageOptionsFactory(
    string componentName, StorageEngineOptions defaults);

/// <summary>
/// TierSet 构建者（tc-tier-collections-spec——四段式装配惯例同 TierHashBuilder：
/// Options 配置链 → Builder → StartAsync 一步到位；注入优先、回落默认）。
/// </summary>
public sealed class TierSetBuilder : IDisposable, IAsyncDisposable
{
    private readonly IFileSystem _fs;
    private SetOptions _options;

    // === 注入面（不注入回落默认）===
    private TierSetRingFactory? _ringFactory;
    private TierSetIndexFactory? _indexFactory;
    private TierSetWatermarkMetaFactory? _watermarkMetaFactory;
    private TierSetStorageOptionsFactory? _storageOptionsFactory;
    private MetaPolicyFactory<RingMetaHeader, RingMetaPayload>? _ringMetaPolicyFactory;
    private IKeyComparer<SetKey>? _indexComparer;
    private LightEpoch? _epoch;
    private IsolatedTaskScheduler? _workerScheduler;
    private ILogger? _logger;

    private TierSet? _set;
    private bool _ownershipTransferred;
    private int _started;

    /// <summary>构造（= 配置，零 IO）。</summary>
    public TierSetBuilder(IFileSystem fs, SetOptions options)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(options);
        _fs = fs;
        _options = options;
    }

    /// <summary>注入数据 Ring 装配工厂。</summary>
    public TierSetBuilder WithRingFactory(TierSetRingFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringFactory = factory;
        return this;
    }

    /// <summary>注入点查索引装配工厂。</summary>
    public TierSetBuilder WithIndexFactory(TierSetIndexFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _indexFactory = factory;
        return this;
    }

    /// <summary>注入域账 meta 装配工厂。</summary>
    public TierSetBuilder WithWatermarkMetaFactory(TierSetWatermarkMetaFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _watermarkMetaFactory = factory;
        return this;
    }

    /// <summary>注入各组件的 StorageEngineOptions 变换器。</summary>
    public TierSetBuilder WithStorageOptionsFactory(TierSetStorageOptionsFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _storageOptionsFactory = factory;
        return this;
    }

    /// <summary>注入 Ring meta 策略工厂（默认回落 Managed 模式——水位持久化必开）。</summary>
    public TierSetBuilder WithMetaPolicyFactory(MetaPolicyFactory<RingMetaHeader, RingMetaPayload> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringMetaPolicyFactory = factory;
        return this;
    }

    /// <summary>注入索引键比较器（默认 = SetKeyComparer 字段字典序）。</summary>
    public TierSetBuilder WithIndexComparer(IKeyComparer<SetKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        _indexComparer = comparer;
        return this;
    }

    /// <summary>注入共享 epoch（多结构同域惯例）。不注入 = 各结构自管。</summary>
    public TierSetBuilder WithEpoch(LightEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        _epoch = epoch;
        return this;
    }

    /// <summary>注入引擎 worker 调度器共享实例（#505）。</summary>
    public TierSetBuilder WithWorkerScheduler(IsolatedTaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _workerScheduler = scheduler;
        return this;
    }

    /// <summary>注入日志器。</summary>
    public TierSetBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// 一步到位：构建 + 恢复（Ring/域账 join + 域账载入 + 索引重放 + 对账）+ WaitForReady。
    /// </summary>
    /// <param name="hints">恢复 hints（无注入项——预留）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已恢复就绪的 TierSet 实例。</returns>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public async Task<TierSet> StartAsync(CollectionRecoveryHints hints = default, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("TierSetBuilder 只能成功启动一次（StartAsync）——已启动或正在启动");

        try
        {
            var ring = BuildRing();
            var index = BuildIndex(ring);             // 构造零 IO——Initialize 延迟到恢复核心
            var watermark = BuildWatermarkMeta();

            var set = new TierSet(ring, index, watermark, _fs, _options, _logger);
            set.Initialize(hints);
            await set.WaitForReadyAsync(ct).ConfigureAwait(false);

            _ownershipTransferred = true;
            _set = set;
            _logger?.LogInformation("TierSet started: {Name} tail={Tail}", _options.SetName, set.TailAddress);
            return set;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TierSetBuilder.StartAsync 启动失败——销毁重建（可重试 StartAsync）");
            try
            {
                if (_set is not null) await _set.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "TierSetBuilder.StartAsync: 启动失败后销毁旧 TierSet DisposeAsync 异常");
            }
            _set = null;
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    /// <summary>装配数据 Ring（{name}.set.ring——MetaPolicyKind.Managed 必开）。</summary>
    private RingOfSetKey BuildRing()
    {
        var settings = new BlittableRingSettings(EngineOptions("ring", new StorageEngineOptions(
            $"{_options.SetName}.set.ring", _options.SegmentGrowthLimit,
            enableSegmentation: true, preallocateFile: _fs is not TC.Tier.Core.IO.Mem.MemoryFileSystem, deleteOnClose: false)))
        {
            PageSize = _options.PageSize,
            MemorySize = _options.MemorySize,
            OverflowPolicy = _options.OverflowPolicy,
            MinOverflowSize = _options.MinOverflowSize,
            ColdReadRatio = _options.ColdReadRatio,
            // ★ 水位持久化（Settings 基类默认 Disabled=no-op——不显式开则重启丢水位回退假尾）
            MetaPolicyKind = MetaPolicyKind.Managed,
            WorkerScheduler = _workerScheduler,
        };
        return _ringFactory?.Invoke(_fs, settings)
            ?? new RingOfSetKey(settings, _fs, metaPolicyFactory: _ringMetaPolicyFactory,
                epoch: _epoch, logger: _logger);
    }

    /// <summary>装配点查索引（{name}.set.index——HashOfSetKey，record key 直读 resolver）。</summary>
    private HashOfSetKey BuildIndex(RingOfSetKey ring)
    {
        var settings = new HashIndexSettings(EngineOptions("index", new StorageEngineOptions(
            $"{_options.SetName}.set.index", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            WorkerScheduler = _workerScheduler,
        };
        var resolver = new SetKeyResolver(ring);
        var comparer = _indexComparer ?? new SetKeyComparer();
        return _indexFactory?.Invoke(_fs, settings, ring, _epoch, resolver, comparer)
            ?? new HashOfSetKey(_fs, settings, keyResolver: resolver, epoch: _epoch, keyComparer: comparer);
    }

    /// <summary>装配域账 meta（{name}.set.water——域账 keyed 块）。</summary>
    private VersionedMetadata BuildWatermarkMeta()
    {
        var settings = new VersionedMetadataSettings(EngineOptions("water", new StorageEngineOptions(
            $"{_options.SetName}.set.water", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            PayloadSize = CollectionsWatermarkDoc.HeaderSize,
            MaxPayloadSize = CollectionsWatermarkDoc.MaxPayloadSize(_options.DomainCapacity),
            WorkerScheduler = _workerScheduler,
        };
        return _watermarkMetaFactory?.Invoke(_fs, settings) ?? new VersionedMetadata(_fs, settings, epoch: _epoch);
    }

    /// <summary>引擎选项变换（注入工厂优先；null = 原样 defaults）。</summary>
    private StorageEngineOptions EngineOptions(string componentName, StorageEngineOptions defaults)
        => _storageOptionsFactory?.Invoke(componentName, defaults)
            ?? defaults;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_ownershipTransferred) _set?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownershipTransferred && _set is not null)
            await _set.DisposeAsync().ConfigureAwait(false);
    }
}
