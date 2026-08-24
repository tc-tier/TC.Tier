namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段表——地址空间权威组件
/// <para>★ 地址空间的唯一权威：段数组管理 + 双尾水位线 + 地址算术 + 建段自洽（经 ISegmentHandler）。</para>
/// <para>★ 主类（AddressSpace）和 Lease 直接依赖本具体类型。</para>
/// <para>★ partial 拆分（一文件一职责）：</para>
/// <list type="bullet">
/// <item><description><c>SegmentTable.cs</c>（本文件）——字段 + 构造 + Dispose + 双尾水位操作</description></item>
/// <item><description><c>SegmentTable.Addressing.cs</c>——地址算术 + 边界只读</description></item>
/// <item><description><c>SegmentTable.Segment.cs</c>——段操作（查询/建段/收缩/紧凑/替换）</description></item>
/// <item><description><c>SegmentTable.Checkpoint.cs</c>——持久化（LoadAddressTable/SaveAddressTable）</description></item>
/// <item><description><c>SegmentTable.Lease.cs</c>——租约</description></item>
/// <item><description><c>SegmentTable.ExtentLease.cs</c>——区间租约</description></item>
/// <item><description><c>SegmentTable.Diagnosis.cs</c>——诊断</description></item>
/// </list>
/// </summary>
public sealed partial class SegmentTable : IDisposable, ILeaseSource
{
    // ── 段数组（COW 替换，无锁读）──
    private Segment[] _segments; // 紧凑追加数组，下标由 _segIndex 映射（构造期按 Settings.IndexCapacity 分配）
    private int[] _segIndex; // segId → _segments 下标（-1=段不存在；构造期 Fill -1）

    private int _segCount; // _segments 条目数（含 Invalid 占位）

    // ── 段表头部边界（合并修撕裂读）──
    /// <summary>★ 16B LogicalAddress 单次原子写——修现状 _minSegId + _minOffset 两次 Volatile 写的撕裂读。</summary>
    private readonly AlignedMemoryManager _minAddrMem = new(16, 16, zeroed: true);

    // ── 双尾水位线（段表内部状态——AllocatedTail/CommittedTail，文档 §4）──
    /// <summary>★ 双尾水位 CAS 原语（段表 private 内部字段）。</summary>
    private readonly TailWatermarkSlot _tailSlot;


    /// <summary>段生长上限（主类 worker 构造建段 task + SaveAddressTable 写 header 用）。</summary>
    public long GrowthLimit { get; private set; }


    /// <summary>
    /// COW 串行化锁——段表结构变更（建段/收缩/紧凑/替换）必须持此锁。
    /// </summary>
    /// <remarks>
    /// ★ 锁层次（7.1）：_mutationLock（段表结构） &gt; SpinRWLock 排他（段级读计划/水位回退）
    ///   &gt; _extentLock（段内区间 Monitor）。获取顺序始终从外到内，禁止反向嵌套。<br/>
    ///   ShrinkHead 持 _mutationLock → 段内 _extentLock；ShrinkTail 持 SpinRWLock 排他 → _extentLock
    ///   （不持 _mutationLock，因尾截断不改段表结构）；二者无反向嵌套故不死锁。
    /// </remarks>
    private readonly object _mutationLock = new();

    /// <summary>
    /// 日志器（可选）。null = 不记录日志。
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// 线程自旋等待时间（毫秒）——CAS 循环失败时自旋等待，避免频繁抢占 CPU。
    /// </summary>
    private readonly long _spinMilliseconds;

    /// <summary>★ 区间公平门（写者公平性）——7a9685aa 根治长持锁窗口写者饿死的协议，
    /// 下沉 Core.FairGate：有等待者时新到者不走快路径（防零间隙复占者插队）+ 唤醒让渡 5ms 先手
    /// + 50ms park 兜底防丢失唤醒。AcquireExtent 退化为门内 Monitor 挂起，区间终态转换
    /// （Commit/Rollback）后 Wake（WriteThrough 每写 fsync 拉宽持锁窗口后曾实测单写离群 5-20s）。</summary>
    private readonly FairGate _extentGate = new();

    /// <summary>
    /// 日志警告频率（每 N 次警告一次，避免日志刷屏）。
    /// </summary>
    private readonly int _warnEvery;

    /// <summary>
    /// 单段模式——true=仅 seg0，分配超容量直接抛容量不足（不 spin/不建 seg1）。
    /// </summary>
    public bool EnableSingleSegment { get; private set; }

    // ── WaitSegmentReady 物理门等待──
    // 预算/告警走 SegmentTableSettings 统一参数（SpinMilliseconds/WarnEvery，与 AcquireExtent 自旋同构）；
    // 本常量只是单次让步粒度——SpinOnce 的时间等待对等物（实现细节，非策略参数）。
    private const int ReadyWaitParkSliceMs = 1_000;

