namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>
/// 快照基类——GB/TB 级大数据流（snapshot/大快照）的读写存储。
/// 引擎无限地址空间 + Allocate 预留 + 双缓冲流式会话 + Backward 扫描找帧尾 + meta O(1) 恢复。
/// <para>★ 与版本链族（Metadata/Mirror）根本不同——<b>无版本链</b>：append 可回滚（尾截断），
///   Overwrite 不可回滚（覆写已破坏旧数据，独立方法名显形）。无内存工作副本、无 epoch。</para>
/// <para>★ 三水位：WriteAddress（逻辑写尾）/ PhysicalWriteAddress（物理写尾，扇区对齐）/
///   TruncatedAddress（逻辑截断点）。</para>
/// <para>★ 生命周期：继承 <see cref="LifecycleBase{THints}"/>——Initialize 同步 void 启动后台恢复，
///   WaitForReady 等就绪（详见 src/TC.Tier.Core/docs/lifecycle.md）。恢复走 RecoveryBase 模板派生。</para>
/// </summary>
public abstract partial class SnapshotBase : LifecycleBase<SnapshotRecoveryHints>, ITransactionParticipant
{
    /// <summary>主数据引擎（无限地址空间）。</summary>
    private protected readonly StorageEngine _engine;

    /// <summary>组合根注入的文件系统（主引擎与 Managed meta 引擎共用）。</summary>
    private readonly IFileSystem _fs;

    /// <summary>meta 传输（Transport 模式用）——默认装配的 Transport 回落取用。</summary>
    private readonly IMetaTransport? _metaTransport;

    /// <summary>Managed 模式的 meta 引擎（构造期 Create；启动在 OnInitializeBegin，就绪等待在恢复核心）。</summary>
    private readonly StorageEngine? _metaEngine;

    /// <summary>meta 引擎访问器（Managed 模式——派生结构恢复核心 join 用）。</summary>
    private protected StorageEngine? MetaEngine => _metaEngine;

    /// <summary>流式帧 codec（Header + Payload + Footer）。</summary>
    private protected readonly ISnapshotCodec _codec;

    // === 配置 ===
    private readonly SnapshotSettings _settings;
    private readonly int _sectorSize;
    private readonly int _sessionBufferSize;

    // === 三水位（LogicalAddress；恢复路径按 meta/扫盘覆盖）===
    /// <summary>逻辑写尾（非对齐）。</summary>
    private protected LogicalAddress _writeAddress;

    /// <summary>物理写尾（扇区对齐，DIO 写入基准）。</summary>
    private protected LogicalAddress _physicalWriteAddress;

    /// <summary>逻辑截断点。</summary>
    private protected LogicalAddress _truncatedAddress;

    /// <summary>★ Abort 回退点：ConfirmCommitted 时的 WriteAddress（append 原子性边界）。</summary>
    private protected LogicalAddress _committedWriteAddress;

    /// <summary>剩余可写窗口（引擎 Write 要求目标在已分配区内——攒窗口批量 Allocate）。</summary>
    private protected long _writeWindow;

    // === 2PC ===
    private protected long _lastCommittedSeq = -1;
    private protected long _lastPreparedSeq = -1;
    private protected long _lastAbortedSeq = -1;
    private readonly SortedList<long, List<Action>> _txCallbacks = new();
    private readonly object _txCallbackLock = new();

