namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// 通用纯追加日志基类（4 基类之一）——接入 <see cref="IStorageEngine"/>（逻辑地址=物理地址）。
/// <para>★ 心智模型（见 Structures-log-rewrite-design.md §0）：IO 底层 = 持久化的内存。
/// Log 不碰段/对齐/落盘细节，只在引擎地址空间上做 record 格式（codec）+ group commit 调度。</para>
/// <para>★ 核心能力（base.md §2）：per-entry 顺序追加（Append/AppendAsync）+ flush 屏障持久化
/// （group commit：Flush + meta.Commit）+ 截断（TruncatePrefix→ReclaimHead / TruncateSuffix→ReclaimTail）
/// + meta 持久化（ILogMetaPolicy 三策略）+ 跨结构 2PC 原子提交（ITransactionParticipant）。</para>
/// <para>★ 水位全部读引擎，Log 不自存（§0.6）：</para>
/// <para> - <see cref="BeginAddress"/> = engine.<see cref="IStorageEngine.MinAddress"/></para>
/// <para> - <see cref="TailAddress"/> = engine.<see cref="IStorageEngine.AllocatedTail"/></para>
/// <para> - "已 group commit 落盘"边界 = EntryLog 的 CommittedOffset（Log 自管，引擎不提供）</para>
/// <para>★ 变化点（base.md §3）：entry 头布局 abstract + 扫描游标 ILogCursor + 恢复走
/// LifecycleBase&lt;LogRecoveryHints&gt; 模板派生（LogRecovery&lt;TLogBase&gt;/RecoveryBase）
/// + meta 策略（构造期 ??= 命名委托装配）。热路径 Append non-virtual。</para>
/// <para>实现类（DeltaLog/EntryLog）只填 entry 布局 abstract，按场景取用基类能力子集。</para>
/// </summary>
public abstract partial class LogBase : LifecycleBase<LogRecoveryHints>, ITransactionParticipant
{
    private protected readonly StorageEngine _engine;

    /// <summary>meta 策略——构造期装配（构造=配置，Core 完整生命周期），永非 null 纯读。</summary>
    public IMetaPolicy<LogMetaHeader, LogMetaPayload> MetaPolicy { get; }

    private readonly LogCursorFactory<ILogCursor>? _cursorFactory;

    private readonly IFileSystem _fs;
    private readonly IMetaTransport? _metaTransport;

    /// <summary>Managed 模式的 meta 引擎（构造期 Create；启动在 OnInitializeBegin，就绪等待在恢复核心）。</summary>
    private readonly StorageEngine? _metaEngine;

    /// <summary>缓存的设置（默认 meta 装配读 MetaOpaqueBytes/MetaPolicyKind）。</summary>
    private readonly LogSettings _settings;

    /// <summary>opaque 脏标记——SetOpaqueMeta 置位，AppendMeta 落盘后清零；
    /// 零数据推进时 CommitCore 凭它仍提交 meta（纯 opaque 提交=完整块，用户裁定）。</summary>
    private protected bool _opaqueDirty;

    /// <summary>★ 2PC Abort 回退点：最近一次<b>已确认提交</b>对应的尾（ConfirmCommitted 推进，单调）。
    /// <para>语义：上一提交点——其后的全部追加属于当前事务窗口（标准 2PC WAL 契约）。Abort 据此
    /// TruncateSuffix 回退；Prepare 随 meta 持久化（PreparedTailAddress 字段）；恢复时从 meta 还原
    /// ——跨崩溃的悬干裁决依据。Empty = 无既有提交边界（首事务 Abort 不截断）。</para></summary>
    private protected LogicalAddress _txRollbackTail;


    private ILogCodec LogCodec { get; }

