using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.Hazards;

/// <summary>
/// 线程在 <see cref="HazardDomain"/> 的注册句柄——线程身份的载体（域内线程表条目索引）。
/// <para>★ Register 幂等：同线程同域重复调用返回<b>同一实例</b>（canonical 实例取代引用计数——
///   Dispose 一次即配对，无需嵌套计数）。Dispose 必须在注册线程调用（同线程注册链仅属主线程访问）。</para>
/// </summary>
public sealed class HazardRegistration : IDisposable
{
    internal HazardDomain Domain = null!;
    /// <summary>线程表条目索引（1..MaxThreads，0 保留）。</summary>
    internal int EntryIndex;

    /// <summary>同线程注册链（跨域；仅属主线程读写）。</summary>
    internal HazardRegistration? NextSameThread;

    /// <summary>属主线程 Id（DEBUG：跨线程 Dispose/使用绊线基准）。</summary>
    internal int OwnerThreadId;

    internal int _disposed;

    internal HazardRegistration(HazardDomain domain, int entryIndex)
    {
        Domain = domain;
        EntryIndex = entryIndex;
        OwnerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>注销：清空全部 hazard 槽并释放条目（供新线程抢占）。与 <see cref="HazardDomain.Register"/> 配对。
    /// 必须在注册线程调用。★ DEBUG 绊线：持保护注销 / 跨线程注销 / 双重 Dispose 一律抛异常。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
#if DEBUG
            Interlocked.Exchange(ref _disposed, 0);   // 回滚标志——绊线抛出后现场可恢复、可重试
            throw new InvalidOperationException(
                $"[HazardPointers] HazardRegistration 双重 Dispose（entry={EntryIndex}）——Register/Dispose 必须严格配对");
#else
            return;
#endif
        }
        try { Domain.ReleaseRegistration(this); }
        catch
        {
            // ★ 绊线抛出前回滚标志——持保护注销等违约现场可修复后重试（Unprotect → Dispose）。
            //   ReleaseRegistration 的校验全部先于状态改写，此处无半途状态。
            Interlocked.Exchange(ref _disposed, 0);
            throw;
        }
    }
}

/// <summary>
/// HazardPointers 原语——无锁结构的<b>退休安全</b>回收层（与 <see cref="Epochs.LightEpoch"/> 同层级的通用机制）。
/// <para>语义：读者在解引用前 <see cref="TryProtect"/>（发布 + 验证来源未变）取得对<b>具体句柄</b>的保护；
/// 删除者物理摘除后 <see cref="Retire"/>；<see cref="Scan"/> 快照全部 hazard 槽，无指向者的退休项执行
/// reclaim（结构性恰好一次）并复用。</para>
/// <para>★ 获取纪律（设计 §3.3，正确性核心）：只发布不验证保护不了任何东西——扫描可能在发布前已过。
/// TryProtect 内建「发布 → 重读来源验证」，验证失败 = 正常竞态以新值重试，<b>不构成错误</b>。</para>
/// <para>★ 活性契约（设计 §3.4）：水位顺带扫描是策略非保证——依赖回收前进的调用方（槽池等）必须在
/// 资源耗尽路径显式 <see cref="Scan"/>（本原语在退休记录池耗尽时自动强制）。</para>
/// <para>★ reclaim 契约：恰好执行一次（结构性保证，<b>禁止调用方假设幂等</b>）；必须纯内存、无异常、
/// 不阻塞。reclaim 内允许 Retire（扫描互斥可重入 + 分层快照缓冲，不自锁）。</para>
/// <para>★ 生命周期：.NET 无线程退出回调——长线程负责 Register/Dispose 配对；瞬态线程漏注销的后果是
/// <b>退休滞留</b>（悬挂 hazard，非内存损坏），由水位看门狗检测。</para>
/// </summary>
public sealed unsafe class HazardDomain : IDisposable
{
    private const int kCacheLineBytes = 64;

    /// <summary>64B 条目内 hazard 槽数上限（7×8B + 4B ThreadId + 4B 保留 = 64B）。</summary>
    private const int kMaxSlotsPerThread = 7;

