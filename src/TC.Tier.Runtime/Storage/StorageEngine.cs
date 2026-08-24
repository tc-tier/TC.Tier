namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 设备抽象基类——承载地址表、分配器、跨段拆分、截断、Compact 等公共逻辑。
/// <para>★ 继承 <see cref="LifecycleBase{EngineRecoveryHints}"/>——获得统一的 CAS 闸门/ResourceGroup/
///   InstanceTracker/终结器告警/non-virtual Dispose 模板/后台恢复 task 编排（与数据结构基类同源）。</para>
/// <para>三档派生 sealed 类各自实现物理读写原语差异。</para>
/// </summary>
internal sealed partial class StorageEngine : LifecycleBase<EngineRecoveryHints>, IStorageEngine
{
    /// <summary>
    /// 引擎策略选项——构造时注入，生命周期内不变。
    /// </summary>
    private readonly StorageEngineOptions _options;

    /// <summary>
    ///  <see cref="LightEpoch"/>——纳秒级 epoch 保护（spec 15 §0.2 确认接近理论极限）。
    /// <para>★ 私有——外部（含测试）需要 epoch 协调时经构造注入实例（Create(..., epoch:)），
    ///   引擎不暴露内部原语（用户裁定：依赖注入而非内部暴露）。</para>
    /// </summary>
    private readonly LightEpoch _epoch;
    /// <summary>
    /// 根空间文件系统——承载引擎名子目录，提供物理读写原语。
    /// </summary>
    private readonly IFileSystem _fs;

    private readonly ObservabilityHub? _hub;

    /// <summary>
    /// 句柄池——<see cref="FileHandlePool"/>, 提供段句柄借出/归还/全量释放能力。
    /// </summary>
    private readonly FileHandlePool _pool;

    /// <summary>
    /// 段表实例——<see cref="SegmentTable"/>，承载段元组、段满/段空、ReclaimHead/AllocatedTail/CommittedTail 等地址空间元信息。
    /// </summary>
    private readonly SegmentTable _segmentTable;

    /// <summary>
    /// Checkpoint 子系统——扫盘 Checkpoint（空目录 ⇒ 合成 seg0，全介质同构）。
    /// </summary>
    private readonly ICheckpoint _checkpoint;
    /// <summary>
    /// 引擎 own 的隔离调度器——worker loop 消费者 Task 跑其上，与公共池隔离。
    /// </summary>
    private readonly IsolatedTaskScheduler _workerScheduler;

    /// <summary>
    /// 运行期后台 worker loop——承载段生命周期事件（Create/Full）或低频后台任务。
    /// </summary>
    private readonly BackgroundWorkerLoop<WorkLoopItemTask> _workerLoop;
    /// <summary>
    /// Compact 子系统——承载段回收、PunchHole、ReclaimHead、Compact 等逻辑。
    /// </summary>
    private readonly ICompact _compact;

    /// <summary>
    /// ★ 公开方法：外部显式保存快照（不自动触发，不阻塞 Dispose）。
    /// <para>调用方按需决定保存时机（批量写入完成后、定时、关闭前等），与 Flush 同级语义。</para>
    /// <para>扫盘切面（HasSnapshot=false）→ 静默跳过。</para>
    /// </summary>
    public void SaveAddressTable()
    {
        EnsureReady();
        if (_checkpoint is not { HasSnapshot: true })
            return; // 扫盘切面或 null → 静默跳过
        _segmentTable.SaveAddressTable(_checkpoint.Writer);
    }

    /// <summary>段名记忆化——每读插值分配在点查热路径上（读句柄池键）。段名是 (EngineName, segId, EnableSegmentation)
    /// 的纯函数且永不失效；同实例返回附带让池键字符串相等走引用快路径。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _segmentNames = new();

    /// <inheritdoc/>
    /// <remarks>★ 根空间下相对路径（'/' 唯一分隔符，IStorageInfo 契约）：多段 = <c>{engine}/{engine}.{segId}</c>；单段 = <c>{engine}/{engine}</c>。引擎名可多级（<c>"a/b"</c>）。</remarks>
    public string SegmentFileName(int segId)
        => _segmentNames.GetOrAdd(segId, static (id, self) =>
            self.EnableSegmentation ? $"{self.EngineName}/{self.EngineName}.{id}" : $"{self.EngineName}/{self.EngineName}", this);