    /// <summary>★ 登记外部 opaque meta——stage 进 meta 策略缓冲，<b>随水位线落盘原子携带</b>（用户裁定）。
    /// <para>语义：opaque 是外部记录搭内部水位线的车——同一块、同一 CRC 原子持久化；不存在独立的
    ///   opaque 提交路径（旧 WriteOpaqueMeta 自拍 TailAddress 独立成块 = 并发水位回退 + 被内部
    ///   提交冲掉，已废除）。落盘时机归水位线提交链；需确定性持久化点调 <see cref="EntryLog.CommitAsync"/>
    ///   （一块 = 当前水位 + opaque）。</para>
    /// <para>⚠️ 写侧拦截：MetaPolicyKind=Disabled 抛 <see cref="InvalidOperationException"/>（禁用即报错，
    ///   不静默吞）；超 MetaOpaqueBytes 由策略抛 ArgumentException。</para></summary>
    public void SetOpaqueMeta(ReadOnlySpan<byte> data)
    {
        if (_settings.MetaPolicyKind == MetaPolicyKind.Disabled)
            throw new InvalidOperationException(
                "MetaPolicyKind=Disabled——未开启 meta 持久化，opaque 登记被拒（ReadOpaqueMeta 将恒为空）。"
                + "请配置 MetaPolicyKind=Managed/Transport，或移除 opaque 写入。");
        MetaPolicy.WritePayload(data);   // stage——下次水位 Commit 原子携带
        _opaqueDirty = true;
    }

    /// <summary>读外部 opaque meta（最近已提交块的 opaque；Empty = 无数据/未开启——读侧不抛，空即答案）。</summary>
    public ReadOnlySpan<byte> ReadOpaqueMeta()
        => MetaPolicy.ReadPayload();

    protected LogBase(ILogCodec codec,
        IFileSystem fs,
        LogSettings settings,
        IRecovery<LogRecoveryHints>? recovery = null,
        LogCursorFactory<ILogCursor>? cursorFactory = null,
        MetaPolicyFactory<LogMetaHeader, LogMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null,
        ILogger? logger = null)
        : base(recovery, logger)
    {
        LogCodec = codec;
        _settings = settings;

        LogPageSizeBits = settings.LogPageSizeBits;
        PageSize = 1 << LogPageSizeBits;
        PageSizeMask = PageSize - 1;

        _fs = fs;
        _metaTransport = metaTransport;
        _cursorFactory = cursorFactory;

        _engine = new StorageEngine(fs, settings.MainEngine);
        Resources.Add(_engine, ownership: ResourceOwnership.Owned);

        // ★ Managed meta 引擎构造期内联构建（纯 Create 零 IO——启动在 OnInitializeBegin，与主引擎并行）。
        if (settings.MetaPolicyKind == MetaPolicyKind.Managed)
        {
            var metaOptions = new StorageEngineOptions(
                    settings.MainEngine.EngineName + ".meta",
                    preallocateFile: false,
                    deleteOnClose: settings.MainEngine.DeleteOnClose)
                .WithSegment(Math.Max(4096, 1L << 20), enableSegmentation: false)
                .WithHints(settings.MainEngine.Hints);
            _metaEngine = new StorageEngine(_fs, metaOptions);
            // ★ meta 引擎进 Resources（owned）——ManagedMetaPolicy.Dispose 只释放自身 buffer，不管引擎
            Resources.Add(_metaEngine, "metaEngine");
            logger?.LogInformation("Managed meta engine created: {MetaEngineName}", _metaEngine.EngineName);
        }
        // ★ meta 策略构造期装配（构造=配置，Core 完整生命周期）：工厂注入优先，否则默认映射。
        metaPolicyFactory ??= CreateMetaPolicyDefault;   // 方法组——与注入工厂同为 MetaPolicyFactory 委托
        MetaPolicy = metaPolicyFactory(settings.MetaPolicyKind);
        Resources.Add(MetaPolicy, "metaPolicy");
    }

    protected override void OnInitializeBegin()
    {
        // ★ 双引擎并行启动（均非阻塞）：主引擎带恢复水位 hint；meta 引擎（Managed）自恢复。
        //   依赖引擎就绪的初始化（InitializeForWrites 用 SectorSize）在恢复核心头部——
        //   引擎后台恢复未完成时 SectorSize=0 → AlignedMemoryManager alignment 越界。
        _engine.Initialize();   // 水位线归结构层：引擎自恢复（结构水位走 meta/扫盘/hints，不下传）
        _metaEngine?.Initialize();
    }



    public LogicalAddress BeginAddress => _engine.MinAddress;

    public LogicalAddress TailAddress => GetCurrentWriteTail();

    internal LogicalAddress FlushedTail => _logicalTail;

    public int PageSize { get; }

    public int LogPageSizeBits { get; }

    internal int PageSizeMask { get; }


    private uint SectorSize => _engine.SectorSize;

    private int HeaderSize => LogCodec.HeaderSize;

    private int Alignment => LogCodec.Alignment;



    private protected void EnsureNotDisposed() => ThrowIfDisposed();




}
