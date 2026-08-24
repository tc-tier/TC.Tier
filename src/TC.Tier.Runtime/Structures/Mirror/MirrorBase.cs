namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>
/// 镜像基类——checkpoint 镜像的版本链存储。
/// 完整版本链（每次 checkpoint 追加一个完整新版本）+ N=2 轮替（Commit 后立即头截断回收最老，文件恒定 2 倍空间）+ 事务回滚。
/// <para>★ 与 MetadataBase 的区别：无内存工作副本（镜像中等体量直接读写盘，Abort 从盘回退——回滚低频可接受）；
///   写入返回逻辑物理地址（不是版本号）；不用 epoch（无内存页池驱逐竞态，截断回收由引擎内部 epoch 保护）。</para>
/// <para>★ 子类两粒度：WholeMirror（HT+OFB 整体单链，Begin→Chunk→End + CRC64 流式）/
///   PagedMirror（per-page 多链，WritePage 可乱序 + CRC32C）。多链原子性由 2PC 统一推进保证。</para>
/// <para>★ 生命周期：继承 <see cref="LifecycleBase{THints}"/>——Initialize 同步 void 启动后台恢复，
///   WaitForReady 等就绪（详见 src/TC.Tier.Core/docs/lifecycle.md）。恢复走 RecoveryBase 模板派生。</para>
/// </summary>
public abstract partial class MirrorBase : LifecycleBase<MirrorRecoveryHints>, ITransactionParticipant
{
    /// <summary>镜像存储引擎（版本链存储）。</summary>
    private protected readonly StorageEngine _engine;

    /// <summary>Managed 模式的 meta 引擎（构造期 Create；启动在 OnInitializeBegin，就绪等待在恢复核心）。</summary>
    private readonly StorageEngine? _metaEngine;

    /// <summary>meta 传输（Transport 模式用）——默认装配的 Transport 回落取用。</summary>
    private readonly IMetaTransport? _metaTransport;

    /// <summary>组合根注入的文件系统（主引擎与 Managed meta 引擎共用）。</summary>
    private readonly IFileSystem _fs;

    /// <summary>镜像格式 codec（Header + Payload + Padding）。</summary>
    private protected readonly IMirrorCodec _codec;

    // === 配置 ===
    private readonly MirrorSettings _settings;

    // === 水位（版本链两端 + tx seq）===
    /// <summary>已提交链头地址（WholeMirror=单链头；PagedMirror=最后提交 record 地址）。</summary>
    private protected LogicalAddress _highestVersionAddress = LogicalAddress.Empty;

    /// <summary>回收边界（最老保留版本地址；初始 Empty=未回收过，语义=地址空间起点）。</summary>
    private protected LogicalAddress _lowestVersionAddress = LogicalAddress.Empty;

    private protected long _lastCommittedSeq = -1;
    private long _lastPreparedSeq = -1;
    private long _lastAbortedSeq = -1;

    /// <summary>已提交 checkpoint 版本号（单调）。</summary>
    private protected long _currentVersion;

    /// <summary>当前 checkpoint 会话版本（写入中未提交；会话内所有 record 同号）。</summary>
    private protected long _sessionVersion;

    /// <summary>会话激活标志（首个写入置位，Confirm/Abort 清零）。</summary>
    private protected bool _sessionActive;

    /// <summary>已提交链尾地址（Abort 尾截断回退点——本地址起均为悬干）。</summary>
    private protected LogicalAddress _committedChainEnd = LogicalAddress.Empty;

    /// <summary>最后写入 record 的结束地址（Confirm 时推进 _committedChainEnd 用）。</summary>
    private protected LogicalAddress _lastRecordEnd = LogicalAddress.Empty;

    /// <summary>是否已有已提交版本（不能用地址值判断——Empty 是合法地址，首 record 就在 Empty）。</summary>
    private protected bool _hasCommittedVersion;

    // === 2PC 回调（对齐 MetadataBase 范式）===
    private readonly SortedList<long, List<Action>> _txCallbacks = new();
    private readonly object _txCallbackLock = new();

