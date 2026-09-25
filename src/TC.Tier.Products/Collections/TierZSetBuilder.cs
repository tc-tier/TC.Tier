using TC.Tier.Contracts.Meta;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Products.Collections;

/// <summary>数据 Ring 装配工厂（RingOfZSetKey）。</summary>
public delegate RingOfZSetKey TierZSetRingFactory(IFileSystem fs, BlittableRingSettings settings);

/// <summary>member 点查索引装配工厂（HashOfZSetKey）。</summary>
public delegate HashOfZSetKey TierZSetMemberIndexFactory(
    IFileSystem fs, HashIndexSettings settings, RingOfZSetKey ring,
    LightEpoch? epoch, IKeyResolver<ZSetKey> keyResolver, IKeyComparer<ZSetKey> keyComparer);

/// <summary>score 有序索引装配工厂（BTreeOfZScoreKey）。</summary>
public delegate BTreeOfZScoreKey TierZSetScoreIndexFactory(
    IFileSystem fs, BTreeIndexSettings settings,
    LightEpoch? epoch, IKeyResolver<ZScoreKey> keyResolver, IKeyComparer<ZScoreKey> keyComparer);

/// <summary>域账 meta 装配工厂（VersionedMetadata）。</summary>
public delegate VersionedMetadata TierZSetWatermarkMetaFactory(
    IFileSystem fs, VersionedMetadataSettings settings);

/// <summary>TierZSet 组件引擎选项变换器。</summary>
public delegate StorageEngineOptions TierZSetStorageOptionsFactory(
    string componentName, StorageEngineOptions defaults);

/// <summary>
/// TierZSet 构建者（tc-tier-collections-spec——四段式装配惯例同 TierHashBuilder：
/// Options 配置链 → Builder → StartAsync 一步到位；注入优先、回落默认）。
/// </summary>
public sealed class TierZSetBuilder : IDisposable, IAsyncDisposable
{
    private readonly IFileSystem _fs;
    private ZSetOptions _options;

    // === 注入面（不注入回落默认）===
    private TierZSetRingFactory? _ringFactory;
    private TierZSetMemberIndexFactory? _memberIndexFactory;
    private TierZSetScoreIndexFactory? _scoreIndexFactory;
    private TierZSetWatermarkMetaFactory? _watermarkMetaFactory;
    private TierZSetStorageOptionsFactory? _storageOptionsFactory;
    private MetaPolicyFactory<RingMetaHeader, RingMetaPayload>? _ringMetaPolicyFactory;
    private IKeyComparer<ZSetKey>? _memberIndexComparer;
    private IKeyComparer<ZScoreKey>? _scoreIndexComparer;
    private LightEpoch? _epoch;
    private IsolatedTaskScheduler? _workerScheduler;
    private ILogger? _logger;

    private TierZSet? _zset;
    private bool _ownershipTransferred;
    private int _started;

