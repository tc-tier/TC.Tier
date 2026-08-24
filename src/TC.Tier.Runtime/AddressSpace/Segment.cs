using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段对象（Segment）——承载数据文件的最小单位，负责水位管理、状态机、锁。
/// </summary>
public sealed partial class Segment
{
    // ═══ 锁状态 ═══
    /// <summary>
    /// SpinRWLock 写偏向自旋读写锁——读计划共享（可跨 await/IO 长持）+ 水位回退/截断排他（短临界区纯内存）。
    /// <para>★ 2026-08-20 自 LockWord 换型：写偏向（pending 挡新读者）保证 ShrinkTail 类排他转换
    ///   在持续读者流下有界落地，不再读优先饿死。</para>
    /// </summary>
    private readonly SpinRWLock _lock = new();
    /// <summary>
    /// 日志记录器（可选）。
    /// </summary>
    private readonly ILogger? _logger;
    /// <summary>
    /// 区间表压缩阈值（碎片率超过该值时触发 CompactIntervals）。
    /// </summary>
    private readonly int _compactThreshold;
    /// <summary>
    /// 真实增长水位（Volatile.Read 保证跨线程可见）。
    /// </summary>
    private long _maxOffset;
    /// <summary>
    /// 段最小水位（Volatile.Read 保证跨线程可见）。
    /// </summary>
    private long _minOffset;
    // ═══ 状态机（StableState 单字段）═══
    /// <summary>
    /// 段生命周期稳态（崩溃恢复后一致）——int 背板（2026-08-16 §6.1：物理门协调零锁化）。
    /// <para>★ CAS 迁移（Interlocked）+ volatile 发布——可见性由 fence 保证，不靠锁；
    ///   Empty→Ready/Broken/Invalid 单向不可逆（除非删段——删段后引用对象已换）。</para>
    /// </summary>
    private int _stateCode;

    /// <summary>
    /// ★ Compact 原位更新版本号（L12 修复 2026-08-21）——每次 Compact 原位替换内脏时 <c>Volatile</c> 递增。
    /// <para>★ 用途：陈旧认知快速失败——<c>AcquireExtent</c> 自旋/FairGate 每轮重取 (seg, version) 对，
    ///   醒来/提交时版本不符 = 该轮认知基于已重整的旧内脏，必须丢弃重查（显式重试/异常，不静默 no-op）。</para>
    /// <para>★ int 足够：Compact 低频（秒级+），2^31 回绕不可达。0 = 从未被 Compact 重整。</para>
    /// </summary>
    private int _compactVersion;

    /// <summary>Compact 原位更新版本（每次 Compact 重整 +1；读者用于校验认知新鲜度）。</summary>
    public int CompactVersion => Volatile.Read(ref _compactVersion);

    /// <summary>
    /// ★ 物理门单向闩——Empty→Ready/Broken/Invalid 迁移时 Set，<b>永不 Reset</b>（单向不可逆）。
    /// 等待 = 先查状态（volatile）→ 等闩 → 醒后复查——双检零竞态。
    /// <para>★ 恢复期构造的非 Empty 段（出生即 Ready/Full）构造时即 Set（已物理就绪）。</para>
    /// </summary>
    private readonly Core.Primitives.AsyncManualResetEvent _physicalReady = new();

    // ═══ 只读属性 ═══
    /// <summary>段身份（事件快照、日志、Compact 关联用）。</summary>
    public int SegId { get; }

    /// <summary>
    /// 生长上限——Compact 原位更新可变（L12 修复 2026-08-21：换段从"新建对象换槽"改为
    /// "同对象换内脏"，引用恒稳——自旋写者/reader/句柄池持锁天然互斥）。
    /// 变更仅经 <see cref="ApplyCompactReplacement"/>（持 extent lock + SegmentLock 排他）。
    /// <para>★ L28 收口（2026-08-22）：读改 Volatile——GrowthLimit 是 Compact 可变的跨线程字段，
    ///   无屏障裸读（地址算术/IsFull/Remaining 的消费点）可与换段写交错读到旧值（ARM 弱序）。</para>
    /// </summary>
    private long _growthLimit;

    /// <summary>生长上限（Volatile.Read 保证跨线程可见——Compact 原位更新可改，见 <see cref="_growthLimit"/>）。</summary>
    public long GrowthLimit
    {
        get => Volatile.Read(ref _growthLimit);
        private set => Volatile.Write(ref _growthLimit, value);
    }

    /// <summary>真实增长水位（Volatile.Read 保证跨线程可见性）。</summary>
    public long MaxOffset => Volatile.Read(ref _maxOffset);

    /// <summary>
    /// 段最小水位（Volatile.Read 保证跨线程可见性）。
    /// </summary>
    public long MinOffset => Volatile.Read(ref _minOffset);

    /// <summary>
    /// 段真实大小（MaxOffset - MinOffset，Volatile.Read 保证跨线程可见性）。
    /// </summary>
    public long RealSize => MaxOffset - MinOffset;
    /// <summary>段生命周期稳态（volatile 发布——零锁读者的可见性保证）。</summary>
    public StableState StableState => (StableState)Volatile.Read(ref _stateCode);