    /// <summary>
    /// 构造（protected，子类继承）。引擎 = 构造期配置（对齐 LogBase/MetadataBase）：
    /// fs 从组合根注入（TierFs，介质平权）+ settings.MainEngine（单段版本链）。
    /// </summary>
    /// <param name="codec">镜像格式 codec（构造第一参数，对齐 Log/Ring/Metadata）。</param>
    /// <param name="fs">组合根文件系统（TierFs 构造的 IFileSystem）。</param>
    /// <param name="settings">镜像设置。</param>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="metaPolicyFactory">可选的 meta 策略工厂（子类定制唯一通道——默认按 Kind 内联装配）。</param>
    /// <param name="metaTransport">可选的 meta 传输（Transport 模式用；未注入回落内部 MetaHost 嵌入主流）。</param>
    /// <param name="logger">可选的日志记录器实例。</param>
    protected MirrorBase(
        IMirrorCodec codec,
        IFileSystem fs,
        MirrorSettings settings,
        IRecovery<MirrorRecoveryHints>? recovery = null,
        MetaPolicyFactory<MirrorMetaHeader, MirrorMetaPayload>? metaPolicyFactory = null,
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
        if (settings.MetaPolicyKind == MetaPolicyKind.Managed)
        {
            // ★ 单段容量 = meta 块几何（与 ManagedMetaPolicy 同式：align4K(header+结构水位+OpaqueCapacity+footer)）
            //   ——按 MetaOpaqueBytes 计算，不硬编码；meta 单块不跨段，容量调多大块就多大，精确匹配。
            var metaBlockSize = (MirrorMetaHeaderCodec.StructSize
                                 + MirrorMetaPayloadCodec.StructSize + _settings.MetaOpaqueBytes
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
        // ★ meta 策略构造期装配（构造=配置，Core 完整生命周期）：工厂优先，否则按 Kind 内联三模式。
        //   几何（SectorSize 等）来自 _fs.Volume——FS 静态属性，构造期可用，策略/引擎构造零生命周期依赖；
        //   Managed 的 meta 引擎纯 Create（零 IO），启动在 OnInitializeBegin、就绪等待在恢复核心。
        metaPolicyFactory ??= CreateMetaPolicyDefault;   // 方法组——与注入工厂同为 MetaPolicyFactory 委托
        MetaPolicy = metaPolicyFactory(_settings.MetaPolicyKind);
        Resources.Add(MetaPolicy, "metaPolicy");

        // 帧基建（统一帧机制：IO 缓冲基类持随——机制归基类，COORDINATION §4 铁律 10）
        _frameIoBuf = new AlignedMemoryManager(FrameIoBufSize, 64);
    }

    /// <summary>释放帧基建缓冲（异步轨实质等价）。</summary>
    protected override void DisposeOverride(bool disposing)
    {
        _frameIoBuf.Dispose();
        base.DisposeOverride(disposing);
    }

    /// <summary>释放帧基建缓冲（异步轨）。</summary>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        _frameIoBuf.Dispose();
        await base.DisposeOverrideAsync(disposing).ConfigureAwait(false);
    }

    /// <summary>
    /// 默认 meta 策略装配（构造期经 ??= 收口——签名即 MetaPolicyFactory"按模式构造"委托：
    /// 注入工厂与默认实现是同一条 kind → policy 映射，无匿名 lambda）。
    /// </summary>
    private IMetaPolicy<MirrorMetaHeader, MirrorMetaPayload> CreateMetaPolicyDefault(MetaPolicyKind kind)
    {
        var layout = new MetaLayout(_settings.MetaOpaqueBytes);
        return kind switch
        {
            MetaPolicyKind.Managed => _metaEngine is not null
                ? new ManagedMetaPolicy<MirrorMetaHeader, MirrorMetaPayload>(
                    layout, _metaEngine, Logger)
                : throw new InvalidOperationException("Meta engine is not initialized."),
            // ★ Transport：上层注入传输实例（自定义介质）；未注入回落到 MetaHost——meta block 作为带 IS_META flag
            //   的版本 record 嵌入镜像流（追加流宿主，对齐 Log/Ring/Metadata）。
            MetaPolicyKind.Transport => new TransportMetaPolicy<MirrorMetaHeader, MirrorMetaPayload>(
                layout, _metaTransport ?? new MetaHost(this), Logger),
            _ => new DisabledMetaPolicy<MirrorMetaHeader, MirrorMetaPayload>(),
        };
    }

    /// <summary>已提交链头地址（当前 checkpoint 版本）。</summary>
    public LogicalAddress HighestVersionAddress => _highestVersionAddress;

    /// <summary>是否已有已提交版本（Empty 是合法地址——不能用 HighestVersionAddress 值判断有无）。</summary>
    public bool HasCommittedVersion => _hasCommittedVersion;

    /// <summary>回收边界（最老保留版本地址）。</summary>
    public LogicalAddress LowestVersionAddress => _lowestVersionAddress;

    /// <summary>已提交 checkpoint 版本号（单调）。</summary>
    public long CurrentVersion => _currentVersion;

    // ════════════════════════════════════════════════════════════
    // === LifecycleBase<MirrorRecoveryHints> 钩子 override ===
    // LifecycleBase（Initialize 类面方法 + IsReady/WaitForReady*/状态机接口面）全部由基类提供。
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ Initialize 第一阶段钩子【前】——引擎 init。
    /// <para>★ 纯装配：MetaPolicy 装配不在此（依赖引擎就绪，在恢复核心内——对齐 lifecycle.md §2 钩子职责）。</para>
    /// </summary>
    protected override void OnInitializeBegin()
    {
        // 水位线归结构层（用户裁定）：引擎不带双尾修正自恢复（物理真相归引擎）；
        // 外部水位注入走结构 Initialize(hints)。静态配置透传 committedTailHint 是脚枪
        // ——设小了引擎按它截断物理尾，结构层水位线全体错乱，Settings 不暴露。
        _engine.Initialize();
        _metaEngine?.Initialize(); // ★ meta 引擎（Managed）并行启动——不等，就绪 join 在恢复核心
    }

    /// <summary>
    /// ★ 恢复算法工厂——默认 DefaultMirrorRecovery。在 Initialize 的 CAS 闸门内被调一次
    /// （基类单一创建点）；注入实例经构造函数直接赋 _recovery，不经本工厂。</summary>
    protected override IRecovery<MirrorRecoveryHints> CreateRecovery()
        => new DefaultMirrorRecovery(this);
}