    // ── 生命周期阶段门禁（恢复阶段一次性，运行阶段不可逆）──
    //   Recovery：构造后初始，仅允许 LoadAddressTable/ApplyHints（幂等/一次性）
    //   Runtime：首次 Allocate 成功推进后锁定，禁止再调 LoadAddressTable/ApplyHints
    private int _phase = (int)LifecyclePhase.Recovery;
    private int _addressTableLoaded; // LoadAddressTable 一次性标志（0=未加载, 1=已加载；CAS test-and-set 保证原子——4.3）
    /// <summary>段表是否已 Dispose——WaitSegmentReady 检查它，Dispose 后抛 ObjectDisposedException（7.2）。</summary>
    private volatile bool _disposed;

    /// <summary>
    /// ★ 本次生命周期创建/Compact 替换的最大段号——segId > 此阈值的段用全局 GrowthLimit（零查段表）。
    /// <para>避免 SegmentGrowthLimit 对新段调 TryGetSegmentRaw（数组下标 + Volatile.Read）。</para>
    /// <para>Compact 替换后更新为此值——历史段（≤ 阈值）大小可能不同，仍需查段。</para>
    /// </summary>
    private int _runtimeCreatedSegIdThreshold = -1;

    private enum LifecyclePhase
    {
        Recovery,
        Runtime
    }

    // ── 段处理器（段的事件模型——建段/段满/删除/替换/压缩/熔断，文档 §5）──
    /// <summary>
    /// 段处理器。null = 纯内存/测试（建 Written 段，不协调物理生命周期）。
    /// <para>★ 主类实现此接口：① 段生命周期事件适配 ISegmentLifecycle（设备层）② 低频后台任务由 worker 执行。</para>
    /// </summary>
    private readonly ISegmentHandler? _handler;

    private readonly LeaseFactory _leaseFactory;

    /// <summary>
    /// 构造——Settings + 处理器 + 日志器，构造完即可用。
    /// </summary>
    /// <param name="settings">段表设置。</param>
    /// <param name="handler">段处理器。</param>
    /// <param name="logger">日志器。</param>
    public SegmentTable(
        SegmentTableSettings settings,
        ISegmentHandler? handler = null,
        ILogger? logger = null)
        : this(settings, LeaseFactory.Default, handler, logger)
    {
    }

    /// <summary>
    /// 构造——Settings + 租约工厂 + 处理器 + 日志器，构造完即可用。
    /// </summary>
    /// <param name="settings">段表设置。</param>
    /// <param name="leaseFactory">租约工厂。</param>
    /// <param name="handler">段处理器。</param>
    /// <param name="logger">日志器。</param>
    public SegmentTable(
        SegmentTableSettings settings,
        LeaseFactory leaseFactory,
        ISegmentHandler? handler = null,
        ILogger? logger = null)
    {
        GrowthLimit = settings.GrowthLimit;
        EnableSingleSegment = settings.EnableSingleSegment;
        _spinMilliseconds = settings.SpinMilliseconds;
        _warnEvery = settings.WarnEvery;
        _handler = handler;
        _logger = logger;
        _leaseFactory = leaseFactory;
        _tailSlot = new TailWatermarkSlot();
        _segments = new Segment[Math.Min(settings.IndexCapacity, 16)];
        _segIndex = new int[settings.IndexCapacity];
        Array.Fill(_segIndex, -1);
        // ★ 构造完立即可用：MinAddress + 双尾水位都设为 (MinSegId, 0)——第一次 Allocate 从合法 segId 开始
        var initial = new LogicalAddress(settings.MinSegId, 0);
        SetMinAddress(initial);
        _tailSlot.Load(initial, initial);
    }

    /// <summary>
    /// 启动期双尾水位设定——一次性覆盖（可大可小，无限制）。
    /// <para>★ 纯水位设定，不建段不删段——段由 Allocate（运行期）或 LoadAddressTable（持久化恢复）建；
    ///   生命周期参数（GrowthLimit/分段开关）构造期经 <see cref="SegmentTableSettings"/> 传入，不在此。</para>
    /// <para>★ 只在 Allocate 之前（启动阶段）可调。**无持久化启动 = 构造 + 本方法定双尾 → 直接运行**
    ///   （LoadAddressTable 全程可选）。</para>
    /// <para>★ 裸写安全：启动单线程 + worker 不推进水位 + 业务 lease 等 Ready 之后。</para>
    /// </summary>
    public void SetStartupTails(StartupParameters startup)
    {
        // ★ 生命周期门禁：仅启动阶段可调。一旦 Allocate 推进水位（进入 Runtime），裸写会破坏 CAS 不变量。
        if ((LifecyclePhase)Volatile.Read(ref _phase) != LifecyclePhase.Recovery)
            throw new InvalidOperationException("SetStartupTails 仅在启动阶段（Allocate 之前）可调——段表已进入运行阶段。");

        var committedTail = startup.CommittedTail;
        var allocatedTail = startup.AllocatedTail;

        // ★ 启动值是上层裁决，可大可小（架构约定——不做数据损坏抛异常）：
        //   小 = 截断回收（删段/打洞/事件通知，物理待办由引擎恢复流程消费）；
        //   大 = 覆盖老数据（上层保证数据有效，段 MaxOffset 推进到该值使地址生效）。
        var real = _tailSlot.Committed;
        _tailSlot.WriteCommitted(committedTail);
        // 双尾联动：committed 是权威终点——allocated 跟随对齐（截断拉回/放大推上，维持 committed ≤ allocated）
        if (_tailSlot.Allocated != committedTail) _tailSlot.WriteAllocated(committedTail);

        if (committedTail < real)
        {
            TruncateSegmentsAfter(committedTail);   // 小：段级截断联动
        }
        else if (committedTail > real)
        {
            // 大：所在段 MaxOffset 推进到该值（覆盖老数据=正常修正，读门/RealSize 生效）
            if (TryGetSegmentRaw(committedTail.SegId, out var seg) && seg is not null && seg.MaxOffset < committedTail.Offset)
                seg.AdvanceOffset(committedTail.Offset);
        }

        if (allocatedTail == committedTail) return;
        _tailSlot.WriteAllocated(allocatedTail);
        if (_tailSlot.Committed > allocatedTail) _tailSlot.WriteCommitted(allocatedTail); // 联动维持 CommittedTail ≤ AllocatedTail
    }

