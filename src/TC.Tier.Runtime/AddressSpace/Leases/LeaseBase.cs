using System.Buffers;
using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>
/// lease 基类——公共机械（占住/多 chunk 协调/doneMask 终态仲裁/Dispose），不含操作类型语义。
/// <para>★ 每个操作类型是独立的类型化 lease 协议（<see cref="AppendLease"/>/<see cref="WriteLease"/>/
///   <see cref="ReclaimLease"/>/<see cref="ReclaimHeadLease"/>/<see cref="ReclaimTailLease"/>）——
///   类型即协议：终态收敛由子类 override <see cref="FinalizeTerminalCore"/> 表达，不做 kind 字节路由
///   （复拆：五合一的单体 OperationLease 抹掉了类型协议边界，上层怎么修都修不干净）。</para>
/// <para>★ chunk 终态仲裁（doneMask）与 Extents 机械在基类——与操作类型正交。</para>
/// </summary>
public abstract partial class LeaseBase : IDisposable, ITrackedLease
{
     private ILeaseSource _source = null!;
    /// <summary>lease 源（Reset 更新——子类经属性取用，不自持字段：readonly 子类字段在池化 Reset 时
    /// 无法更新，且基类构造期 RegisterLease(this) 会把子类字段未初始化的半成品发布给诊断线程）。</summary>
    private protected ILeaseSource Source => _source;
    /// <summary>日志（Reset 更新，子类钩子取用）。</summary>
    private protected ILogger? LeaseLogger => _logger;
    /// <summary>
    /// Lease 诊断信息（池化复用时每次 Reset 都重置）。
    /// </summary>
    private sealed class LeaseDiagnostics
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly long CreatedTimestampMs = Environment.TickCount64;
    }
    private ILogger? _logger;
    /// <summary>
    /// 保护 (ExtentsInternal, ChunkCountInternal) 字段对的诊断快照读（SegIds）与生命周期写（ReleaseExtents / Reset 发布）。
    /// <para>避免诊断枚举与 Commit/Rollback/Reset 并发时读到：撕裂的字段对、或已归还/复用的 ArrayPool 数组。</para>
    /// <para>仅 SegIds（诊断冷路径）与 Extents 发布点持锁；热路径（每 chunk）零锁。
    /// ★ SpinLock struct（4B 零堆分配）——默认工厂每次 new（对象小、池化成本更高），
    ///   每 lease 一次的锁对象分配是真实成本（设计决策收缩）。</para>
    /// </summary>
    private SpinLock _extentsSync;
    /// <summary>
    /// 内部访问：chunkCount（池化复用时每次 Reset 都重置）。
    /// </summary>
    private protected int ChunkCountInternal { get; private set; }
    /// <summary>
    /// 内部访问：extents（池化复用时每次 Reset 都重置）。
    /// </summary>
    private protected ExtentLease[] ExtentsInternal { get; private set; } = Array.Empty<ExtentLease>();
    /// <summary>
    /// chunk 终态位掩码——bit i = 1 ⇔ chunk i 的 chunk 级终态迁移（Commit 或 Rollback，终局不可逆）已发生。
    /// <para>★ 四条路径（部分提交/部分回滚/整体提交/整体回滚）共用 <see cref="TryMarkDone"/> 单一仲裁：
    ///   谁把 bit 从 0 置 1，谁执行该 chunk 的 extent 操作——跨所有路径 exactly-once。</para>
    /// <para>★ [0,64) chunk 零堆分配；超过 64 chunk 的 lease 用 <see cref="_doneOverflow"/>（Reset 时分配）。</para>
    /// </summary>
    private long _doneBits;
    /// <summary>终态位溢出字组——仅 <see cref="ChunkCountInternal"/> &gt; 64 时 Reset 内分配（8B / 64 chunk）。</summary>
    private long[]? _doneOverflow;
    private int _committedCount;
    private int _state = (int)LeaseState.Active;

    /// <summary>
    /// Lease 诊断信息（池化复用时每次 Reset 都重置）。
    /// </summary>
    private LeaseDiagnostics? _diag;

    /// <summary>
    /// Lease 唯一标识（池化复用时每次 Reset 都重置）。
    /// </summary>
    public Guid Id => (_diag ??= new LeaseDiagnostics()).Id;
    /// <summary>
    /// Lease 创建时间戳（池化复用时每次 Reset 都重置）。
    /// </summary>
    public long CreatedTimestampMs => (_diag ??= new LeaseDiagnostics()).CreatedTimestampMs;
    /// <summary>
    /// 起始逻辑地址（包含）。
    /// </summary>
    public LogicalAddress Start { get; private set; }

    /// <summary>
    /// 结束逻辑地址（不包含）。
    /// </summary>
    public LogicalAddress End { get; private set; }
    /// <summary>lease 状态（Active / Committed / RolledBack / Finalized——原子读）。</summary>
    public LeaseState State => (LeaseState)Volatile.Read(ref _state);
    internal int ChunkCount => ChunkCountInternal;

    /// <summary>
    /// Lease 占用的总长度（所有 chunk 的逻辑地址区间长度之和）。
    /// </summary>
    public long Length
    {
        get
        {
            long total = 0;
            // ★ Math.Min 防御：即便与 ReleaseExtents/Reset 并发（Extents 被换为 Array.Empty）也不越界。
            //   Length 不在诊断并发路径（仅 Reclaim 预处理读一次，Extents 此时稳定），此处仅作防御加固。
            var extents = ExtentsInternal;
            for (var i = 0; i < Math.Min(ChunkCountInternal, extents.Length); i++)
                total += extents[i].End - extents[i].Start;
            return total;
        }
    }

    IEnumerable<int> ITrackedLease.SegIds
    {
        get
        {
            // ★ 诊断快照——锁内把 OwnerSegId 拷进新建 int[]，避免与 ReleaseExtents/Reset 并发读到：
            //   (1) _chunkCount 与 Extents 撕裂（IndexOutOfRange）；
            //   (2) ArrayPool.Return(clearArray:true) 清零出的脏 0；
            //   (3) 被另一 lease 重新 Rent 的同一 buffer 的幻觉数据。
            // 拷贝目标是不经 ArrayPool 的 int[]，归还/复用永远碰不到它；持锁期仅字段读 + 拷贝（极短）。
            // 直接返回已物化的 int[]（而非 yield）——快照在访问瞬间原子完成，不再延迟到 .ToArray() 枚举。
            var lk = SpinLockScope.Enter(ref _extentsSync);
            try
            {
                var extents = ExtentsInternal;
                var n = Math.Min(ChunkCountInternal, extents.Length);
                var ids = new int[n];
                for (var i = 0; i < n; i++)
                    ids[i] = extents[i].OwnerSegId;
                return ids;
            }
            finally { lk.Dispose(); }
        }
    }

    /// <summary>
    /// 构造——占住 [start, end) 区间的所有 chunk（段表切分），并初始化状态。
    /// </summary>
    /// <param name="source">Lease 源对象</param>
    /// <param name="start">起始逻辑地址</param>
    /// <param name="end">结束逻辑地址</param>
    /// <param name="extentState">Extent 状态（操作类型各自的在途区间码）</param>
    /// <param name="logger">日志记录器</param>
    internal LeaseBase(
        ILeaseSource source,
        LogicalAddress start,
        LogicalAddress end,
        byte extentState,
        ILogger? logger = null)
    {
        Reset(source, start, end, extentState, logger);
    }

    /// <summary>
    /// 重置 lease 对象——占住 [start, end) 区间的所有 chunk（段表切分），并初始化状态（池化复用入口）。
    /// </summary>
    internal void Reset(
        ILeaseSource source,
        LogicalAddress start,
        LogicalAddress end,
        byte extentState,
        ILogger? logger = null)
    {
        _source = source;
        Start = start;
        End = end;
        _logger = logger;
        _state = (int)LeaseState.Active;
        _doneBits = 0;
        _doneOverflow = null;
        _committedCount = 0;
        _diag = null;

        // ★ 去掉 GetExtentCount 遍历——用预估 buffer 大小直接 Rent，AcquireExtentsForLease 返回实际 count。
        //   之前：GetExtentCount（遍历1）+ Rent + AcquireExtentsForLease（遍历2）= 2 次遍历。
        //   现在：Rent 预估 + AcquireExtentsForLease（遍历1）= 1 次遍历。
        //   预估 chunk 数：用 End-SegId - Start-SegId + 2（跨段数 + 头尾两个部分段）。
        //   单段 lease（最常见）预估=1-2，ArrayPool 命中最小桶零分配。
        var estimatedChunks = end.SegId - start.SegId + 2;
        if (estimatedChunks <= 0) estimatedChunks = 1;

        // ★ 用局部 buffer 占住 chunk——不提前发布到 Extents 字段，避免诊断枚举（SegIds）与 Reset 并发时
        //   读到半初始化数组或 _chunkCount/Extents 撕裂。占住完成后在锁内一次性原子发布。
        var extents = ArrayPool<ExtentLease>.Shared.Rent(estimatedChunks);

        int acquired;
        try
        {
            acquired = source.AcquireExtentsForLease(start, end, extentState, extents);
            // 预估不够（跨段多于预估）——扩容重试（罕见：极大 lease + 极小段）
            if (acquired > extents.Length)
            {
                ArrayPool<ExtentLease>.Shared.Return(extents, clearArray: true);
                extents = ArrayPool<ExtentLease>.Shared.Rent(acquired);
                acquired = source.AcquireExtentsForLease(start, end, extentState, extents);
            }
        }
        catch
        {
            // ★ 1.4：逐个回滚已占 chunk——try/catch 保护避免 Rollback 抛异常掩盖原始异常
            //   （与 Rollback / OnChunkRollback 风格一致）
            for (var i = 0; i < extents.Length; i++)
            {
                try { extents[i].Rollback(); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Reset 回滚 Extent 失败 segId={segId}", extents[i].OwnerSegId); }
            }
            ArrayPool<ExtentLease>.Shared.Return(extents, clearArray: true);
            throw;
        }

        // ★ 原子发布——锁内一次性替换 (ExtentsInternal, ChunkCountInternal)，旧 buffer 锁外归还（缩短临界区）。
        //   构造/池化复用入口此处旧 Extents 必为 Array.Empty（前序 ReleaseExtents 已清），故 stale 通常为 null。
        //   ★ 租用判定：ArrayPool.Rent 永不返回空数组 → ExtentsInternal ≠ Array.Empty ⟺ 已租用（无独立 bool 字段）。
        ExtentLease[]? stale;
        var pubLk = SpinLockScope.Enter(ref _extentsSync);
        try
        {
            stale = !ReferenceEquals(ExtentsInternal, Array.Empty<ExtentLease>()) ? ExtentsInternal : null;
            ExtentsInternal = extents;
            ChunkCountInternal = acquired;
        }
        finally { pubLk.Dispose(); }
        if (stale is not null)
            ArrayPool<ExtentLease>.Shared.Return(stale, clearArray: true);
        // ★ 终态位溢出字组——仅 >64 chunk 的 lease 才分配（罕见：单 lease 跨 64+ 段）。
        //   Reset 与 chunk 操作不并发（池化契约：归还后才 Reset），无需原子发布。
        if (acquired > 64)
            _doneOverflow = new long[(acquired + 63) >> 6];

        // ★ 只在诊断模式注册——生产模式（EnableDiagnostics=false）零开销
        if (source.EnableDiagnostics)
            source.RegisterLease(this);
    }

    /// <summary>
    /// 返回一个枚举器，该枚举器可用于遍历 lease 中的所有 chunk。
    /// </summary>
    /// <returns>返回一个 <see cref="ChunkEnumerator"/> 实例</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator GetEnumerator() => new(this);
    /// <summary>
    /// chunk 物理门——类型化协议各自表达对本 chunk 段稳态的要求（调用点：chunk 流水线第一拍 + 整体提交扫尾）。
    /// <para>★ 基类默认<b>无门</b>（Reclaim 系协议：打洞/截断/删段，不等物理就绪）。</para>
    /// <para>★ <see cref="AppendLease"/>/<see cref="WriteLease"/> override 为 Empty→Ready 物理门——
    ///   Append/Write 全部 chunk IO 与提交都必须等物理段（设计决策：协议要求不可混合进基类）。</para>
    /// </summary>
    /// <param name="ext">目标 chunk 的区间租约。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal virtual void EnterChunkPhysicalGate(ExtentLease ext)
    {
    }

    // ═══ 终态收敛（类型化协议——子类表达各自的整体级语义）═══

    /// <summary>
    /// lease 终态收敛——类型化协议钩子：子类路由到段表的本类型 Finalize 方法。
    /// <para>★ 无方向：Append/ReclaimHead/ReclaimTail 的物理操作在 lease 存在时已不可逆，
    ///   终态收敛与提交/回滚方向无关。Write/Reclaim 无终态收敛（整体级无段表副作用）——空实现。</para>
    /// <para>★ 幂等：被双触发（部分路径终态 + 整体路径）接近同时调两次也安全——
    ///   推尾 max-CAS / ShrinkHead 条件推进 / ShrinkTail 条件回退均幂等。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal abstract void FinalizeTerminalCore();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinalizeTerminal() => FinalizeTerminalCore();

    // ═══ chunk 提交/回滚（无锁多 chunk 协调——doneMask 单一仲裁）═══

    /// <summary>
    /// chunk 终态位仲裁——bit 0→1 的赢家获得该 chunk 的 extent 操作执行权（跨四路径 exactly-once）。
    /// <para>★ net8.0 无 Interlocked.Or（.NET 9+），CAS 循环置位；无竞争时一次 CAS。</para>
    /// </summary>
    /// <returns>true = 本调用赢得终态迁移权；false = 已终态（对端路径已赢）。</returns>
    private bool TryMarkDone(int index)
    {
        ref long word = ref (index < 64
            ? ref _doneBits
            : ref _doneOverflow![index >> 6]);
        long bit = 1L << (index & 63);
        long cur = Volatile.Read(ref word);
        while (true)
        {
            if ((cur & bit) != 0) return false;   // 已终态——对端路径已赢
            var prev = Interlocked.CompareExchange(ref word, cur | bit, cur);
            if (prev == cur) return true;
            cur = prev;
        }
    }

    /// <summary>
    /// 全部 chunk 是否已终态（方向可混合）——回滚路径的 Finalized 触发判据（设计文档 §2.5）。
    /// <para>★ 不用于 Committed 判定：提交线程的位可见先于其计数落地，"mask 满且 count&lt;N"会把全员提交误判为混合。</para>
    /// </summary>
    private bool AllDone()
    {
        var n = ChunkCountInternal;
        if (n <= 64)
            return Volatile.Read(ref _doneBits) == (n == 64 ? -1L : (1L << n) - 1);
        if (Volatile.Read(ref _doneBits) != -1L) return false;
        var words = _doneOverflow!;
        var lastBits = n & 63;
        var lastMask = lastBits == 0 ? -1L : (1L << lastBits) - 1;
        for (var w = 0; w < words.Length - 1; w++)
            if (Volatile.Read(ref words[w]) != -1L) return false;
        return Volatile.Read(ref words[^1]) == lastMask;
    }

    internal void OnChunkCommit(int index)
    {
        if (Volatile.Read(ref _state) != (int)LeaseState.Active) return;
        if (!TryMarkDone(index)) return;
        // ★ L15 修复（）：数组引用在 _extentsSync 内快照——TryMarkDone 赢位后可被抢占，
        //   并发整体 Commit()/Rollback() 已 ReleaseExtents（换 Empty + 归池清零），裸读会 IOoR/
        //   操作他人重租的 buffer。ExtentLease 为 readonly struct——快照后调用与数组生命周期解耦
        //   （陈旧段认知由 CompactVersion 哨兵拦截）。
        ExtentLease ext;
        var snapLk = SpinLockScope.Enter(ref _extentsSync);
        try { ext = ExtentsInternal[index]; }
        finally { snapLk.Dispose(); }
        ext.Commit();

        // ★ Committed 只由最后增量者触发（设计文档 §2.5）：Interlocked 全序使 count==N 的读
        //   传递看到此前所有置位与增量——count 满 ⟺ 全员提交，无需再查 mask。
        if (Interlocked.Increment(ref _committedCount) != ChunkCountInternal ||
            Interlocked.CompareExchange(ref _state, (int)LeaseState.Committed, (int)LeaseState.Active) !=
            (int)LeaseState.Active) return;
        FinalizeTerminal();
        ReleaseExtents();
    }

    internal void OnChunkRollback(int index)
    {
        if (Volatile.Read(ref _state) != (int)LeaseState.Active) return;
        if (!TryMarkDone(index)) return;
        // ★ L15 修复（同上）：锁内快照数组引用
        ExtentLease ext;
        var snapLk = SpinLockScope.Enter(ref _extentsSync);
        try { ext = ExtentsInternal[index]; }
        finally { snapLk.Dispose(); }
        try
        {
            ext.Rollback();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ExtentLease.Rollback 失败 segId={segId}", ext.OwnerSegId);
        }

        // ★ Finalized 只由回滚线程触发：本 chunk 永不增量 ⟹ count 上限 N-1 ⟹ 必为混合方向（设计文档 §2.5）。
        //   与 Committed 触发构造性互斥：存在任一回滚则 count 永远到不了 N。
        if (!AllDone() ||
            Interlocked.CompareExchange(ref _state, (int)LeaseState.Finalized, (int)LeaseState.Active) !=
            (int)LeaseState.Active) return;
        FinalizeTerminal();
        ReleaseExtents();
    }

    /// <summary>
    /// 释放租用的 Extent 数组（如果有），并重置相关状态。
    /// </summary>
    internal void ReleaseExtents()
    {
        // ★ 字段变更与 SegIds 快照互斥——归还池的 Return 放锁外（缩短临界区；此时字段已指向 Array.Empty，
        //   新读者拿不到该 buffer，已持快照 int[] 的读者更不受影响）。
        ExtentLease[]? toRelease;
        var relLk = SpinLockScope.Enter(ref _extentsSync);
        try
        {
            toRelease = !ReferenceEquals(ExtentsInternal, Array.Empty<ExtentLease>()) ? ExtentsInternal : null;
            ExtentsInternal = Array.Empty<ExtentLease>();
            ChunkCountInternal = 0;
        }
        finally { relLk.Dispose(); }

        if (toRelease is not null)
            ArrayPool<ExtentLease>.Shared.Return(toRelease, clearArray: true);
    }

    // ═══ 整体 Commit/Rollback/Dispose ═══

    /// <summary>
    /// 提交整个 Lease——批量扫尾（对未终态 chunk 执行与部分提交完全相同的操作）+ 终态收敛。
    /// <para>★ 不是第二种原子性：原子单元是 chunk，本方法只是批量（设计文档 §1.2）。</para>
    /// <para>★ TryMarkDone 仲裁：已终态（含并发刚终态）的 chunk 跳过——exactly-once，不重放。</para>
    /// <para>★ 物理门不变量（lease-protocol §1.3）：chunk IO 未执行过的 chunk，扫尾提交前必须先过
    ///   <see cref="EnterChunkPhysicalGate"/>（与 chunk 流水线第一拍同一道 Empty→Ready 门）——已 Ready 段
    ///   走快路径立即返回，常态（流水线已全部提交）零开销。</para>
    /// </summary>
    public void Commit()
    {
        if (Interlocked.CompareExchange(ref _state, (int)LeaseState.Committed, (int)LeaseState.Active) !=
            (int)LeaseState.Active) return;
        for (var i = 0; i < ChunkCountInternal; i++)
            if (TryMarkDone(i))
            {
                EnterChunkPhysicalGate(ExtentsInternal[i]);
                ExtentsInternal[i].Commit();
            }
        FinalizeTerminal();
        ReleaseExtents();
    }

    /// <summary>
    /// 回滚整个 Lease——对称扫尾（只回滚未终态 chunk；已提交的 chunk 终态不可逆，跳过）+ 终态收敛。
    /// </summary>
    public void Rollback()
    {
        if (Interlocked.CompareExchange(ref _state, (int)LeaseState.RolledBack, (int)LeaseState.Active) !=
            (int)LeaseState.Active) return;
        for (var i = 0; i < ChunkCountInternal; i++)
        {
            if (!TryMarkDone(i)) continue;
            try
            {
                ExtentsInternal[i].Rollback();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ExtentLease.Rollback 失败 segId={segId}", ExtentsInternal[i].OwnerSegId);
            }
        }

        FinalizeTerminal();
        ReleaseExtents();
    }

    /// <summary>
    /// 释放 lease 对象，并在必要时回滚未提交的 chunk。
    /// </summary>
    public void Dispose()
    {
        // ★ 只在诊断模式注销——生产模式不访问 Id（避免触发 LeaseDiagnostics 构造 32B）
        if (_source.EnableDiagnostics)
            _source.UnregisterLease(Id);
        if (Volatile.Read(ref _state) != (int)LeaseState.Active) return;
        Rollback();
        GC.SuppressFinalize(this);
    }
}
