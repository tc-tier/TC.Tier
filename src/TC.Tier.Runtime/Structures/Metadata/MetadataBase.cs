namespace TC.Tier.Runtime.Structures.Metadata;

/// <summary>
/// 元数据基类——小数据元数据（meta 结构体 + cursor）的版本链存储。
/// 内存工作副本（运行时同步零 IO）+ 磁盘版本链（事务回滚）+ 多版本保留（Abort 零 IO）。
/// <para>★ 生命周期：继承 <see cref="LifecycleBase{THints}"/>（实现 <see cref="ILifecycle{THints}"/>）——
///   <see cref="LifecycleBase{THints}"/>.Initialize 同步 void 启动后台恢复后立即返回，
///   调用方用 IsReady/WaitForReady/事件观测等待。详见 src/TC.Tier.Core/docs/lifecycle.md。
///   本类 override <see cref="OnInitializeBegin"/>（引擎 init）+ <see cref="CreateRecovery"/>（返回
///   DefaultMetadataRecovery——RecoveryBase 模板派生：WaitForDependenciesAsync 等主引擎就绪，
///   MetaPolicy 装配在恢复核心内）。</para>
/// </summary>
public abstract partial class MetadataBase
    : LifecycleBase<MetadataRecoveryHints>, ITransactionParticipant
{
    /// <summary>
    /// 元数据存储引擎。
    /// </summary>
    private readonly StorageEngine _engine;

    /// <summary>
    /// 组合根注入的文件系统（介质平权——TierFs 拿 IFileSystem，换介质 = 换一根 spec）。
    /// 主引擎与 Managed meta 引擎共用（meta 引擎子目录 = 主引擎名 + ".meta"）。
    /// </summary>
    private readonly IFileSystem _fs;

    /// <summary>meta 传输（Transport 模式用）——默认装配的 Transport 回落取用。</summary>
    private readonly IMetaTransport? _metaTransport;

    /// <summary>Managed 模式的 meta 引擎（构造期 Create；启动在 OnInitializeBegin，就绪等待在恢复核心）。</summary>
    private readonly StorageEngine? _metaEngine;

    /// <summary>
    /// 元数据格式 codec（Header + Payload + Padding）。
    /// </summary>
    private readonly IMetadataCodec _codec;

    // === 配置 ===
    private readonly MetadataSettings _settings;
    private readonly int _payloadSize;
    private readonly int _paddingLength;
    private readonly int _recordSize; // HeaderSize + payloadSize + padding（每版本块总长）
    private readonly int _maxMemoryVersions; // 内存多版本保留窗口（底线 2）

    // === 落盘策略（Sync 立即落盘 / Async 后台批量落盘）===
    private readonly IPersistencePolicy? _persistencePolicy;

    // === epoch（内存一致性核心：保护内存工作副本读 vs 截断回收竞态）===
    private readonly LightEpoch _epoch;

    // === 水位（版本链两端 + tx seq）===
    private LogicalAddress _highestVersionAddress = LogicalAddress.Empty; // 链头/当前版本
    private LogicalAddress _lowestVersionAddress = LogicalAddress.Empty; // 链尾/最老版本
    private long _lastCommittedSeq = -1;
    private long _lastPreparedSeq = -1;
    private long _lastAbortedSeq = -1;
    private long _currentVersion; // 当前版本号（单调递增）

    // === 热数据：内存多版本工作副本（Abort 零 IO 的关键，对齐内存对象）===
    // 最近 N 个版本的对齐内存对象（N = _maxMemoryVersions）。[0] = 当前。
    // ★ 用 AlignedMemoryManager（pinned native，零 GC）而非 byte[]——对齐 Ring 页池模式。
    // ★ 本次生命周期的 PayloadSize 固定（每个热区对象大小 = _payloadSize）。
    private readonly AlignedMemoryManager[] _hotVersions;
    private int _hotVersionCount;

    // === 历史版本只读缓冲（恢复载入）——设计决策：不能无条件截断用户数据 ===
    // ★ 载入的旧版本按盘上真实 PayloadLength 从自持 PinnedBufferPool 租用（只读产物，
    //   不进按当前配置分配的读写热区——历史大小 ≠ 当前 PayloadSize 时不补零、不截断）。
    // ★ 首次 Write 前：当前内容 = 加载版本（Read/AsSpan 按历史真实大小交付）；
    //   Write 后：当前内容 = 热区（本启动配置大小）。池自持（不提供全局——全局池易租借不归还）。
    private readonly Core.Collections.PinnedBufferPool _bufferPool = new(maxPerBucket: 2);
    private AlignedMemoryManager? _loadedVersion;
    private int _loadedVersionLength;
    private bool _serveLoaded;

    // === 会话/回退基准（Abort 回退点 + 无变化跳过持久化）===
    private long _baseVersion;        // 最近 恢复载入/ConfirmCommitted 时的版本号——Write 超过它才需要内存回退
    private long _persistedVersion;   // 盘上链头对应的版本号——Prepare 时内容未变则跳过追加（防重复/防缩容零覆写）
    private bool _sessionActive;      // Write 开启的编辑会话（ConfirmCommitted/Abort 关闭）——无会话时 Confirm 不得改版本号
    private long _sessionVersion;     // 会话写出的版本号（Write 推进后记录；Confirm 采纳为当前）
    private bool _prepareAppended;    // 本 Prepare 真追加过 record（Abort 尾截断只回收真追加的悬干）
    private long _preparePersistedVersion; // Prepare 追加前的 _persistedVersion（Abort 回退盘上链头版本记账）

    // === 恢复（LifecycleBase 后台任务基础设施，全部上提到基类）===
    // Recovery / _recoveryState / _recoverTask / _recoveryCts / _initialized / _recoveryError / _disposed
    // / RecoveryProgressChanged 事件——均由 LifecycleBase<THints> 提供，本类不再重复声明。

    // === Abort 尾截断快照（Prepare 前的链头地址，Abort 回退用）===
    private LogicalAddress _prepareSnapshotAddress = LogicalAddress.Empty;
    private bool _hasPrepareSnapshot; // ★ 是否记录过 Prepare 快照（不能用 addr==Empty 判断——Empty 是合法地址）

    // === 2PC 回调（对齐 LogBase.Transaction 范式）===
    private readonly SortedList<long, List<Action>> _txCallbacks = new();
    private readonly object _txCallbackLock = new();

    /// <summary>
    /// 构造（protected，子类继承）。注入 codec + fs + settings + 可选 recovery/metaTransport/epoch/persistencePolicy。
    /// <para>★ 引擎 = 构造期配置（对齐 LogBase）：<c>new StorageEngine(fs, settings.MainEngine)</c>——
    ///   fs 从组合根注入（TierFs，介质平权），段几何/hints 走 MainEngine 选项。Initialize 启动后台恢复。</para>
    /// </summary>
    /// <param name="codec">数据格式 codec（构造第一参数，对齐 Log/Ring）。</param>
    /// <param name="fs">组合根文件系统（TierFs 构造的 IFileSystem）。</param>
    /// <param name="settings">元数据设置。</param>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="metaPolicyFactory">可选的 meta 策略工厂实例。</param>
    /// <param name="metaTransport">可选的 meta 传输（Transport 模式用）——上层实现此接口决定 meta 存哪、何时落盘。</param>
    /// <param name="epoch">可选的 LightEpoch 实例。</param>
    /// <param name="persistencePolicy">可选的持久化策略实例。</param>
    /// <param name="logger">可选的日志记录器实例。</param>
    /// <exception cref="ArgumentException">当 settings 无效时抛出。</exception>
    protected MetadataBase(
        IMetadataCodec codec,
        IFileSystem fs,
        MetadataSettings settings,
        IRecovery<MetadataRecoveryHints>? recovery = null,
        MetaPolicyFactory<MetadataMetaHeader, MetadataMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null,
        LightEpoch? epoch = null,
        IPersistencePolicy? persistencePolicy = null,
        ILogger? logger = null)
        : base(recovery, logger: logger)
    {
        _codec = codec;
        _fs = fs;
        _settings = settings;
        _metaTransport = metaTransport;
        _payloadSize = (settings as VersionedMetadataSettings)?.PayloadSize
                       ?? throw new ArgumentException("VersionedMetadataSettings.PayloadSize 必须指定", nameof(settings));
        _maxMemoryVersions = Math.Max(2, settings.MaxMemoryVersions);
        _persistencePolicy = persistencePolicy;

        // ★ 引擎构造 = 配置（对齐 LogBase）：fs 注入 + settings.MainEngine（Metadata 版本链默认单段模式）。
        //   SectorSize 属性读 _fs.Volume（构造即可用，不依赖 Initialize）——padding/热区分配安全。
        _engine = new StorageEngine(fs, settings.MainEngine);
        Resources.Add(_engine, ownership: ResourceOwnership.Owned);
        // epoch（注入或内部 new 自管）——LightEpoch 实现 IDisposable，进 Resources 统一释放
        _epoch = epoch ?? new LightEpoch();
        Resources.Add(_epoch, ownership: epoch is null ? ResourceOwnership.Owned : ResourceOwnership.Referenced);
        // 历史版本只读缓冲池进资源组（Dispose 统一释放；加载版本先由 DisposeOverride 归还）
        Resources.Add(_bufferPool, "bufferPool");
        // padding 对齐到扇区
        var sectorSize = (int)_engine.SectorSize;
        _paddingLength = (codec.HeaderSize + _payloadSize).AlignUp(sectorSize)
                         - codec.HeaderSize - _payloadSize;
        _recordSize = codec.HeaderSize + _payloadSize + _paddingLength;
        // 热区：N 个对齐内存对象（当前 PayloadSize 固定大小）
        _hotVersions = new AlignedMemoryManager[_maxMemoryVersions];
        for (var i = 0; i < _maxMemoryVersions; i++)
            _hotVersions[i] = new AlignedMemoryManager(_payloadSize, sectorSize, zeroed: true);
        _hotVersionCount = 0;
        // ★ Managed meta 引擎构造期内联构建（纯 Create 零 IO——启动在 OnInitializeBegin，与主引擎并行）。
        if (settings.MetaPolicyKind == MetaPolicyKind.Managed)
        {
            // ★ 单段容量 = meta 块几何（与 ManagedMetaPolicy 同式：align4K(header+结构水位+OpaqueCapacity+footer)）
            //   ——按 MetaOpaqueBytes 计算，不硬编码；meta 单块不跨段，容量调多大块就多大，精确匹配。
            var metaBlockSize = (MetadataMetaHeaderCodec.StructSize
                                 + MetadataMetaPayloadCodec.StructSize + _settings.MetaOpaqueBytes
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

    /// <summary>
    /// 当前版本链的最高地址（链头/最新版本）。
    /// </summary>
    public LogicalAddress HighestVersionAddress => _highestVersionAddress;

    /// <summary>
    /// 当前版本链的最低地址（链尾/最老版本）。
    /// </summary>
    public LogicalAddress LowestVersionAddress => _lowestVersionAddress;

    /// <summary>
    /// 当前版本号（单调递增）。
    /// </summary>
    public long CurrentVersion => _currentVersion;

    /// <summary>
    /// 当前版本链的扇区大小（字节）。
    /// </summary>
    public uint SectorSize => _engine.SectorSize;

    // ════════════════════════════════════════════════════════════
    // === LifecycleBase<MetadataRecoveryHints> 钩子 override ===
    // LifecycleBase（Initialize 类面方法 + IsReady/WaitForReady*/RecoveryProgressChanged/状态机接口面）全部由基类提供。
    // 本类只 override 两个钩子：OnInitializeBegin（引擎 init）+ CreateRecovery（返回 RecoveryBase 模板派生的
    // DefaultMetadataRecovery——策略装配在其恢复核心内，引擎就绪后）。
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ Initialize 第一阶段钩子【前】——引擎 init（此时 this 已构造完，虚方法安全）。
    /// <para>基类 Initialize 调用 → CreateRecovery（紧随其后）→ 订阅事件 → 后台恢复。</para>
    /// <para>★ 纯装配：MetaPolicy 装配不在此（依赖引擎就绪，挪进恢复核心——对齐 lifecycle.md §2 钩子职责）。</para>
    /// </summary>
    protected override void OnInitializeBegin()
    {
        // 阶段 1：引擎初始化（同步，调用线程）
        // ★ 构造 = 配置（段生长上限/分段开关已在 ctor 传入）；Initialize 只带恢复水位
        // 水位线归结构层（设计决策）：引擎自恢复，外部水位注入走结构 Initialize(hints)——
        // 静态配置透传 committedTailHint 设小了会把有效数据当半写截断，Settings 不暴露
        _engine.Initialize();
        _metaEngine?.Initialize();   // ★ meta 引擎（Managed）并行启动——不等，就绪 join 在恢复核心
    }

    /// <summary>
    /// ★ 恢复算法工厂——默认 DefaultMetadataRecovery。在 Initialize 的 CAS 闸门内被调一次
    /// （基类单一创建点）；注入实例经构造函数直接赋 _recovery，不经本工厂。</summary>
    protected override IRecovery<MetadataRecoveryHints> CreateRecovery()
        => new DefaultMetadataRecovery(this);
}