    /// <summary>
    /// ★ 物理状态就绪（物理门开）——<see cref="WaitSegmentReady"/> 的判定谓词（2026-08-16 用户裁定：
    /// 物理门正向判定物理就绪，不用逻辑相位反向判定）。
    /// <para>Ready/Full/Compacting 均为物理就绪（Compacting 物理存在、整理排他由区间锁管，不在本门职责）；
    /// Empty（建段中，门关）/Broken（门永关）/Invalid（准入吊销，文件不存在）非就绪。</para>
    /// </summary>
    public bool IsPhysicalReady
    {
        get
        {
            var s = (StableState)Volatile.Read(ref _stateCode);
            return s is StableState.Ready or StableState.Full or StableState.Compacting;
        }
    }

    /// <summary>
    /// 剩余可写空间（GrowthLimit - MaxOffset，若已满则为 0）。
    /// </summary>
    public long Remaining => GrowthLimit > MaxOffset ? GrowthLimit - MaxOffset : 0;

    /// <summary>
    /// 段是否已满（MaxOffset ≥ GrowthLimit）。
    /// </summary>
    public bool IsFull => MaxOffset >= GrowthLimit;

    /// <summary>
    /// 段对象是否有效（segId ≥ 0 且 growthLimit > 0 且 maxOffset ≥ 0）。
    /// </summary>
    public bool IsValid => GrowthLimit > 0 && SegId >= 0 && MaxOffset >= 0;
    // ═══ 构造（单阶段——初始化并入构造，删 Create/Initialize/_isInitialized）═══

    /// <summary>
    /// 段锁对象（SpinRWLock 写偏向 RW 自旋锁）——读共享 + 水位回退/截断排他。
    /// </summary>
    public SpinRWLock SegmentLock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _lock;
    }

    /// <summary>
    /// 初始化一个新的 <see cref="Segment"/> 实例。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="maxOffset">初始最大偏移量。</param>
    /// <param name="minOffset">初始最小偏移量。</param>
    /// <param name="growthLimit">段生长上限。</param>
    /// <param name="stableState">段稳态（建段中=Empty，就绪=Written 等）。</param>
    /// <param name="compactThreshold">区间表压缩阈值。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    internal Segment(
        int segId,
        long maxOffset,
        long minOffset = 0,
        long growthLimit = AlignmentConst.Alignment128M,
        StableState stableState = StableState.Ready,
        int compactThreshold = 256,
        ILogger? logger = null)
    {
        // ★ Hollow 哨兵用 -1 全字段构造（segId<0），放过校验；合法段必须 growthLimit > 0。
        //   不校验会静默建出 Invalid 段（IsValid=false），下游 EnsureSegmentsForLength 等死循环，
        //   根因极难定位（参数错位 bug 的教训）。
        if (segId >= 0 && growthLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(growthLimit), growthLimit,
                $"Segment {segId} 构造 growthLimit 必须 > 0（Hollow 哨兵除外）");
        if (segId >= 0 && maxOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(maxOffset), maxOffset,
                $"Segment {segId} 构造 maxOffset 必须 >= 0");
        if (segId >= 0 && minOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(minOffset), minOffset,
                $"Segment {segId} 构造 minOffset 必须 >= 0");
        // ★ 关系不变量（2026-08-14 事故增设：Recovery 元组位序错位把 growthLimit 塞进 minOffset 位，
        //   minOffset=1048576 > maxOffset=0 静默通过 → RealSize 为负 → reader/水位/容量计数全歪）。
        //   位序/取值错误在构造点当场爆炸，绝不带病入段表。
        if (segId >= 0 && (minOffset > maxOffset || maxOffset > growthLimit))
            throw new ArgumentException(
                $"Segment {segId} 构造关系不变量破坏：minOffset={minOffset} ≤ maxOffset={maxOffset} ≤ growthLimit={growthLimit} 不成立" +
                "（典型的参数位序错位——检查调用点）");

        SegId = segId;
        GrowthLimit = growthLimit;
        _minOffset = minOffset;
        _maxOffset = maxOffset;
        Volatile.Write(ref _stateCode, (int)stableState);
        // ★ 恢复期出生即物理就绪的段（Ready/Full 等）——闩直接 Set（单向闩永不回头，等待者零等待）
        if (stableState != StableState.Empty)
            _physicalReady.Set();
        _compactThreshold = compactThreshold;
        _logger = logger;
        EnsureCommittedSeed(maxOffset);
        logger?.LogInformation("Segment {SegId} constructed with StableState {StableState}, MaxOffset {MaxOffset}.",
            segId, stableState, maxOffset);
    }

    /// <summary>
    /// 初始化一个新的 <see cref="Segment"/> 实例（从 <see cref="SegmentSpec"/> 构造）。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="spec">段规格对象。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    public Segment(int segId, SegmentSpec spec, ILogger? logger = null)
        : this(segId, spec.MaxOffset, spec.MinOffset, spec.GrowthLimit, spec.StableState, logger: logger)
    {
    }
    /// <summary>
    /// 空段单例——所有字段用 -1 哨兵值，StableState.Invalid。
    /// <para>★ segId/growthLimit/maxOffset/realSize 全为 -1，与合法值 0 无歧义（segId=0 是真实首段）。</para>
    /// </summary>
    internal static readonly Segment Hollow = new(
        segId: -1, minOffset: -1, growthLimit: -1, maxOffset: -1,
        stableState: StableState.Invalid);
}