    /// <summary>链头/空闲栈的空标记（记录索引 0 保留，故任何合法 (idx&lt;&lt;32|tag) 恒非 0）。</summary>
    private const long kEmptyLink = 0;

    /// <summary>重入扫描的预分配快照缓冲层数（reclaim→Retire→池空→Scan 的嵌套深度上限）。</summary>
    private const int kMaxScanDepth = 4;

    /// <summary>
    /// 线程表条目（一槽一缓存行）。hazard 槽 ×K + ThreadId。释放序：先清 hazard 再置 ThreadId=0
    /// （volatile 配对）——扫描者见 ThreadId=0 即知 hazard 已清，见 ThreadId≠0 则如实快照其 hazard。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = kCacheLineBytes)]
    private struct Entry
    {
        [FieldOffset(0)] public fixed long Hazards[kMaxSlotsPerThread];
        [FieldOffset(56)] public int ThreadId;
        [FieldOffset(60)] public int Reserved;
    }

    // ════════════════════════════════════════════════════════════
    //  实例状态
    // ════════════════════════════════════════════════════════════

    /// <summary>★ POH 数组必须持强引用——GC.AllocateArray(pinned:true) 只保证不移动不保证不回收
    /// （LightEpoch L124-126 教训：只存裸指针会被 GC 回收 → 悬挂）。</summary>
    private readonly Entry[] _tableRaw;
    private readonly Entry* _tableAligned;
    private readonly int _maxThreads;              // 可用条目 1.._maxThreads（0 保留）
    private readonly int _slotsPerThread;
    private readonly int _retireThreshold;

    /// <summary>退休记录池（预分配——Retire/Scan 零分配）。记录 1.._retireCapacity（0 保留为空标记）。</summary>
    private readonly int _retireCapacity;
    private readonly long[] _retireRefs;
    private readonly Action<long>?[] _retireActions;
    private readonly int[] _retireNext;            // 退休链 next（记录在链上的后继）
    private readonly int[] _freeNext;              // 空闲记录栈 next
    private long _retireHead;                      // (idx<<32)|tag——Treiber 头，tag 防 ABA
    private long _freeHead;                        // (idx<<32)|tag——空闲栈头，tag 防 ABA
    private long _retiredTotal;
    private long _reclaimedTotal;

    /// <summary>并发 Scan 互斥（Monitor 可重入——reclaim 内 Retire→池空→Scan 不自锁）。</summary>
    private readonly object _scanGate = new();

    /// <summary>分嵌套深度的快照缓冲（每线程独立于深度键——重入 Scan 各用各的缓冲，互不覆写）。
    /// 深于 <see cref="kMaxScanDepth"/> 的嵌套回退为临时分配（异常用法，罕见路径）。</summary>
    private readonly long[][] _scanSnapshots;
    private readonly int _snapshotSize;

    [ThreadStatic] private static int t_scanDepth;

    /// <summary>同线程跨域注册链（Register 幂等查找用；仅属主线程读写）。</summary>
    [ThreadStatic] private static HazardRegistration? t_registrations;

    private int _disposed;

    /// <summary>当前线程在本域注册上下文（幂等——同线程同域返回同一实例）。</summary>
    public int MaxThreads => _maxThreads;

    /// <summary>每线程 hazard 槽数。</summary>
    public int SlotsPerThread => _slotsPerThread;

    /// <summary>水位阈值（退休数达到即顺带触发 Scan）。</summary>
    public int RetireThreshold => _retireThreshold;