    /// <summary>
    /// 构造函数——注入根空间文件系统、引擎名、选项、Compact、Checkpoint、日志记录器、观测中心、LightEpoch。
    /// </summary>
    internal StorageEngine(
        IFileSystem root,
        StorageEngineOptions? options = null,
        ICompact? compact = null,
        ICheckpoint? checkpoint = null,
        ILogger? logger = null,
        ObservabilityHub? hub = null,
        LightEpoch? epoch = null)
        : base(logger: logger)
    {
        _options = options ??= StorageEngineOptions.Default;
        // ★ 生命周期参数构造传入（构造=配置，启动=双尾）——不再经 Initialize hints
        _fs = root;
        _hub = hub;
        EngineName = options.EngineName;
        Hints = options.Hints;
        _checkpoint = checkpoint ?? new ScanCheckpoint(this, logger: logger);
        Resources.Add(_checkpoint,
            ownership: checkpoint == null ? ResourceOwnership.Owned : ResourceOwnership.Referenced);
        _pool = new FileHandlePool(root, logger: logger);
        // ★ 读选项两形态构造期固化（FileOpenOptions 是 record 类——每读 new 即热路径堆分配）
        _readOptionsPageCache = new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
        };
        _readOptionsDio = new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
            Hints = FileOpenHints.NoBuffering,
        };
        // ★ M1：写句柄两形态构造期固化（读路径记忆化同款——写主路径每借零分配）
        _writeOptionsDio = new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
            Hints = (Hints.HasFlag(FileOpenHints.NoBuffering) ? FileOpenHints.NoBuffering : FileOpenHints.None)
                    | (Hints & FileOpenHints.WriteThrough),
        };
        _writeOptionsBuffer = new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
            Hints = Hints & FileOpenHints.WriteThrough,
        };
        // ★ LightEpoch——纳秒级 epoch 保护（spec 15 §0.2 确认接近理论极限）。
        //   自建或注入：reader 持 epoch 防 PunchHole 在读期间执行（drain worker 延迟）。
        _epoch = epoch ?? new LightEpoch();
        //   Epoch 用所有权二态：自建 Owned（组释放）/ 注入 Referenced（调用方自管，仅跟踪诊断）。
        Resources.Add(_epoch, ownership: epoch == null ? ResourceOwnership.Owned : ResourceOwnership.Referenced);
        var segmentHandler = new DefaultSegmentHandler(this);
        _segmentTable = new SegmentTable(_options.ToSegmentTableSettings(), segmentHandler, Logger);
        // ★ DefaultSegmentHandler 无状态（纯委托转发到引擎）——无需释放，不进 Resources
        //   （ResourceGroup 要求 IDisposable，注册即拒——ResourceGroup.Add:72 契约）。
        Resources.Add(_segmentTable);
        _workerScheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { Name = "engine-worker" });
        Resources.Add(_workerScheduler, "EngineWorkerScheduler");
        // ★ VII-2 收口（2026-08-22）：消费者数读 Optimization.WorkerConsumers（默认 2）——此前硬编码 1
        //   且选项从未接线：N2/N4 压测与 SegmentSingleFlightTests 设的 WorkerConsumers 形同虚设，
        //   所谓"N=2 证据线"实际一直在 N=1 下跑（用户指认）。建段 single-flight / 池协议已多次
        //   收口（§XI/§XIV/§XV），默认上调至配置契约值。
        var workerConsumers = Math.Max(1, _options.Optimization?.WorkerConsumers ?? 1);
        _workerLoop = new DefaultEngineWorkerLoop(this, Logger, consumerCount: workerConsumers);
        Resources.Add(_workerLoop);
        _compact = compact ?? new DefaultCompactor(this, _fs, epoch: _epoch, logger: Logger);
        Resources.Add(_compact, ownership: compact == null ? ResourceOwnership.Owned : ResourceOwnership.Referenced);
    }


    // === 元信息 ===

    /// <inheritdoc/>
    /// <remarks>★ 卷几何来自注入的根空间（构造时探测、fs 生命周期内不变）——mem 表达无对齐要求。</remarks>
    public uint SectorSize => (uint)Math.Max(1, _fs.Volume.SectorSize);

    /// <summary>★ 无缓冲 IO 探测结果（Core IO 对象——首个写句柄借出时回写；int 背板走 Volatile）。</summary>
    public UnbufferedIoSupport UnbufferedSupport => (UnbufferedIoSupport)Volatile.Read(ref _unbufferedSupportRaw);

    private int _unbufferedSupportRaw;

    /// <summary>★ 打开提示（请求意图：NoBuffering=DIO 请求 / WriteThrough=写透）——Core IO 对象，不自造枚举。</summary>
    public FileOpenHints Hints { get; }
    /// <inheritdoc/>
    public string EngineName { get; }

    /// <inheritdoc/>
    public long SegmentGrowthLimit => _options.SegmentGrowthLimit;

    /// <inheritdoc/>
    public bool EnableSegmentation => _options.EnableSegmentation;

    /// <inheritdoc/>
    public bool PreallocateFile => _options.PreallocateFile;

    // === 地址空间元信息 ===

    /// <inheritdoc/>
    public LogicalAddress AllocatedTail => _segmentTable.AllocatedTail;

    /// <inheritdoc/>
    public LogicalAddress CommittedTail => _segmentTable.CommittedTail;

    /// <inheritdoc/>
    /// <remarks>★ 最小有效地址。ReclaimHead 落在段中间时 Offset > 0(前半段已 PunchHole,段还在)。</remarks>
    public LogicalAddress MinAddress => _segmentTable.MinAddress;


    protected override void OnInitializeBegin()
    {
        // ★ 引擎子目录 mkdir -p（幂等，Core CreateDirectory 内建父目录耐久）——扫盘/建段的前置。
        _fs.CreateDirectory(EngineName);
    }

    protected override void OnInitializeComplete()
    {
        ConfigureBackgroundWorker(_workerLoop);
        InitializeSegmentPool();   // ★ IO 层段预备池（lookahead）——恢复已定 EnableSegmentation，尾段后预建 N 个
        CpuSampler.Start();
    }
}