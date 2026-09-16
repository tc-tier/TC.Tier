using TC.Tier.Contracts.Meta;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Logging;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Core.Primitives;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Products.TimeSeries;

/// <summary>数据 Ring 装配工厂——Builder 内部调（fs + 由 Options 派生的 Settings → Ring 实例）。
/// 不注入 = 默认装配（new RingOfTimeKey + 透传已注入的 metaPolicyFactory/epoch/logger）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">Ring 装配规格（由 Options 派生）。</param>
/// <returns>装配完成的数据 Ring 实例。</returns>
public delegate RingOfTimeKey TimeSeriesRingFactory(
    IFileSystem fs, BlittableRingSettings settings);

/// <summary>水位 meta 装配工厂——Builder 内部调（fs + 由 Options 派生的 Settings → VersionedMetadata 实例）。
/// 不注入 = 默认装配（new VersionedMetadata）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">VersionedMetadata 装配规格。</param>
/// <returns>装配完成的水位 meta 实例。</returns>
public delegate VersionedMetadata TimeSeriesWatermarkMetaFactory(
    IFileSystem fs, VersionedMetadataSettings settings);

/// <summary>时间索引装配工厂。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">BTree 索引装配规格。</param>
/// <param name="epoch">共享读保护域（null = 各结构自管）。</param>
/// <param name="keyResolver">键解析器（读 record envelope 反解 ts）。</param>
/// <param name="keyComparer">键比较器（TimeKeyComparer）。</param>
/// <returns>装配完成的时间 BTree 索引实例。</returns>
public delegate BTreeOfTimeKey TimeSeriesIndexFactory(
    IFileSystem fs, BTreeIndexSettings settings, LightEpoch? epoch,
    IKeyResolver<TimeKey> keyResolver, IKeyComparer<TimeKey> keyComparer);

/// <summary>TimeSeries 组件引擎选项变换器；可覆盖 hints、优化参数及全部 StorageEngineOptions 字段。</summary>
/// <param name="componentName">组件名（ring/index/water——日志/诊断用）。</param>
/// <param name="defaults">由 Options 派生的默认 StorageEngineOptions。</param>
/// <returns>变换后的 StorageEngineOptions（null 变换器 = 原样 defaults）。</returns>
public delegate StorageEngineOptions TimeSeriesStorageOptionsFactory(
    string componentName, StorageEngineOptions defaults);

/// <summary>
/// TierTimeSeries 构建者（tc-tier-timeseries-spec §3——四段式装配惯例同 TierQueueBuilder：
/// Options 配置链 → Builder → StartAsync 一步到位）。
/// <para>★ 组合设计思想（注入优先、回落默认）：零件规格全在 <see cref="TimeSeriesOptions"/>；
///   可替换件全走 With\* 注入——Ring 工厂、Ring meta 策略工厂、水位 meta 工厂、索引工厂、
///   索引比较器、共享 epoch（多结构同域惯例）、引擎选项变换器、logger；不注入 = 内部默认实现。</para>
/// <para>★ 启动状态机同 TierQueueBuilder：只能成功启动一次（重复抛）；失败可重试（销毁重建，
///   对齐 RecoveryBase "Failed 销毁重建" 哲学）。</para>
/// </summary>
public sealed class TierTimeSeriesBuilder : IDisposable, IAsyncDisposable
{
    private readonly IFileSystem _fs;
    private TimeSeriesOptions _options;

    // === 注入面（不注入回落默认）===
    private TimeSeriesRingFactory? _ringFactory;
    private TimeSeriesWatermarkMetaFactory? _watermarkMetaFactory;
    private TimeSeriesIndexFactory? _indexFactory;
    private TimeSeriesStorageOptionsFactory? _storageOptionsFactory;
    private MetaPolicyFactory<RingMetaHeader, RingMetaPayload>? _ringMetaPolicyFactory;
    private IKeyComparer<TimeKey>? _indexComparer;
    private LightEpoch? _epoch;
    private ILogger? _logger;