    /// <summary>当前未回收退休数（链上 + 在途私批 = 累计退休 − 累计回收）——水位看门狗用。</summary>
    public long RetiredCount
    {
        get
        {
            var r = Interlocked.Read(ref _retiredTotal);
            var c = Interlocked.Read(ref _reclaimedTotal);
            return r - c;
        }
    }

#if DEBUG
    /// <summary>Phase 2 F1 仪器支撑：填充当前注册的全部 hazard 槽值到 <paramref name="values"/>
    ///（不足处以 0 填充）。消费方解引用点校验用它断言"目标 ∈ 当前线程 hazard 集"。</summary>
    internal void DebugFillHazards(HazardRegistration reg, Span<long> values)
    {
        var e = _tableAligned + reg.EntryIndex;
        var n = Math.Min(_slotsPerThread, values.Length);
        for (var j = 0; j < n; j++) values[j] = Volatile.Read(ref e->Hazards[j]);
        for (var j = n; j < values.Length; j++) values[j] = 0;
    }
#endif

#if DEBUG
    // ══ 常设 Debug 仪器（Release 零开销）——值示波器：协议违反异常自动携带最近操作历史 ══
    private readonly (string op, int tid, int entry, long retireHead, long freeHead, long retired, long reclaimed)[]
        _ops = new (string, int, int, long, long, long, long)[32];
    private int _opsIdx;

    private void TraceOp(string op, int entry = 0)
    {
        var i = Interlocked.Increment(ref _opsIdx) - 1;
        _ops[i % _ops.Length] = (op, Environment.CurrentManagedThreadId, entry,
            Volatile.Read(ref _retireHead), Volatile.Read(ref _freeHead),
            Interlocked.Read(ref _retiredTotal), Interlocked.Read(ref _reclaimedTotal));
    }

    private string OpsDump()
    {
        var sb = new System.Text.StringBuilder();
        var end = Volatile.Read(ref _opsIdx);
        var start = Math.Max(0, end - _ops.Length);
        for (var i = start; i < end; i++)
        {
            var e = _ops[i % _ops.Length];
            sb.Append($"\n  [{i}] {e.op} T{e.tid} entry={e.entry} rHead={e.retireHead:X} fHead={e.freeHead:X} " +
                      $"retired={e.retired} reclaimed={e.reclaimed}");
        }
        return sb.ToString();
    }

    [DoesNotReturn]
    private void ThrowViolation(string detail, string hint)
        => throw new InvalidOperationException(
            $"[HazardPointers] 协议违反：{detail}（线程={Environment.CurrentManagedThreadId}，" +
            $"retired={Interlocked.Read(ref _retiredTotal)}，reclaimed={Interlocked.Read(ref _reclaimedTotal)}）。{hint}" +
            $"\n── 最近协议操作历史（示波器）──{OpsDump()}");
#endif

    // ════════════════════════════════════════════════════════════
    //  构造
    // ════════════════════════════════════════════════════════════