    /// <summary>
    /// 构造（protected，子类继承）。引擎 = 构造期配置（对齐 LogBase/MetadataBase/MirrorBase）。
    /// </summary>
    /// <param name="codec">流式帧 codec（构造第一参数）。</param>
    /// <param name="fs">组合根文件系统（TierFs 构造的 IFileSystem）。</param>
    /// <param name="settings">快照设置。</param>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="metaPolicyFactory">可选的 meta 策略工厂。</param>
    /// <param name="metaTransport">可选的 meta 传输（Transport 模式用）。</param>
    /// <param name="logger">可选的日志记录器实例。</param>
    protected SnapshotBase(
        ISnapshotCodec codec,
        IFileSystem fs,
        SnapshotSettings settings,
        IRecovery<SnapshotRecoveryHints>? recovery = null,
        MetaPolicyFactory<SnapshotMetaHeader, SnapshotMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null,
        ILogger? logger = null)
        : base(recovery, logger)
    {
        _codec = codec;
        _fs = fs;
        _settings = settings;
        _metaTransport = metaTransport;

        _engine = new StorageEngine(fs, settings.MainEngine);
        Resources.Add(_engine, ownership: ResourceOwnership.Owned);
        _sectorSize = (int)_engine.SectorSize;
        _sessionBufferSize = settings.SessionBufferSize;
        // 水位初值 = 引擎 MinAddress（空盘起点）；恢复路径按 meta/扫盘覆盖
        _writeAddress = _engine.MinAddress;
        _physicalWriteAddress = _engine.MinAddress;
        _truncatedAddress = _engine.MinAddress;
        _committedWriteAddress = _engine.MinAddress;
        _writeWindow = 0;
        // ★ Managed meta 引擎构造期内联构建（纯 Create 零 IO——启动在 OnInitializeBegin，与主引擎并行）。
        if (settings.MetaPolicyKind == MetaPolicyKind.Managed)
        {
            // ★ 单段容量 = meta 块几何（与 ManagedMetaPolicy 同式：align4K(header+结构水位+OpaqueCapacity+footer)）
            //   ——按 MetaOpaqueBytes 计算，不硬编码；meta 单块不跨段，容量调多大块就多大，精确匹配。
            var metaBlockSize = (SnapshotMetaHeaderCodec.StructSize
                                 + SnapshotMetaPayloadCodec.StructSize + _settings.MetaOpaqueBytes
                                 + Crc32FooterCodec.StructSize).AlignUp(4096)
                                 + 4096;   // ★ +1 页余量（保守）：区间统一后精确填满已合法（尾停驻段末，覆盖写不再被拒）——余量防边界敏感，暂留
            var metaOptions = new StorageEngineOptions(
                    _settings.MainEngine.EngineName + ".meta",
                    metaBlockSize,
                    enableSegmentation: false,
                    preallocateFile: false,
                    deleteOnClose: _settings.MainEngine.DeleteOnClose)
                .WithHints(_settings.MainEngine.Hints);
            _metaEngine = new StorageEngine(_fs, metaOptions);
            // ★ meta 引擎进 Resources（owned）——ManagedMetaPolicy.Dispose 只释放自身 buffer，不管引擎
            Resources.Add(_metaEngine, "metaEngine");
            logger?.LogInformation("Managed meta engine created: {MetaEngineName}", _metaEngine.EngineName);
        }
        // ★ meta 策略构造期装配（构造=配置，Core 完整生命周期）：工厂注入优先，否则默认映射
        //   （几何来自 _fs.Volume——FS 静态属性，构造期可用，零生命周期依赖）。
        metaPolicyFactory ??= CreateMetaPolicyDefault;   // 方法组——与注入工厂同为 MetaPolicyFactory 委托
        MetaPolicy = metaPolicyFactory(_settings.MetaPolicyKind);
        Resources.Add(MetaPolicy, "metaPolicy");
    }

    /// <summary>逻辑写尾（ref 暴露：恢复路径直接赋值；会话推进单写者顺序，无需 CAS）。</summary>
    public ref LogicalAddress WriteAddress => ref _writeAddress;

    /// <summary>物理写尾（扇区对齐）。</summary>
    public ref LogicalAddress PhysicalWriteAddress => ref _physicalWriteAddress;

    /// <summary>逻辑截断点。</summary>
    public ref LogicalAddress TruncatedAddress => ref _truncatedAddress;

    /// <summary>当前流大小 = 写尾 - 截断点（引擎地址距离）。</summary>
    public long Size => _engine.GetDistance(_truncatedAddress, _writeAddress);

    /// <summary>设备扇区大小。</summary>
    public int SectorSize => _sectorSize;

    /// <summary>AlignedMemoryManager 内存对齐值——max(SectorSize, 4096)——保证 DIO 兼容。</summary>
    protected int DioAlignment => Math.Max(_sectorSize, 4096);

    // ════════════════════════════════════════════════════════════
    // === LifecycleBase<SnapshotRecoveryHints> 钩子 override ===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ Initialize 第一阶段钩子【前】——引擎 init。
    /// <para>★ 纯装配：MetaPolicy 装配在恢复核心内（依赖引擎就绪，对齐 lifecycle.md §2 钩子职责）。</para>
    /// </summary>
    protected override void OnInitializeBegin()
    {
        // 水位线归结构层（设计决策）：引擎自恢复，外部水位注入走结构 Initialize(hints)——
        // 静态配置透传 committedTailHint 设小了会把有效数据当半写截断，Settings 不暴露
        _engine.Initialize();
        _metaEngine?.Initialize();   // ★ meta 引擎（Managed）并行启动——不等，就绪 join 在恢复核心
    }

    /// <summary>
    /// ★ 恢复算法工厂——默认 DefaultSnapshotRecovery。在 Initialize 的 CAS 闸门内被调一次
    /// （基类单一创建点）；注入实例经构造函数直接赋 _recovery，不经本工厂。</summary>
    protected override IRecovery<SnapshotRecoveryHints> CreateRecovery()
        => new DefaultSnapshotRecovery(this);
}