    /// <summary>构造（= 配置，零 IO）。</summary>
    public TierZSetBuilder(IFileSystem fs, ZSetOptions options)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(options);
        _fs = fs;
        _options = options;
    }

    /// <summary>注入数据 Ring 装配工厂。</summary>
    public TierZSetBuilder WithRingFactory(TierZSetRingFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringFactory = factory;
        return this;
    }

    /// <summary>注入 member 点查索引装配工厂。</summary>
    public TierZSetBuilder WithMemberIndexFactory(TierZSetMemberIndexFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _memberIndexFactory = factory;
        return this;
    }

    /// <summary>注入 score 有序索引装配工厂。</summary>
    public TierZSetBuilder WithScoreIndexFactory(TierZSetScoreIndexFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _scoreIndexFactory = factory;
        return this;
    }

    /// <summary>注入域账 meta 装配工厂。</summary>
    public TierZSetBuilder WithWatermarkMetaFactory(TierZSetWatermarkMetaFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _watermarkMetaFactory = factory;
        return this;
    }

    /// <summary>注入各组件的 StorageEngineOptions 变换器。</summary>
    public TierZSetBuilder WithStorageOptionsFactory(TierZSetStorageOptionsFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _storageOptionsFactory = factory;
        return this;
    }

    /// <summary>注入 Ring meta 策略工厂（默认回落 Managed 模式——水位持久化必开）。</summary>
    public TierZSetBuilder WithMetaPolicyFactory(MetaPolicyFactory<RingMetaHeader, RingMetaPayload> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringMetaPolicyFactory = factory;
        return this;
    }

    /// <summary>注入 member 点查索引键比较器（默认 = ZSetKeyComparer 字段字典序）。</summary>
    public TierZSetBuilder WithMemberIndexComparer(IKeyComparer<ZSetKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        _memberIndexComparer = comparer;
        return this;
    }

    /// <summary>注入 score 有序索引键比较器（默认 = ZScoreKeyComparer 字段字典序）。</summary>
    public TierZSetBuilder WithScoreIndexComparer(IKeyComparer<ZScoreKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        _scoreIndexComparer = comparer;
        return this;
    }

    /// <summary>注入共享 epoch（多结构同域惯例）。不注入 = 各结构自管。</summary>
    public TierZSetBuilder WithEpoch(LightEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        _epoch = epoch;
        return this;
    }

    /// <summary>注入引擎 worker 调度器共享实例（#505）。</summary>
    public TierZSetBuilder WithWorkerScheduler(IsolatedTaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _workerScheduler = scheduler;
        return this;
    }

    /// <summary>注入日志器。</summary>
    public TierZSetBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// 一步到位：构建 + 恢复（Ring/域账 join + 域账载入 + 双索引重放 + 对账）+ WaitForReady。
    /// </summary>
    /// <param name="hints">恢复 hints（无注入项——预留）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已恢复就绪的 TierZSet 实例。</returns>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public async Task<TierZSet> StartAsync(CollectionRecoveryHints hints = default, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("TierZSetBuilder 只能成功启动一次（StartAsync）——已启动或正在启动");

        try
        {
            var ring = BuildRing();
            var memberIndex = BuildMemberIndex(ring);   // 构造零 IO——Initialize 延迟到恢复核心
            var scoreIndex = BuildScoreIndex(ring);     // 同上
            var watermark = BuildWatermarkMeta();

            var zset = new TierZSet(ring, memberIndex, scoreIndex, watermark, _fs, _options, _logger);
            zset.Initialize(hints);
            await zset.WaitForReadyAsync(ct).ConfigureAwait(false);

            _ownershipTransferred = true;
            _zset = zset;
            _logger?.LogInformation("TierZSet started: {Name} tail={Tail}", _options.ZSetName, zset.TailAddress);
            return zset;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TierZSetBuilder.StartAsync 启动失败——销毁重建（可重试 StartAsync）");
            try
            {
                if (_zset is not null) await _zset.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "TierZSetBuilder.StartAsync: 启动失败后销毁旧 TierZSet DisposeAsync 异常");
            }
            _zset = null;
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    /// <summary>装配数据 Ring（{name}.zset.ring——MetaPolicyKind.Managed 必开）。</summary>
    private RingOfZSetKey BuildRing()
    {
        var settings = new BlittableRingSettings(EngineOptions("ring", new StorageEngineOptions(
            $"{_options.ZSetName}.zset.ring", _options.SegmentGrowthLimit,
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
            ?? new RingOfZSetKey(settings, _fs, metaPolicyFactory: _ringMetaPolicyFactory,
                epoch: _epoch, logger: _logger);
    }

    /// <summary>装配 member 点查索引（{name}.zset.index——HashOfZSetKey，record key 直读 resolver）。</summary>
    private HashOfZSetKey BuildMemberIndex(RingOfZSetKey ring)
    {
        var settings = new HashIndexSettings(EngineOptions("index", new StorageEngineOptions(
            $"{_options.ZSetName}.zset.index", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            WorkerScheduler = _workerScheduler,
        };
        var resolver = new ZSetKeyResolver(ring);
        var comparer = _memberIndexComparer ?? new ZSetKeyComparer();
        return _memberIndexFactory?.Invoke(_fs, settings, ring, _epoch, resolver, comparer)
            ?? new HashOfZSetKey(_fs, settings, keyResolver: resolver, epoch: _epoch, keyComparer: comparer);
    }

    /// <summary>装配 score 有序索引（{name}.zset.ordidx——BTreeOfZScoreKey，envelope 反解 resolver）。</summary>
    private BTreeOfZScoreKey BuildScoreIndex(RingOfZSetKey ring)
    {
        var settings = new BTreeIndexSettings(EngineOptions("ordidx", new StorageEngineOptions(
            $"{_options.ZSetName}.zset.ordidx", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            PersistencePolicy = _options.IndexPersistencePolicy ?? new(),
            NodeSize = _options.IndexNodeSize,
            WorkerScheduler = _workerScheduler,
        };
        var resolver = new ZScoreKeyResolver(ring);
        var comparer = _scoreIndexComparer ?? new ZScoreKeyComparer();
        return _scoreIndexFactory?.Invoke(_fs, settings, _epoch, resolver, comparer)
            ?? new BTreeOfZScoreKey(_fs, settings, epoch: _epoch, keyComparer: comparer, keyResolver: resolver);
    }

    /// <summary>装配域账 meta（{name}.zset.water——域账 keyed 块）。</summary>
    private VersionedMetadata BuildWatermarkMeta()
    {
        var settings = new VersionedMetadataSettings(EngineOptions("water", new StorageEngineOptions(
            $"{_options.ZSetName}.zset.water", 4L << 20,
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
        if (!_ownershipTransferred) _zset?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownershipTransferred && _zset is not null)
            await _zset.DisposeAsync().ConfigureAwait(false);
    }
}