    /// <summary>
    /// 截断水位之后的段级处理（ApplyHints 小值修正联动）——hint 之后整段 MarkInvalid+摘索引+事件通知，
    /// 由引擎恢复流程在 ApplyHints 之后消费执行（表不做 IO，对齐 ReclaimHead 三阶段）。
    /// <para>★ 恢复期单线程（ApplyHints 门禁保证）；事件通知锁外触发（对齐 ShrinkHead 模式）。</para>
    /// </summary>
    /// <param name="hint">截断后的真实水位。</param>
    private void TruncateSegmentsAfter(LogicalAddress hint)
    {
        List<int>? deletedSegIds = null;
        long holeFrom = 0, holeTo = 0;
        var holeSeg = -1;
        var holeGrowthLimit = 0L;
        lock (_mutationLock)
        {
            var segs = Volatile.Read(ref _segments);
            for (var sid = hint.SegId + 1; sid < _segIndex.Length; sid++)
            {
                var idx = sid < _segIndex.Length ? _segIndex[sid] : -1;
                if (idx < 0) continue;
                var seg = segs[idx];
                if (seg.StableState == StableState.Invalid) continue;
                seg.MarkInvalid();
                    // ★ 摘索引（区别于 ShrinkHead——那里靠 MinAddress 前移跳过 Invalid 段；
                    //   截断场景 MinAddress 不动，不摘会让 EnsureSegmentsForLength 把 tail 推进到 Invalid 段）
                    //   volatile 发布（读者侧 acquire 对称——TryGetSegmentRaw 等无锁读）
                    Volatile.Write(ref _segIndex[sid], -1);
                (deletedSegIds ??= new List<int>()).Add(sid);
            }

            // hint 所在段：MaxOffset/区间表回退到 hint；[hint.Offset, 旧 MaxOffset) 记为打洞待办
            if (TryGetSegmentRaw(hint.SegId, out var cur) && cur is not null && cur.MaxOffset > hint.Offset)
            {
                holeSeg = hint.SegId;
                holeFrom = hint.Offset;
                holeTo = cur.MaxOffset;
                holeGrowthLimit = cur.GrowthLimit;
                cur.RetreatOffset(hint.Offset);
            }
        }

        // 锁外事件通知（对齐 ShrinkHead 模式）——★ 无整段删除也要发打洞事件（909afaaa 后的早退
        //   会把单段截断的 OnSegmentReclaim 一起吞掉）。事件只做通知——物理联动由引擎恢复流程
        //   【提前处理】（hint 驱动），表不做反向取回。
        if (_handler is null) return;
        {
            if (deletedSegIds is not null)
                foreach (var sid in deletedSegIds)
                    _handler.OnSegmentDelete(sid);
            if (holeSeg >= 0 && holeTo > holeFrom)
                _handler.OnSegmentReclaim(holeSeg, holeFrom, holeTo, holeGrowthLimit);
        }
    }

    // ── 私有辅助 ──

    /// <summary>设 MinAddress——16B 单次原子写（修撕裂读）。</summary>
    private void SetMinAddress(LogicalAddress value)
    {
        // ★ 7.5：Dispose 后静默丢弃会掩盖延迟回调（如 ShrinkHead 的 SetMinAddress）的写入失败——改抛异常暴露
        if (_minAddrMem.IsDisposed)
            throw new ObjectDisposedException(nameof(SegmentTable), "段表已 Dispose，SetMinAddress 无效");
        _minAddrMem.GetRefUnsafe<LogicalAddress>(0) = value;
    }

    /// <summary>释放 native 内存（_minAddrMem + _tailSlot）。</summary>
    public void Dispose()
    {
        // ★ 7.2：先标记 Dispose + 唤醒所有 WaitSegmentReady 上 park 的等待者，让它们检查 _disposed 后抛
        //   ObjectDisposedException，避免 Dispose 后永久挂起
        _disposed = true;
        PulseAllSegmentsReady();
        _minAddrMem.Dispose();
        _tailSlot.Dispose();
    }
}