    /// <summary>创建 HP 域。一个域可服务一个或多个无锁结构（推荐共享——扫描按域摊销）。</summary>
    /// <param name="maxThreads">线程表容量；0 = max(128, 2×CPU)（LightEpoch KTableSize 同式）。</param>
    /// <param name="hazardSlotsPerThread">每线程 hazard 槽数（1..7；链表遍历 2 槽轮换）。</param>
    /// <param name="retireThreshold">水位阈值（默认 64）。</param>
    /// <param name="retireCapacity">退休记录池容量；0 = max(4096, 16×阈值)。</param>
    public HazardDomain(int maxThreads = 0, int hazardSlotsPerThread = 2,
        int retireThreshold = 64, int retireCapacity = 0)
    {
        if (maxThreads <= 0) maxThreads = Math.Max(128, Environment.ProcessorCount * 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(hazardSlotsPerThread, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hazardSlotsPerThread, kMaxSlotsPerThread);
        ArgumentOutOfRangeException.ThrowIfLessThan(retireThreshold, 1);
        if (retireCapacity <= 0) retireCapacity = Math.Max(4096, retireThreshold * 16);

        _maxThreads = maxThreads;
        _slotsPerThread = hazardSlotsPerThread;
        _retireThreshold = retireThreshold;
        _retireCapacity = retireCapacity;

        _tableRaw = GC.AllocateArray<Entry>(maxThreads + 2, pinned: true);
        var p = (long)Unsafe.AsPointer(ref _tableRaw[0]);
        _tableAligned = (Entry*)((p + (kCacheLineBytes - 1)) & ~(long)(kCacheLineBytes - 1));

        _retireRefs = new long[retireCapacity + 1];
        _retireActions = new Action<long>?[retireCapacity + 1];
        _retireNext = new int[retireCapacity + 1];
        _freeNext = new int[retireCapacity + 1];
        for (var i = 1; i < retireCapacity; i++) _freeNext[i] = i + 1;
        _freeNext[retireCapacity] = 0;                     // 栈底（空标记）
        _freeHead = (1L << 32) | 1;                        // 栈顶 = 记录 1，tag = 1

        _snapshotSize = maxThreads * hazardSlotsPerThread;
        _scanSnapshots = new long[kMaxScanDepth][];
        for (var i = 0; i < kMaxScanDepth; i++) _scanSnapshots[i] = new long[_snapshotSize];
    }

    /// <summary>清理域。★ DEBUG 绊线：<b>悬挂 hazard</b>（漏注销的实质危害——退休永久滞留）或
    /// 退休链未清空（泄漏）一律抛异常。瞬态线程池线程的<b>净占用</b>（hazard 全空）是设计 §3.2
    /// 允许的隐式滞留——只损失表容量，不产生滞留，不绊。调用方须保证 Dispose 时无并发操作。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
#if DEBUG
        var dangling = new List<(int entry, int tid, long hazard)>();
        for (var i = 1; i <= _maxThreads; i++)
        {
            var e = _tableAligned + i;
            var tid = Volatile.Read(ref e->ThreadId);
            if (tid == 0) continue;
            for (var j = 0; j < _slotsPerThread; j++)
            {
                var h = Volatile.Read(ref e->Hazards[j]);
                if (h != 0) { dangling.Add((i, tid, h)); break; }
            }
        }
        if (dangling.Count > 0)
            ThrowViolation(
                $"Dispose 时仍有 {dangling.Count} 个悬挂 hazard（{string.Join("，", dangling.Select(d => $"entry {d.entry} tid={d.tid} hazard={d.hazard}"))}）",
                "操作收尾必须清空 hazard（Unprotect/Publish 0）——悬挂值 = 退休永久滞留（F2）。");
        var leftover = RetiredCount;
        if (leftover > 0)
            ThrowViolation($"Dispose 时退休链尚有 {leftover} 项未回收（泄漏/漏 Scan）",
                "风暴后应扫描至 RetiredCount==0 再 Dispose（F7 守恒）。");
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  注册（线程表）
    // ════════════════════════════════════════════════════════════

    /// <summary>当前线程在本域注册上下文。幂等：同线程同域重复调用返回同一实例。</summary>
    /// <exception cref="ObjectDisposedException">域已释放。</exception>
    public HazardRegistration Register()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var tid = Environment.CurrentManagedThreadId;
        for (var r = t_registrations; r != null; r = r.NextSameThread)
            if (ReferenceEquals(r.Domain, this) && Volatile.Read(ref r._disposed) == 0)
                return r;

        var entry = ClaimEntry(tid);
        var reg = new HazardRegistration(this, entry) { NextSameThread = t_registrations };
        t_registrations = reg;
#if DEBUG
        TraceOp("Register", entry);
#endif
        return reg;
    }

    /// <summary>以 CAS 在域内线程表抢条目（LightEpoch ReserveEntry 同型：探测 + 满表 Yield 重试）。</summary>
    private int ClaimEntry(int tid)
    {
        var start = 1 + (int)((uint)(tid * 2654435761) % (uint)_maxThreads);
        var i = start;
        while (true)
        {
            if (Interlocked.CompareExchange(ref (_tableAligned + i)->ThreadId, tid, 0) == 0)
                return i;
            i = (i % _maxThreads) + 1;
            if (i == start) Thread.Yield();   // 一圈未得——满表让位重试
        }
    }