    private TierTimeSeries? _series;
    private bool _ownershipTransferred;
    private int _started;   // 0=未启动 1=已启动/启动中（CAS 抢启动权）

    /// <summary>构造（= 配置，零 IO）。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TimeSeries 选项（序列名/几何/retention/索引开关）。</param>
    /// <exception cref="ArgumentNullException">fs 或 options 为 null。</exception>
    public TierTimeSeriesBuilder(IFileSystem fs, TimeSeriesOptions options)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(options);
        _fs = fs;
        _options = options;
    }

    /// <summary>注入数据 Ring 装配工厂（自定义整件替换——恢复算法/扫描游标/快照/meta 策略全参自控）。
    /// 不注入 = 默认装配（透传 WithMetaPolicyFactory/WithEpoch/WithLogger 到生成封闭类全参 ctor）。</summary>
    /// <param name="factory">Ring 工厂。</param>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithRingFactory(TimeSeriesRingFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringFactory = factory;
        return this;
    }

    /// <summary>注入水位 meta 装配工厂（自定义持久化策略/meta 传输/恢复替换）。
    /// 不注入 = 默认装配（new VersionedMetadata）。</summary>
    /// <param name="factory">水位 meta 工厂。</param>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithWatermarkMetaFactory(TimeSeriesWatermarkMetaFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _watermarkMetaFactory = factory;
        return this;
    }

    /// <summary>注入时间 BTree 工厂。</summary>
    /// <param name="factory">时间索引工厂。</param>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithIndexFactory(TimeSeriesIndexFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _indexFactory = factory;
        return this;
    }

    /// <summary>注入索引键比较器（默认 = <see cref="TimeKeyComparer"/> 字段字典序）。</summary>
    /// <param name="comparer">键比较器。</param>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithIndexComparer(IKeyComparer<TimeKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        _indexComparer = comparer;
        return this;
    }

    /// <summary>注入各组件的 StorageEngineOptions 变换器。</summary>
    /// <param name="factory">引擎选项变换器。</param>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithStorageOptionsFactory(TimeSeriesStorageOptionsFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _storageOptionsFactory = factory;
        return this;
    }

    /// <summary>注入 Ring meta 策略工厂（默认回落 Managed 模式——水位持久化，P0 已证实必需：
    /// Disabled 下重启丢水位回退引擎假尾）。</summary>
    /// <param name="factory">meta 策略工厂。</param>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithMetaPolicyFactory(MetaPolicyFactory<RingMetaHeader, RingMetaPayload> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ringMetaPolicyFactory = factory;
        return this;
    }

    /// <summary>注入共享 epoch（组合域多结构共用同一读保护域的惯例——Ring 与索引同域时注入）。
    /// 不注入 = 各结构自管。</summary>
    /// <param name="epoch">共享 epoch 实例。</param>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithEpoch(LightEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        _epoch = epoch;
        return this;
    }

    /// <summary>注入日志器。</summary>
    /// <param name="logger">日志器实例。</param>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>启用稠密多序列模式（#443 设计稿——等价 <c>Options.DenseSeries = true</c> 链式糖）。
    /// dense：DenseTimeKey（20B）+ TTS2 envelope + 单树键域化 + 水位 keyed 块化；序列容量护栏 =
    /// <see cref="TimeSeriesOptions.SeriesCapacity"/>（缺省 2²⁰）。</summary>
    /// <returns>TierTimeSeriesBuilder（链式）。</returns>
    public TierTimeSeriesBuilder WithDenseSeries()
    {
        _options = _options with { DenseSeries = true };
        return this;
    }

    /// <summary>
    /// 一步到位：构建 + 恢复（Ring/meta join + 水位载入 + 索引启动 + 对账）+ WaitForReady。
    /// </summary>
    /// <param name="hints">恢复 hints（无注入项——预留）。</param>
    /// <param name="ct">取消令牌——透传 WaitForReadyAsync。</param>
    /// <returns>已恢复就绪的 TierTimeSeries 实例。</returns>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public async Task<TierTimeSeries> StartAsync(TimeSeriesRecoveryHints hints = default, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("TierTimeSeriesBuilder 只能成功启动一次（StartAsync）——已启动或正在启动");

        try
        {
            var keys = BuildKeySpace();               // 键域（单序列/dense——键类型与封闭类型封在其内）
            var watermark = BuildWatermarkMeta();     // 构造零 IO——Initialize 延迟到恢复核心（Ring 就绪后）

            // ★ 生命周期模板：TierTimeSeries 构造（三件套进资源组）→ Initialize
            //   （OnInitializeBegin 并行启动 Ring/水位 → 后台恢复核心 join + 水位载入 + 索引对账 +
            //   retention worker 装配）→ WaitForReady
            var series = new TierTimeSeries(keys, watermark, _fs, _options, _logger);
            series.Initialize(hints);
            await series.WaitForReadyAsync(ct).ConfigureAwait(false);

            _ownershipTransferred = true;   // 所有权转移（Dispose 由 TierTimeSeries 负责）
            _series = series;
            _logger?.LogInformation("TierTimeSeries started: {Name} tail={Tail}",
                _options.SeriesName, series.TailAddress);
            return series;
        }
        catch (Exception ex)
        {
            // ★ 失败可重试：销毁重建（Failed 哲学——重试 = 新实例）
            _logger?.LogWarning(ex, "TierTimeSeriesBuilder.StartAsync 启动失败——销毁重建（可重试 StartAsync）");
            try
            {
                if (_series is not null) await _series.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "TierTimeSeriesBuilder.StartAsync: 启动失败后销毁旧 TierTimeSeries DisposeAsync 异常");
            }
            _series = null;
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    /// <summary>键域装配（按 Options.DenseSeries 分支——两模式互不读对方文件）：
    /// dense = RingOfDenseTimeKey + BTreeOfDenseTimeKey + TTS2 resolver；单序列 = 既有装配。
    /// ★ WithRingFactory/WithIndexFactory/WithIndexComparer 注入件为 TimeKey 封闭类型——仅单序列模式生效。</summary>
    /// <returns>键域实例（Ring/索引结构归其持有——生命周期经键域编排）。</returns>
    private ITimeSeriesKeySpace BuildKeySpace()
    {
        if (_options.DenseSeries)
        {
            var ring = BuildDenseRing();
            var resolver = new DenseTimeKeyResolver(ring);
            var index = BuildDenseIndex(ring, resolver);   // 构造零 IO——Initialize 延迟到恢复核心（Ring 就绪后）
            return new DenseSeriesKeySpace(ring, index);
        }
        var ring16 = BuildRing();
        var resolver16 = new TimeKeyResolver(ring16);
        var index16 = BuildIndex(ring16, resolver16);
        return new SingleSeriesKeySpace(ring16, index16);
    }

    /// <summary>装配数据 Ring：注入工厂优先；默认 = 生成封闭类全参 ctor（透传 meta 策略/epoch/logger）。</summary>
    /// <returns>装配完成的数据 Ring 实例。</returns>
    private RingOfTimeKey BuildRing()
    {
        var settings = new BlittableRingSettings(EngineOptions("ring", new StorageEngineOptions(
            $"{_options.SeriesName}.ts.ring", _options.SegmentGrowthLimit,
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
        };
        if (_ringFactory is { } f) return f(_fs, settings);
        return new RingOfTimeKey(settings, _fs, metaPolicyFactory: _ringMetaPolicyFactory,
            epoch: _epoch, logger: _logger);
    }

    /// <summary>装配 dense 数据 Ring（RingOfDenseTimeKey——Settings 与单序列同几何派生）。</summary>
    /// <returns>装配完成的 dense 数据 Ring 实例。</returns>
    private RingOfDenseTimeKey BuildDenseRing()
    {
        var settings = new BlittableRingSettings(EngineOptions("ring", new StorageEngineOptions(
            $"{_options.SeriesName}.ts.ring", _options.SegmentGrowthLimit,
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
        };
        return new RingOfDenseTimeKey(settings, _fs, metaPolicyFactory: _ringMetaPolicyFactory,
            epoch: _epoch, logger: _logger);
    }

    /// <summary>装配 dense 时间索引（BTreeOfDenseTimeKey——键 (sid, ts, addr.Offset)，逐序列 = 键前缀域）。</summary>
    /// <param name="ring">已装配的 dense 数据 Ring。</param>
    /// <param name="resolver">envelope (sid, ts) 解析器（与恢复对账共用同一实例）。</param>
    /// <returns>时间索引实例（null = 降档）。</returns>
    private BTreeOfDenseTimeKey? BuildDenseIndex(RingOfDenseTimeKey ring, DenseTimeKeyResolver resolver)
    {
        if (!_options.Indexed) return null;
        var settings = new BTreeIndexSettings(EngineOptions("index", new StorageEngineOptions(
            $"{_options.SeriesName}.ts.index", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            PersistencePolicy = _options.IndexPersistencePolicy ?? new(),
            NodeSize = _options.IndexNodeSize,
        };
        return new BTreeOfDenseTimeKey(_fs, settings, epoch: _epoch,
            keyComparer: new DenseTimeKeyComparer(), keyResolver: resolver);
    }

    /// <summary>装配水位 meta（{name}.ts.water——TrimmedUntil/TrimmedAddress/SampleCount/Epoch 版本链）。
    /// dense：payload 分区 = 默认槽（40B）+ BlockCount（4B）+ n × 36B 稀疏块——变长写入上限 =
    /// SeriesCapacity 满配尺寸（热区按需增长，护栏内不预支）。</summary>
    /// <returns>装配完成的水位 meta 实例。</returns>
    private VersionedMetadata BuildWatermarkMeta()
    {
        var settings = new VersionedMetadataSettings(EngineOptions("water", new StorageEngineOptions(
            $"{_options.SeriesName}.ts.water", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            PayloadSize = TimeSeriesWatermarkState.PayloadSize,
            MaxPayloadSize = _options.DenseSeries
                ? DenseSeriesWatermarkDoc.HeaderSize + checked((int)_options.SeriesCapacity) * DenseSeriesWatermarkBlock.BlockSize
                : null,
        };
        return _watermarkMetaFactory?.Invoke(_fs, settings) ?? new VersionedMetadata(_fs, settings, epoch: _epoch);
    }

    /// <summary>装配时间索引（spec §1——BTreeOfTimeKey，键 (ts, addr.Offset)，经
    /// <see cref="TimeKeyResolver"/> 适配键空间）。Options.Indexed = false → 不装配（纯追加降档——定案⑨）。</summary>
    /// <param name="ring">已装配的数据 Ring。</param>
    /// <param name="resolver">envelope ts 解析器（与恢复对账共用同一实例）。</param>
    /// <returns>时间索引实例（null = 降档）。</returns>
    private BTreeOfTimeKey? BuildIndex(RingOfTimeKey ring, TimeKeyResolver resolver)
    {
        if (!_options.Indexed) return null;
        var settings = new BTreeIndexSettings(EngineOptions("index", new StorageEngineOptions(
            $"{_options.SeriesName}.ts.index", 4L << 20,
            enableSegmentation: true, preallocateFile: false)))
        {
            // 持久化策略透传（锚点帧后台 dump 间隔/增量阈值——测试可收紧；null = BTree 缺省——与字段缺省同值）
            PersistencePolicy = _options.IndexPersistencePolicy ?? new(),
            NodeSize = _options.IndexNodeSize,
        };
        var comparer = _indexComparer ?? new TimeKeyComparer();
        return _indexFactory?.Invoke(_fs, settings, _epoch, resolver, comparer)
            ?? new BTreeOfTimeKey(_fs, settings, epoch: _epoch, keyComparer: comparer, keyResolver: resolver);
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
        if (!_ownershipTransferred) _series?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownershipTransferred && _series is not null)
            await _series.DisposeAsync().ConfigureAwait(false);
    }
}
