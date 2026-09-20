using TC.Tier.Core.Logging;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Snapshot;

namespace TC.Tier.Products.Blob;

/// <summary>数据引擎装配工厂——Builder 内部调（fs + 由 Options 派生的 Settings → StreamSnapshot 实例）。
/// 不注入 = 默认装配（StreamSnapshot + meta Disabled——帧自描述，恢复 Backward 找帧尾）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">数据引擎装配规格（由 Options 派生）。</param>
/// <returns>装配完成的数据引擎实例。</returns>
public delegate StreamSnapshot BlobDataSnapshotFactory(IFileSystem fs, StreamSnapshotSettings settings);

/// <summary>对象表引擎装配工厂——不注入 = 默认装配（StreamSnapshot + meta Managed——表水位 O(1) 恢复）。</summary>
/// <param name="fs">文件系统抽象（介质面）。</param>
/// <param name="settings">表引擎装配规格（由 Options 派生）。</param>
/// <returns>装配完成的表引擎实例。</returns>
public delegate StreamSnapshot BlobTableSnapshotFactory(IFileSystem fs, StreamSnapshotSettings settings);

/// <summary>Blob 组件引擎选项变换器；可覆盖引擎名之外的 StorageEngineOptions 字段。</summary>
/// <param name="componentName">组件名（data/meta——日志/诊断用）。</param>
/// <param name="defaults">由 Options 派生的默认 StorageEngineOptions。</param>
/// <returns>变换后的 StorageEngineOptions（null 变换器 = 原样 defaults）。</returns>
public delegate StorageEngineOptions BlobStorageOptionsFactory(string componentName, StorageEngineOptions defaults);

/// <summary>
/// TierBlob 构建者（四段式装配惯例同 TierTimeSeriesBuilder：Options 配置链 → Builder → StartAsync 一步到位）。
/// <para>★ 组合设计思想（注入优先、回落默认）：零件规格全在 <see cref="TierBlobOptions"/>；
///   可替换件全走 With* 注入——数据/表引擎工厂、引擎选项变换器、logger；不注入 = 内部默认实现。</para>
/// <para>★ 启动状态机同 TierQueueBuilder：只能成功启动一次（重复抛）；失败可重试（销毁重建）。</para>
/// </summary>
public sealed class TierBlobBuilder : IDisposable, IAsyncDisposable
{
    private readonly IFileSystem _fs;
    private readonly TierBlobOptions _options;

    // === 注入面（不注入回落默认）===
    private BlobDataSnapshotFactory? _dataFactory;
    private BlobTableSnapshotFactory? _tableFactory;
    private BlobStorageOptionsFactory? _storageOptionsFactory;
    private ILogger? _logger;

    private TierBlob? _blob;
    private bool _ownershipTransferred;
    private int _started;   // 0=未启动 1=已启动/启动中（CAS 抢启动权）

    /// <summary>构造（= 配置，零 IO）。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">Blob 选项（空间名/几何/容量上限）。</param>
    /// <exception cref="ArgumentNullException">fs 或 options 为 null。</exception>
    public TierBlobBuilder(IFileSystem fs, TierBlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(options);
        _fs = fs;
        _options = options;
    }

    /// <summary>注入数据引擎装配工厂（自定义整件替换——恢复算法/meta 策略全参自控）。</summary>
    /// <param name="factory">数据引擎工厂。</param>
    /// <returns>TierBlobBuilder（链式）。</returns>
    public TierBlobBuilder WithDataSnapshotFactory(BlobDataSnapshotFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _dataFactory = factory;
        return this;
    }

    /// <summary>注入对象表引擎装配工厂。</summary>
    /// <param name="factory">表引擎工厂。</param>
    /// <returns>TierBlobBuilder（链式）。</returns>
    public TierBlobBuilder WithTableSnapshotFactory(BlobTableSnapshotFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _tableFactory = factory;
        return this;
    }

    /// <summary>注入各组件的 StorageEngineOptions 变换器。</summary>
    /// <param name="factory">引擎选项变换器。</param>
    /// <returns>TierBlobBuilder（链式）。</returns>
    public TierBlobBuilder WithStorageOptionsFactory(BlobStorageOptionsFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _storageOptionsFactory = factory;
        return this;
    }