    /// <summary>注销（属主线程）：清 hazard → 释放条目 → 摘出同线程链。★ 释放序保证扫描者安全。</summary>
    internal void ReleaseRegistration(HazardRegistration reg)
    {
        var e = _tableAligned + reg.EntryIndex;
#if DEBUG
        if (reg.OwnerThreadId != Environment.CurrentManagedThreadId)
            ThrowViolation($"跨线程 Dispose 注册（属主={reg.OwnerThreadId}，当前={Environment.CurrentManagedThreadId}，entry={reg.EntryIndex}）",
                "HazardRegistration.Dispose 必须在注册线程调用（同线程注册链仅属主可写）。");
        if (Volatile.Read(ref e->ThreadId) != reg.OwnerThreadId)
            ThrowViolation($"条目已被抢占/异常状态（entry={reg.EntryIndex}，期望 tid={reg.OwnerThreadId}，" +
                           $"实际={Volatile.Read(ref e->ThreadId)}）", "注册生命周期被外部破坏。");
        for (var j = 0; j < _slotsPerThread; j++)
            if (Volatile.Read(ref e->Hazards[j]) != 0)
                ThrowViolation($"持保护注销（entry={reg.EntryIndex}，slot={j}，hazard={Volatile.Read(ref e->Hazards[j])}）",
                    "注销前必须解除全部保护（Unprotect）——悬挂 hazard = 退休永久滞留。");
#endif
        for (var j = 0; j < _slotsPerThread; j++)
            Volatile.Write(ref e->Hazards[j], 0);
        Volatile.Write(ref e->ThreadId, 0);
        HazardRegistration? prev = null;
        for (var r = t_registrations; r != null; prev = r, r = r.NextSameThread)
        {
            if (!ReferenceEquals(r, reg)) continue;
            if (prev == null) t_registrations = r.NextSameThread;
            else prev.NextSameThread = r.NextSameThread;
            break;
        }
#if DEBUG
        TraceOp("Release", reg.EntryIndex);
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  保护发布（热路径）
    // ════════════════════════════════════════════════════════════

    /// <summary>注册与槽位校验（常开——非 DEBUG-only）：写错域/越界槽 = 静默写他域条目或越界，
    /// 比协议违反严重一档，代价各一条比较。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entry* EntryOf(HazardRegistration reg, int hazardSlot)
    {
        ArgumentNullException.ThrowIfNull(reg);
        if (!ReferenceEquals(reg.Domain, this))
            ThrowWrongDomain(reg);
        if ((uint)hazardSlot >= (uint)_slotsPerThread)
            ThrowSlotOutOfRange(hazardSlot);
#if DEBUG
        if (Volatile.Read(ref reg._disposed) != 0)
            ThrowViolation($"使用已注销的注册（entry={reg.EntryIndex}）", "Dispose 后不得再 Publish/TryProtect。");
        if (Volatile.Read(ref (_tableAligned + reg.EntryIndex)->ThreadId) != Environment.CurrentManagedThreadId)
            ThrowViolation($"跨线程使用注册（entry={reg.EntryIndex}，属主={reg.OwnerThreadId}，" +
                           $"当前={Environment.CurrentManagedThreadId}）", "hazard 槽是线程私有资源。");
#endif
        return _tableAligned + reg.EntryIndex;
    }

    [DoesNotReturn]
    private void ThrowWrongDomain(HazardRegistration reg)
        => throw new InvalidOperationException(
            $"[HazardPointers] 注册不属于本域（entry={reg.EntryIndex}）——多域共存时各域注册不可混用，" +
            $"静默后果是写他域线程表条目。");

    [DoesNotReturn]
    private void ThrowSlotOutOfRange(int hazardSlot)
        => throw new ArgumentOutOfRangeException(nameof(hazardSlot), hazardSlot,
            $"hazard 槽越界（合法 0..{_slotsPerThread - 1}，本域配置 {_slotsPerThread} 槽/线程）");

    /// <summary>
    /// 获取保护（发布 + 验证——正确性内建，设计 §3.3）。
    /// <para>语义：读 <paramref name="source"/> → 发布 → <b>重读 source 验证未变</b>；验证失败 = 正常竞态，
    /// 以最新值重试；source 为空返回 false。返回 true 时 <paramref name="slotRef"/> 已验证新鲜，
    /// 此后才允许解引用。</para>
    /// <para>★ 为什么必须有验证：只发布不验证，保护的是"过去读到的值"——扫描可能在发布前已过
    /// （读者读边 → 删除者摘除+退休+扫描+回收 → 读者才发布 → 解引用读到复用后数据）。验证读返回旧值
    /// ⟹ 摘除尚未发生 ⟹ 其后的扫描必见本次发布。与消费方"先物理摘除后 Retire"的程序序配对成立。</para>
    /// </summary>
    /// <param name="reg">本域注册。</param>
    /// <param name="hazardSlot">hazard 槽位（线程内轮换，如链表遍历 curr/next 两槽）。</param>
    /// <param name="source">共享来源位置（如结构边）。</param>
    /// <param name="slotRef">输出的被保护句柄（0 = 来源为空）。</param>
    public bool TryProtect(HazardRegistration reg, int hazardSlot, ref long source, out long slotRef)
    {
        var e = EntryOf(reg, hazardSlot);
        var v = Volatile.Read(ref source);
        while (v != 0)
        {
            Volatile.Write(ref e->Hazards[hazardSlot], v);            // ① 发布
            if (Volatile.Read(ref source) == v) { slotRef = v; return true; }   // ② 验证
            v = Volatile.Read(ref source);                            // 来源已变——正常竞态，以新值重试
        }
        Volatile.Write(ref e->Hazards[hazardSlot], 0);                // 清残留（失败方向只会"多留一轮"，但顺手清）
        slotRef = 0;
        return false;
    }

    /// <summary>裸发布：仅限指针新鲜性已有结构性证明的场景（如对同一已验证引用换槽重发布）。
    /// 常规获取一律走 <see cref="TryProtect"/>。slotRef = 0 即清空（等价 <see cref="Unprotect"/>）。</summary>
    public void Publish(HazardRegistration reg, int hazardSlot, long slotRef)
    {
        var e = EntryOf(reg, hazardSlot);
        Volatile.Write(ref e->Hazards[hazardSlot], slotRef);
    }

    /// <summary>解除保护（等价 Publish(reg, hazardSlot, 0)）。</summary>
    public void Unprotect(HazardRegistration reg, int hazardSlot)
    {
        var e = EntryOf(reg, hazardSlot);
        Volatile.Write(ref e->Hazards[hazardSlot], 0);
    }

    // ════════════════════════════════════════════════════════════
    //  退休（MPSC Treiber 栈）与扫描
    // ════════════════════════════════════════════════════════════

    /// <summary>登记退休（1 CAS push 即返）。达到水位顺带触发 Scan（策略非保证，见活性契约）。
    /// <para>★ reclaim 恰好执行一次由 Scan 结构性保证；delegate 由调用方缓存（零分配纪律）；
    /// 必须纯内存、无异常、不阻塞。记录池耗尽时自动强制 Scan 推进（活性兜底）。</para></summary>
    public void Retire(long slotRef, Action<long> reclaim)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(reclaim);
        var rec = PopFreeRecord();
        _retireRefs[rec] = slotRef;
        _retireActions[rec] = reclaim;
        while (true)
        {
            var h = Volatile.Read(ref _retireHead);
            _retireNext[rec] = (int)(h >> 32);
            if (Interlocked.CompareExchange(ref _retireHead, ((long)rec << 32) | ((uint)h + 1), h) == h)
                break;
        }
#if DEBUG
        TraceOp("Retire");
#endif
        if (Interlocked.Increment(ref _retiredTotal) - Volatile.Read(ref _reclaimedTotal) >= _retireThreshold)
            Scan();
    }

