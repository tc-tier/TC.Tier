namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 构建者（三段式装配惯例同 StorageEngineBuilder：Options 配置链 → Builder →
/// StartAsync 一步到位——构建 + 恢复 + WaitForReady 一体）。
/// <para>★ 注入面开放（设计决策：委托注入开放——不注入回落默认，Meta 校验兜底不可绕过）。</para>
/// <para>★ 启动状态机：只能成功启动一次（重复抛）；失败可重试（销毁重建 EntryLog 实例，对齐
///   RecoveryBase"Failed 销毁重建"哲学）。</para>
/// </summary>
public sealed class TierWalBuilder : IDisposable, IAsyncDisposable
{
    private readonly IFileSystem _fs;
    private readonly TierWalOptions _options;
    private MetaPolicyFactory<LogMetaHeader, LogMetaPayload>? _metaPolicyFactory;
    private IMetaTransport? _metaTransport;
    private ICommitPolicy? _commitPolicy;
    private IAsyncTransferPersistence? _snapshotPersistence;   // ★ 快照传输注入面（Export/Import——未注入 = 单机不导出）
    private ILogger? _logger;

    private TierWal? _wal;
    private bool _ownershipTransferred;
    private int _started;   // 0=未启动 1=已启动/启动中（CAS 抢启动权）

    /// <summary>构造（= 配置，零 IO）。</summary>
    public TierWalBuilder(IFileSystem fs, TierWalOptions options)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(options);
        _fs = fs;
        _options = options;
    }

    /// <summary>注入自定义 meta 策略工厂（默认回落 Managed 模式；不注入 = 默认装配）。</summary>
    /// <param name="factory">meta 策略工厂（LogMetaHeader + LogMetaPayload）。</param>
    /// <returns>TierWalBuilder 实例（链式调用）。</returns>
    public TierWalBuilder WithMetaPolicyFactory(MetaPolicyFactory<LogMetaHeader, LogMetaPayload> factory)
    {
        _metaPolicyFactory = factory;
        return this;
    }

    /// <summary>注入 meta 传输（Transport 模式：外部/远程存储）。</summary>
    /// <param name="transport">meta 传输实例。</param>
    /// <returns>TierWalBuilder 实例（链式调用）。</returns>
    public TierWalBuilder WithMetaTransport(IMetaTransport transport)
    {
        _metaTransport = transport;
        return this;
    }

    /// <summary>注入组提交策略（默认 = Options 三维度策略）。</summary>
    /// <param name="policy">组提交策略实例。</param>
    /// <returns>TierWalBuilder 实例（链式调用）。</returns>
    public TierWalBuilder WithCommitPolicy(ICommitPolicy policy)
    {
        _commitPolicy = policy;
        return this;
    }

    /// <summary>
    /// 注入快照传输面（跨节点导出/导入——网络传输/远端对象存储/备份介质）。
    /// 未注入 = 单机形态：快照本地化（SnapshotAsync + 冷启动自动载入）开箱即用，Export/Import 抛。
    /// </summary>
    /// <param name="persistence">快照传输实例。</param>
    /// <returns>TierWalBuilder 实例（链式调用）。</returns>
    public TierWalBuilder WithSnapshotPersistence(IAsyncTransferPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _snapshotPersistence = persistence;
        return this;
    }

    /// <summary>注入日志器。</summary>
    /// <param name="logger">日志器实例。</param>
    /// <returns>TierWalBuilder 实例（链式调用）。</returns>
    public TierWalBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// 一步到位：构建 + 恢复（载水位/元数据）+ WaitForReady——异步优先，外部禁止直接 Initialize。
    /// </summary>
    /// <param name="hints">恢复 hints（注入已知水位）；default 让底层自恢复。</param>
    /// <param name="ct">取消令牌——透传 WaitForReadyAsync，取消等待恢复完成。</param>
    /// <returns>已恢复就绪的 TierWal 实例。</returns>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public async Task<TierWal> StartAsync(WalRecoveryHints hints = default, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("TierWalBuilder 只能成功启动一次（StartAsync）——已启动或正在启动");

        try
        {
            var settings = BuildSettings(_options);
            var stager = new OpaqueStager(_logger);
            var policy = new OpaqueStagingCommitPolicy(stager,
                _commitPolicy ?? new GroupCommitThresholdPolicy(_options));
            var log = new EntryLog(_fs, settings, policy,
                metaPolicyFactory: _metaPolicyFactory, metaTransport: _metaTransport);

            // ★ 生命周期模板：TierWal 构造（EntryLog + 镜像快照进资源组）→ Initialize（OnInitializeBegin 启动
            //   EntryLog 与快照恢复 → 后台恢复核心 join + opaque 解析 + 锚点重建 + 快照载入）→ WaitForReady
            var wal = new TierWal(log, _fs, _options, hints, _logger, _snapshotPersistence);
            stager.Attach(wal);
            wal.Initialize(hints);
            await wal.WaitForReadyAsync(ct).ConfigureAwait(false);

            _ownershipTransferred = true;   // 所有权转移（Dispose 由 TierWal 负责）
            _wal = wal;
            _logger?.LogInformation("TierWAL started: {Name} allocated={Allocated} persisted={Persisted}",
                _options.WalName, wal.AllocatedIndex, wal.PersistedIndex);
            return wal;
        }
        catch (Exception ex)
        {
            // ★ 失败可重试：销毁重建（Failed 哲学——重试 = 新实例）
            _logger?.LogWarning(ex, "TierWalBuilder.StartAsync 启动失败——销毁重建（可重试 StartAsync）");
            try
            {
                if (_wal is not null) await _wal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "TierWalBuilder.StartAsync: 启动失败后销毁旧 TierWal DisposeAsync 异常");
            }
            _wal = null;
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    private static EntryLogSettings BuildSettings(TierWalOptions options)
    {
        var engine = new StorageEngineOptions(options.WalName, options.SegmentGrowthLimit,
                enableSegmentation: true, preallocateFile: true, deleteOnClose: false)
            .WithHints(options.Hints);
        return new EntryLogSettings(engine)
        {
            // ★ 三维度映射（EntryLog 后台时间循环 + TierWAL 包装策略共用同一组阈值）：
            //   时间维度走 EntryLog 后台循环（StartEarlyCommitLoop），字节/条数维度由包装策略内联判定。
            CommitInterval = options.CommitInterval,
            MaxUnflushedBytes = options.MaxUnflushedBytes,
            MaxUnflushedCount = options.MaxUnflushedCount,
            MetaPolicyKind = options.MetaPolicyKind,
            // ★ TierWAL 必须显式配置 opaque 容量（Settings 基类默认 0 = 无 opaque 区，搭车通道不可用）
            MetaOpaqueBytes = options.MetaOpaqueBytes,
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_ownershipTransferred) _wal?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownershipTransferred && _wal is not null)
            await _wal.DisposeAsync().ConfigureAwait(false);
    }
}