    /// <summary>注入日志器。</summary>
    /// <param name="logger">日志器实例。</param>
    /// <returns>TierBlobBuilder（链式）。</returns>
    public TierBlobBuilder WithLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        return this;
    }

    /// <summary>
    /// 一步到位：构建 + 恢复（引擎 join + 表重放 + 对账）+ WaitForReady。
    /// </summary>
    /// <param name="hints">恢复 hints（无注入项——预留）。</param>
    /// <param name="ct">取消令牌——透传 WaitForReadyAsync。</param>
    /// <returns>已恢复就绪的 TierBlob 实例。</returns>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public async Task<TierBlob> StartAsync(BlobRecoveryHints hints = default, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("TierBlobBuilder 只能成功启动一次（StartAsync）——已启动或正在启动");

        try
        {
            var data = BuildDataSnapshot();
            var table = new BlobObjectTable(BuildTableSnapshot());

            // ★ 生命周期模板：TierBlob 构造（双引擎进资源组）→ Initialize（OnInitializeBegin 并行启动
            //   → 后台恢复核心 join + 表重放 + 对账）→ WaitForReady
            var blob = new TierBlob(data, table, _options, _logger);
            blob.Initialize(hints);
            await blob.WaitForReadyAsync(ct).ConfigureAwait(false);

            _ownershipTransferred = true;   // 所有权转移（Dispose 由 TierBlob 负责）
            _blob = blob;
            _logger?.LogInformation("TierBlob started: {Name} tail={Tail}", _options.BlobName, blob.Bytes);
            return blob;
        }
        catch (Exception ex)
        {
            // ★ 失败可重试：销毁重建（Failed 哲学——重试 = 新实例）
            _logger?.LogWarning(ex, "TierBlobBuilder.StartAsync 启动失败——销毁重建（可重试 StartAsync）");
            try
            {
                if (_blob is not null) await _blob.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "TierBlobBuilder.StartAsync: 启动失败后销毁旧 TierBlob DisposeAsync 异常");
            }
            _blob = null;
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    /// <summary>装配数据引擎：注入工厂优先；默认 = StreamSnapshot（meta Disabled——帧自描述）。</summary>
    private StreamSnapshot BuildDataSnapshot()
    {
        var settings = new StreamSnapshotSettings(EngineOptions("data", new StorageEngineOptions(
            $"{_options.BlobName}.blob.data", _options.SegmentGrowthLimit,
            enableSegmentation: true, preallocateFile: _fs is not TC.Tier.Core.IO.Mem.MemoryFileSystem,
            deleteOnClose: false)))
        {
            SessionBufferSize = _options.SessionBufferSize,
            // ★ 数据引擎 meta Disabled：对象帧自描述（footer magic 反扫找尾 O(1)），
            //   表是登记真相源——数据引擎水位持久化属 2PC 语义，TierBlob 单写通道下无悬干形态
            MetaPolicyKind = MetaPolicyKind.Disabled,
            WorkerScheduler = _options.WorkerScheduler,
        };
        return _dataFactory?.Invoke(_fs, settings) ?? new StreamSnapshot(_fs, settings);
    }

    /// <summary>装配表引擎：注入工厂优先；默认 = StreamSnapshot（meta Managed——表水位 O(1) 恢复 + 2PC 悬干裁决）。</summary>
    private StreamSnapshot BuildTableSnapshot()
    {
        var settings = new StreamSnapshotSettings(EngineOptions("meta", new StorageEngineOptions(
            $"{_options.BlobName}.blob.meta", _options.TableSegmentGrowthLimit,
            enableSegmentation: true, preallocateFile: false, deleteOnClose: false)))
        {
            SessionBufferSize = _options.TableSessionBufferSize,
            MetaPolicyKind = MetaPolicyKind.Managed,
            WorkerScheduler = _options.WorkerScheduler,
        };
        return _tableFactory?.Invoke(_fs, settings) ?? new StreamSnapshot(_fs, settings);
    }

    /// <summary>引擎选项变换（注入工厂优先；null = 原样 defaults；配置形态调度器在此应用——
    /// 共享实例形态经 Settings 直达）。</summary>
    private StorageEngineOptions EngineOptions(string componentName, StorageEngineOptions defaults)
    {
        var applied = _options.WorkerSchedulerOptions is { } scheduler
            ? defaults.WithWorkerScheduler(scheduler)
            : defaults;
        return _storageOptionsFactory?.Invoke(componentName, applied) ?? applied;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_ownershipTransferred) _blob?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownershipTransferred && _blob is not null)
            await _blob.DisposeAsync().ConfigureAwait(false);
    }
}