    /// <summary>弹出一条空闲退休记录；池空 → 强制 Scan 推进（活性契约——HP 下回收常是资源唯一来源）。</summary>
    private int PopFreeRecord()
    {
        while (true)
        {
            var h = Volatile.Read(ref _freeHead);
            var idx = (int)(h >> 32);
            if (idx == 0)
            {
                Scan();          // Monitor 可重入：reclaim 栈内 Retire→池空→Scan 不自锁
                Thread.Yield();
                continue;
            }
            var next = _freeNext[idx];   // idx 在栈上期间值稳定（仅出栈后的持有者会写）
            if (Interlocked.CompareExchange(ref _freeHead, ((long)next << 32) | ((uint)h + 1), h) == h)
                return idx;
        }
    }

    private void PushFreeRecord(int rec)
    {
        while (true)
        {
            var h = Volatile.Read(ref _freeHead);
            _freeNext[rec] = (int)(h >> 32);
            if (Interlocked.CompareExchange(ref _freeHead, ((long)rec << 32) | ((uint)h + 1), h) == h)
                return;
        }
    }

    /// <summary>
    /// 扫描（整链交换私批——设计 §3.4）：① Exchange 原子摘走整条退休链（并发 push 只挂新链）
    /// → ② 快照全部 hazard 槽（排序）→ ③ 私批比对：无指向者执行 reclaim 并复用记录；幸存者 CAS 推回。
    /// <para>★ 恰好一次是结构保证：批次脱离共享链 + 交换原子 + 并发 Scan 经 <c>_scanGate</c> 串行
    /// ——不依赖 reclaim 幂等。快照缓冲按嵌套深度分层（重入 Scan 各用各的，互不覆写）。</para>
    /// <para>★ 快照读到瞬时值只会"多留一轮"不会漏保护：发布（release 写）与验证读的配对保证
    /// "验证成功 ⟹ 后续扫描必见发布"。</para>
    /// </summary>
    public void Scan()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var depth = t_scanDepth;
        long[] snap;
        if (depth < _scanSnapshots.Length) snap = _scanSnapshots[depth];
        else snap = new long[_snapshotSize];   // 深嵌套回退（异常用法，罕见路径）
        t_scanDepth = depth + 1;
        try
        {
            lock (_scanGate)
            {
                var h = Interlocked.Exchange(ref _retireHead, kEmptyLink);
                if (h == kEmptyLink) return;

                var n = 0;
                for (var i = 1; i <= _maxThreads; i++)
                {
                    var e = _tableAligned + i;
                    if (Volatile.Read(ref e->ThreadId) == 0) continue;   // 空条目 hazard 已清（释放序）
                    for (var j = 0; j < _slotsPerThread; j++)
                    {
                        var v = Volatile.Read(ref e->Hazards[j]);
                        if (v != 0) snap[n++] = v;
                    }
                }
                Array.Sort(snap, 0, n);

                var rec = (int)(h >> 32);
                while (rec != 0)
                {
                    var next = _retireNext[rec];        // 先取 next（rec 随即可能入池复用）
                    var r = _retireRefs[rec];
                    if (Array.BinarySearch(snap, 0, n, r) < 0)
                    {
                        var action = _retireActions[rec]!;
                        _retireActions[rec] = null;
                        try
                        {
#if DEBUG
                            try { action(r); }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException(
                                    $"[HazardPointers] reclaim action 抛异常（slotRef={r}）——reclaim 必须纯内存、无异常、不阻塞。",
                                    ex);
                            }
#else
                            action(r);
#endif
                        }
                        finally
                        {
                            PushFreeRecord(rec);
                            Interlocked.Increment(ref _reclaimedTotal);
                        }
                    }
                    else
                    {
                        // 幸存者：CAS 推回（与并发 Retire push 同场竞争；tag 防 ABA）
                        while (true)
                        {
                            var ch = Volatile.Read(ref _retireHead);
                            _retireNext[rec] = (int)(ch >> 32);
                            if (Interlocked.CompareExchange(
                                    ref _retireHead, ((long)rec << 32) | ((uint)ch + 1), ch) == ch)
                                break;
                        }
                    }
                    rec = next;
                }
            }
        }
        finally { t_scanDepth = depth; }
#if DEBUG
        TraceOp("Scan");
#endif
    }
}
