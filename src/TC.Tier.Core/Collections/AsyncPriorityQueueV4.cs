using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.Hazards;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Collections;

/// <summary>
/// ★ 实验版本（Route B' V4——HazardPointers 回收层验证版）——**不可用于生产**：仅保留作设计档案
/// 与对照实验；生产一律用 <see cref="AsyncPriorityQueue{T}"/>（Route A 基线）。测试默认 Skip。
/// <para>PQ V4 = Route A marker 删除协议的 slot 世界移植 + HazardPointers 回收层 + 零分配。
/// 相对 V2（epoch 版）的替换：读路径
/// TryProtect（发布+验证）/ Retire MPSC / Scan 整链交换 / 池空强制扫描。</para>
/// <para><b>实现固化的四个协议决策（对设计稿的修正/细化，逐条有论证）</b>：</para>
/// <list type="bullet">
/// <item><b>位置性 walk 保持正确性承重</b>（设计稿曾拟降级为 DEBUG 仪器——论证有缺口）：可达入边
/// 必须在 Retire 前摘除。不可达的陈旧边无害（TryProtect 验证的来源必是可达边）；但可达残余边会让
/// 读者验证通过后解引用已回收槽 → CheckGen 异常风暴。Route A 的位置性 Find（走链顺带摘除一切已标
/// 节点）就是完备的 walk——胜者出队后复用它。</item>
/// <item><b>mark 位编码进边值（bit63）</b>而非目标槽 Kind："curr 已删？"成为纯值判断（对应 Route A
/// 的 <c>is Marker</c> 类型判断 1:1），marker 槽仅在真正读 NextSlot 时解引用。hazard 值一律存 plain
/// 引用（scan 匹配退休记录）——边获取走 <see cref="TryProtectEdge"/>（规范化发布+验证）。</item>
/// <item><b>Route A 发布形态：全部自身边先写完再发布 level-0</b>——V2 的 LinkState Linking/Done 门
/// 是其交错写边形态的产物，本形态不需要（保留 Done 常值 + 消费侧绊线）。高层 best-effort 链接的
/// CAS 基（succs[L]）会被删除 walk 先行失效——迟到链接必然失败，无残余入边。</item>
/// <item><b>marker 级联退休</b>：markers 不由胜者直接退休，而由 victim 的 reclaim action 级联
/// （victim 各层冻结边是 marker 的唯一引用源）。持有 victim hazard 的读者可安全读其冻结边取
/// NextSlot——victim 未回收 ⟹ 其 markers 未退休。级联 Retire 在 reclaim 内——Phase 1 原语的
/// 重入扫描（Monitor + 分层快照缓冲）正是为此设计。</item>
/// <item><b>遇已删 pred 不跟随冻结边、从 head 重走本层</b>（Route A 靠 GC 跟随；HP 下冻结路径的
/// 末端节点无法经未冻结来源验证活性）。活性由 helping 保证：每次重走时已删段只会更接近被摘除。</item>
/// </list>
/// <para>★ 零分配热路径：侵入式空闲链、stackalloc preds/succs、缓存 reclaim delegate、
/// [ThreadStatic] xorshift、域内预分配扫描缓冲。</para>
/// <para>★ 约束（原型）：固定容量（nodes + markers 共享池）；generation 16 位（回绕窗口由
/// fail-visible 兜底）；HazardDomain 必须注入且 slotsPerThread ≥ 2。</para>
/// </summary>
/// <typeparam name="T">队列中存储的元素类型。</typeparam>
[SuppressMessage("Naming", "CA1711:标识符应采用正确的后缀")]
[Experimental("TCTier001")]
internal sealed class AsyncPriorityQueueV4<T> : IDisposable
{
    // ════════════════════════════════════════════════════════════
    //  槽位与常量
    // ════════════════════════════════════════════════════════════

    private const byte KindNode = 0;
    private const byte KindMarker = 1;
    private const byte LinkDone = 0;
    private const int GenShift = 16;
    private const int GenMask = 0xFFFF;

    /// <summary>边值 mark 位：置位 = 目标是 marker（victim 该层已逻辑删除）。slotRef 占低 63 位。</summary>
    private const long MarkBit = 1L << 63;

    private const int HeadIndex = 0;
    private const int SlotSize = 32;

    // ════════════════════════════════════════════════════════════
    //  实例字段
    // ════════════════════════════════════════════════════════════

    private readonly HazardDomain _hp;
    private readonly NativeArena _arena;
    private readonly IntPtr _slotsPtr;
    private readonly int _capacity;
    private readonly int _maxLevel;
    private readonly int _edgeStride;              // maxLevel + 1
    private readonly long[] _edges;                // 边表（托管——永不回收，读写恒内存安全；仅槽位解引用需 hazard）
    private readonly int[] _gen;                   // 槽代数（回收即 +1）
    private readonly T?[] _items;                  // 元素根集（GC 存活可见）
    private readonly AsyncManualResetEvent _signal = new();
    private readonly Action<long> _recycle;        // 缓存 delegate——Retire/级联零分配

    private long _freeHead;                        // (head << 16) | pushTag——ABA 免疫
    private long _sequenceCounter;
    private long _count;
    private long _opCount;                         // DEBUG 校验器节流
    private int _disposed;

#if DEBUG
    private readonly bool[] _inFree;               // DEBUG 双归还探测器（PushFree 断言）
    private readonly byte[] _lastFreeOp;           // DEBUG：上次归还来源（1=FreeSlotNow 2=RecycleSlot-node 3=RecycleSlot-marker）
    private readonly long[] _slotOps;              // DEBUG 租还历史环（op:4|slot:20|gen:16|tid:24）
    private int _slotOpsIdx;
    private void TraceSlot(int op, int idx)
        => _slotOps[Interlocked.Increment(ref _slotOpsIdx) % _slotOps.Length]
            = ((long)op << 60) | ((long)idx << 40) | ((long)(_gen[idx] & 0xFFFF) << 24) | (uint)Environment.CurrentManagedThreadId;
    private string SlotHistory(int idx)
    {
        var sb = new System.Text.StringBuilder();
        var end = Volatile.Read(ref _slotOpsIdx);
        var start = Math.Max(0, end - _slotOps.Length);
        for (var i = start; i < end; i++)
        {
            var e = _slotOps[i % _slotOps.Length];
            if (e != 0 && (int)((e >> 40) & 0xFFFFF) == idx)
            {
                sb.Append("\n  [").Append(i).Append("] op=").Append(e >> 60)
                  .Append(" slot=").Append((e >> 40) & 0xFFFFF).Append(" gen=").Append((e >> 24) & 0xFFFF)
                  .Append(" tid=").Append(e & 0xFFFFFF);
            }
        }
        return sb.ToString();
    }
#endif

    // ════════════════════════════════════════════════════════════
    //  构造
    // ════════════════════════════════════════════════════════════

    /// <summary>创建 V4 实验队列。</summary>
    /// <param name="hazardDomain">共享 HP 域（<b>必需</b>——槽位回收靠扫描确认无读者）。slotsPerThread ≥ 2。</param>
    /// <param name="capacity">槽位容量（node 与 marker 共享；耗尽时让位等回收）。</param>
    /// <param name="maxLevel">跳表最大层数。</param>
    public AsyncPriorityQueueV4(HazardDomain hazardDomain, int capacity = 4096, int maxLevel = 31)
    {
        ArgumentNullException.ThrowIfNull(hazardDomain);
        if (hazardDomain.SlotsPerThread < 2)
            throw new ArgumentOutOfRangeException(nameof(hazardDomain),
                $"slotsPerThread ≥ 2 必需（Find 两槽轮换：curr 与 marker），当前 {hazardDomain.SlotsPerThread}");
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLevel);

        _hp = hazardDomain;
        _capacity = capacity;
        _maxLevel = maxLevel;
        _edgeStride = maxLevel + 1;

        _arena = new NativeArena(capacity * SlotSize);
        _slotsPtr = _arena.Pointer;
        _edges = new long[capacity * _edgeStride];
        _gen = new int[capacity];
        _items = new T?[capacity];
        _recycle = RecycleAndCascade;
#if DEBUG
        _inFree = new bool[capacity];
        _lastFreeOp = new byte[capacity];
        _slotOps = new long[1 << 17];
        for (var i = 1; i < capacity; i++) _inFree[i] = true;
#endif

        // 除 head 外全部入空闲链（单线程构造：NextFree = 前一个索引，1 为链尾）
        for (var i = capacity - 1; i >= 1; i--)
            SlotAtOwned(i).NextFree = i - 1;
        _freeHead = ((long)(capacity - 1) << 16) | 1;

        // head 槽（永久，不标记不回收）：key = long.MinValue
        SlotAtOwned(HeadIndex).Key = long.MinValue;
        SlotAtOwned(HeadIndex).TopLevel = maxLevel;
        SlotAtOwned(HeadIndex).Kind = KindNode;
        SlotAtOwned(HeadIndex).LinkState = LinkDone;
        _gen[HeadIndex] = 1;
    }

    /// <summary>近似元素数（并发下仅诊断用）。</summary>
    public int Count { get { var c = Interlocked.Read(ref _count); return c < 0 ? 0 : (int)c; } }

    // ════════════════════════════════════════════════════════════
    //  槽位与边表原语
    // ════════════════════════════════════════════════════════════

    /// <summary>槽位解引用（R1 强制点）——DEBUG 断言目标 ∈ 当前线程 hazard 集（F1 仪器）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe ref AsyncPriorityQueueV2Slot SlotAt(int idx)
    {
#if DEBUG
        if ((uint)idx >= (uint)_capacity)
            throw new InvalidOperationException(
                $"[V4] SlotAt 越界：idx={idx} capacity={_capacity}（空闲链/边表被破坏）");
        DebugAssertSlotHazarded(idx);
#endif
        return ref Unsafe.AsRef<AsyncPriorityQueueV2Slot>((void*)(_slotsPtr + idx * SlotSize));
    }

    /// <summary>无读者场景的槽位访问（自有未发布槽 / head / 回收路径 / 诊断）——F1 仪器豁免。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe ref AsyncPriorityQueueV2Slot SlotAtOwned(int idx)
    {
#if DEBUG
        if ((uint)idx >= (uint)_capacity)
            throw new InvalidOperationException(
                $"[V4] SlotAtOwned 越界：idx={idx} capacity={_capacity}（空闲链/边表被破坏）");
#endif
        return ref Unsafe.AsRef<AsyncPriorityQueueV2Slot>((void*)(_slotsPtr + idx * SlotSize));
    }

#if DEBUG
    /// <summary>F1 仪器：解引用点校验——目标不在当前 hazard 集 = 漏 Publish（值示波器式覆盖）。</summary>
    private void DebugAssertSlotHazarded(int idx)
    {
        var reg = _hp.Register();
        Span<long> hz = stackalloc long[8];
        _hp.DebugFillHazards(reg, hz);
        foreach (var h in hz)
            if (h != 0 && (int)((ulong)h >> GenShift) == idx) return;
        throw new InvalidOperationException(
            $"[V4 F1] 解引用未保护 slot={idx} gen={_gen[idx]}——当前 hazard 集：[" +
            string.Join(",", hz.ToArray().Where(h => h != 0).Select(h => $"{(int)((ulong)h >> GenShift)}#{h & GenMask}")) +
            $"]\n── slot={idx} 租还历史──{SlotHistory(idx)}");
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref long EdgeRef(int slot, int level) => ref _edges[(slot * _edgeStride) + level];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CasEdge(ref long edge, long expected, long value)
        => Interlocked.CompareExchange(ref edge, value, expected) == expected;

#if DEBUG
    /// <summary>head.edge[0] 专项示波器——复活的写者狩猎（op=8 记入租还环）。</summary>
    private bool CasHeadEdge(long expected, long value)
    {
        var won = Interlocked.CompareExchange(ref _edges[0], value, expected) == expected;
        var i = Interlocked.Increment(ref _slotOpsIdx) - 1;
        _slotOps[i % _slotOps.Length] =
            (8L << 60) | ((long)Interlocked.CompareExchange(ref _dbgHeadEdge, 0, 0) << 24) | (uint)Environment.CurrentManagedThreadId;
        _dbgHeadWrites[_slotOpsIdx % _dbgHeadWrites.Length] =
            (won ? 'W' : 'L', expected, value, Environment.CurrentManagedThreadId);
        return won;
    }
    private readonly (char won, long exp, long val, int tid)[] _dbgHeadWrites = new (char, long, long, int)[256];
    private long _dbgHeadEdge;
    private string HeadWritesDump()
    {
        var sb = new System.Text.StringBuilder();
        var end = Volatile.Read(ref _slotOpsIdx);
        for (var k = 0; k < _dbgHeadWrites.Length; k++)
        {
            var e = _dbgHeadWrites[(end + k) % _dbgHeadWrites.Length];
            if (e == default) continue;
            sb.Append($" [{e.won} exp={Decode(e.exp).Index}#{Decode(e.exp).Gen} val={Decode(e.val).Index}#{Decode(e.val).Gen} tid={e.tid}]");
        }
        return sb.ToString();
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Marked(long slotRef) => slotRef | MarkBit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMarkedRef(long raw) => (raw & MarkBit) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Plain(long raw) => raw & ~MarkBit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Encode(int idx) => ((long)idx << GenShift) | (uint)(_gen[idx] & GenMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int Index, int Gen) Decode(long slotRef) => ((int)(slotRef >> GenShift), (int)(slotRef & GenMask));

    /// <summary>fail-visible 绊线：代数不匹配 = 陈旧引用（协议 bug——验证通过的解引用不应撞见）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckGen(int idx, int gen, int level = -1)
    {
        if (Volatile.Read(ref _gen[idx]) != gen)
            ThrowStaleGen(idx, gen, level);
    }

    [DoesNotReturn]
    private void ThrowStaleGen(int idx, int gen, int level)
        => throw new InvalidOperationException(
            $"[V4] fail-visible：陈旧槽位引用 slot={idx} gen={gen} 当前gen={Volatile.Read(ref _gen[idx])} " +
            $"level={level}——level-0 链快照：{DumpChain0()}"
#if DEBUG
            + $" headWrites:{HeadWritesDump()}"
            + $"\n── slot={idx} 租还历史──{SlotHistory(idx)}"
#endif
        );

    /// <summary>写链校验：写进活链的 bypass 值必须代数有效（位置性 walk 论证下不可达已回收目标——
    /// 命中即协议回归，fail-visible）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long CheckLive(long slotRef)
    {
        if (slotRef != 0)
        {
            var (idx, gen) = Decode(slotRef);
            CheckGen(idx, gen);
        }
        return slotRef;
    }

    /// <summary>边获取（发布+验证，hazard 值规范化为 plain——scan 匹配退休记录用 plain 引用）。
    /// 语义同 <see cref="HazardDomain.TryProtect"/>；域内规范：边值可能带 mark 位，hazard 只存 plain。
    /// 返回 raw（含 mark 位）供调用方判断 marker；来源已变以新值重试。</summary>
    private long TryProtectEdge(HazardRegistration reg, int hazardSlot, int slotIdx, int level)
    {
        while (true)
        {
            var raw = Volatile.Read(ref EdgeRef(slotIdx, level));
            if (raw == 0)
            {
                _hp.Publish(reg, hazardSlot, 0);
                return 0;
            }
            _hp.Publish(reg, hazardSlot, Plain(raw));
            if (Volatile.Read(ref EdgeRef(slotIdx, level)) == raw)
                return raw;
        }
    }

    /// <summary>读 marker 的 NextSlot（解引用 marker 槽——冻结字段）。
    /// ★ 新鲜性论证（裸 Publish 的结构性证明场景，设计 §3.1）：调用方刚经验证的边仍指向此 marker
    /// ⟹ 其 victim 仍挂链（未冻结摘除）⟹ victim 未回收 ⟹ 级联退休未触发 ⟹ marker 槽未复用。</summary>
    private long ReadMarkerNext(HazardRegistration reg, int hazardSlot, long mRef)
    {
        var (mIdx, mGen) = Decode(mRef);
        CheckGen(mIdx, mGen);
        _hp.Publish(reg, hazardSlot, mRef);
        return Volatile.Read(ref SlotAt(mIdx).Key);        // NextSlot（Key 字段复用，冻结）
    }

    /// <summary>操作收尾清理：Find/遍历出口可能在 hz0 留陈旧值——净占用只是容量损失，但会推迟
    /// 单个退休项的回收并触发域 Dispose 绊线。两 volatile 写，收尾一律执行。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearHazards(HazardRegistration reg)
    {
        _hp.Publish(reg, 0, 0);
        _hp.Publish(reg, 1, 0);
    }

    // ════════════════════════════════════════════════════════════
    //  槽位池（侵入式空闲链 + tag CAS 头——零分配、ABA 免疫；marker 共享此池）
    // ════════════════════════════════════════════════════════════

    /// <summary>非阻塞租槽（-1 = 池空，让位上层）。池空由 <see cref="RentSlotOrScan"/> 兜底。</summary>
    private int TryRentSlot()
    {
        var spin = new SpinWait();
        var tries = 0;
        while (true)
        {
            var h = Volatile.Read(ref _freeHead);
            var head = (int)(h >> 16);
            if (head == 0) return -1;
            var next = SlotAtOwned(head).NextFree;
            if (Interlocked.CompareExchange(ref _freeHead, ((long)next << 16) | ((h + 1) & 0xFFFF), h) == h)
            {
#if DEBUG
                Volatile.Write(ref _inFree[head], false);
                TraceSlot(1, head);
#endif
                return head;
            }
            if (++tries > 64) return -1;
            spin.SpinOnce();
        }
    }

    /// <summary>租槽 + 活性契约兜底：池空 → 强制 Scan（HP 下回收是空闲槽唯一来源）→ 让位重试。</summary>
    private int RentSlotOrScan()
    {
        var spin = new SpinWait();
        while (true)
        {
            var idx = TryRentSlot();
            if (idx >= 0) return idx;
            _hp.Scan();
            spin.SpinOnce();
        }
    }

    private void PushFree(int idx)
    {
        var spin = new SpinWait();
        while (true)
        {
#if DEBUG
            if (Volatile.Read(ref _inFree[idx]))
                throw new InvalidOperationException(
                    $"[V4] 双归还！slot={idx} gen={_gen[idx]} 上次来源={_lastFreeOp[idx]}" +
                    $"（1=FreeSlotNow 2=RecycleSlot-node 3=RecycleSlot-marker）" +
                    $"\n── slot={idx} 租还历史──{SlotHistory(idx)}");
#endif
            var h = Volatile.Read(ref _freeHead);
            SlotAtOwned(idx).NextFree = (int)(h >> 16);
            if (Interlocked.CompareExchange(ref _freeHead, ((long)idx << 16) | ((h + 1) & 0xFFFF), h) == h)
            {
#if DEBUG
                Volatile.Write(ref _inFree[idx], true);
#endif
                return;
            }
            spin.SpinOnce();
        }
    }

    /// <summary>立即归还（仅限从未发布的槽——无任何边指向，无需退休）。</summary>
    private void FreeSlotNow(int idx)
    {
        _items[idx] = default;
        SlotAtOwned(idx).Kind = KindNode;
        SlotAtOwned(idx).LinkState = LinkDone;
        Volatile.Write(ref _gen[idx], (_gen[idx] + 1) & GenMask);
#if DEBUG
        TraceSlot(4, idx);
        _lastFreeOp[idx] = 1;
#endif
        PushFree(idx);
    }

    /// <summary>reclaim action：node 槽级联退休其 markers 后归还；marker 槽直接归还。
    /// ★ 级联（marker 的唯一引用源是 victim 的冻结边）：victim 无 hazard（扫描已确认）⟹ 无人能再
    /// 经其冻结边解引用 marker（读者必先 hazard victim）⟹ 一并退休安全。级联 Retire 在 reclaim 内
    /// 执行——原语的重入扫描（Monitor 可重入 + 分层快照缓冲）为此设计。</summary>
    private void RecycleAndCascade(long slotRef)
    {
        var (idx, gen) = Decode(slotRef);
#if DEBUG
        if (Volatile.Read(ref _gen[idx]) != gen)
            throw new InvalidOperationException(
                $"[V4 陈旧退休记录] reclaim slot={idx} 记录gen={gen} 当前gen={Volatile.Read(ref _gen[idx])}" +
                $"——该记录跨代存活（双重退休，或退休时槽已回到池中）chain={DumpChain0()} 历史={SlotHistory(idx)}");
#endif
        if (SlotAtOwned(idx).Kind == KindMarker)
        {
            RecycleSlot(idx, 3);                           // marker：其边是前租户垃圾——无级联
            return;
        }
        var top = SlotAtOwned(idx).TopLevel;
        for (var i = 0; i <= top; i++)
        {
            var e = Volatile.Read(ref EdgeRef(idx, i));
            if (IsMarkedRef(e))
            {
#if DEBUG
                TraceSlot(6, Decode(Plain(e)).Index);      // 级联退休 victim 的 marker
#endif
                _hp.Retire(Plain(e), _recycle);
            }
        }
        RecycleSlot(idx, 2);
    }

    /// <summary>回收归还（扫描确认无 hazard 后执行）。来源：<paramref name="src"/> 供双归还探测器指认路径。</summary>
    private void RecycleSlot(int idx, byte src)
    {
        _items[idx] = default;
        SlotAtOwned(idx).Kind = KindNode;
        SlotAtOwned(idx).LinkState = LinkDone;
        Volatile.Write(ref _gen[idx], (_gen[idx] + 1) & GenMask);
#if DEBUG
        TraceSlot(2, idx);
        _lastFreeOp[idx] = src;
#endif
        PushFree(idx);
    }

    // ════════════════════════════════════════════════════════════
    //  FIND——marker 感知遍历 + 顺带物理摘除（helping）+ 位置性 walk（正确性承重）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 查找 key 的各层前驱/后继。★ 同時是胜者出队后的位置性摘除 walk（可达入边全清——Retire 前置）。
    /// <para>hazard 纪律：每跳经 <see cref="TryProtectEdge"/> 发布+验证（来源=上一跳的边——未冻结：
    /// 链接/splice 都改写它）；遇已删 pred（其边冻结持 marker）<b>不跟随</b>——从 head 重走本层
    /// （Route A 靠 GC 跟随冻结边；HP 下冻结路径末端无法经未冻结来源验证活性）。</para>
    /// <para>helping：curr 已删（其本层边带 mark 位）→ CAS pred 边绕过 curr 直连 marker.Next——
    /// bypass 仅作<b>值</b>写入（R2），其活性由"验证通过的 pred 边 + 位置性 walk 前置"保证。</para>
    /// </summary>
    private void Find(long key, Span<long> preds, Span<long> succs, HazardRegistration reg)
    {
        for (var level = _maxLevel; level >= 0; level--)
        {
            var settled = false;
            while (!settled)
            {
                var pred = HeadIndex;
                var currRaw = TryProtectEdge(reg, 0, pred, level);
                while (true)
                {
                    if (currRaw == 0)
                    {
                        // 层尾：currRaw 一路经未冻结来源验证推进（Route A 的 curr==null 出口）
                        preds[level] = Encode(pred);
                        succs[level] = 0;
                        settled = true;
                        break;
                    }
                    if (IsMarkedRef(currRaw))
                    {
                        // pred 已删（本层）——其边冻结持 marker。不跟随（Route A 靠 GC 跟随冻结边；
                        // HP 下冻结路径的末端节点无法经未冻结来源验证活性）→ 本层从 head 重走。
                        // 活性：每次重走，已删段只会被本线程/他方的 helping splice 推进——有限重走。
                        break;
                    }
                    var (cIdx, cGen) = Decode(currRaw);
                    CheckGen(cIdx, cGen, level);
                    var cKey = SlotAt(cIdx).Key;                    // 解引用 ✓ hz0

                    // curr 已删？（其本层边带 mark 位——纯值判断，对应 Route A 的 next is Marker）
                    var nextRaw = Volatile.Read(ref EdgeRef(cIdx, level));
                    if (IsMarkedRef(nextRaw))
                    {
                        // helping splice：pred 边绕过 curr 直连冻结后继（bypass 仅作值写入——R2；
                        // 其活性由"验证通过的 pred 边 + 位置性 walk 前置"保证，CheckLive 为绊线）
                        var bypass = ReadMarkerNext(reg, 1, Plain(nextRaw));
#if DEBUG
                        if (pred == HeadIndex && level == 0) CasHeadEdge(currRaw, CheckLive(bypass));
                        else
#endif
                        CasEdge(ref EdgeRef(pred, level), currRaw, CheckLive(bypass));
                        _hp.Unprotect(reg, 1);
                        currRaw = TryProtectEdge(reg, 0, pred, level);   // 同一 pred 重新验证
                        continue;
                    }
                    if (cKey < key)
                    {
                        pred = cIdx;
                        currRaw = TryProtectEdge(reg, 0, pred, level);   // 下一跳来源 = 新 pred 的边
                        continue;
                    }
                    // 定位：curr 未删且 Key ≥ key（succs 绝不返回 marker——Route A 同款契约）
                    preds[level] = Encode(pred);
                    succs[level] = currRaw;
                    settled = true;
                    break;
                }
            }
        }
        // ★ Find 出口即清（不等操作尾）：hz0 的 settled-curr 残留会让退休记录长期成为幸存者
        //   （延迟回收配置下死锁租用路径；常规配置下推迟回收并放大交错窗口）
        _hp.Publish(reg, 0, 0);
        _hp.Publish(reg, 1, 0);
    }

    // ════════════════════════════════════════════════════════════
    //  随机层级（thread-local xorshift，无锁无分配）
    // ════════════════════════════════════════════════════════════

    [ThreadStatic] private static uint _tRandState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RandomLevel()
    {
        var s = _tRandState;
        if (s == 0) s = (uint)Environment.CurrentManagedThreadId * 2654435761u | 1;
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        _tRandState = s;

        var level = 0;
        while ((s & 1) == 0 && level < _maxLevel)
        {
            level++;
            s >>= 1;
        }
        return level;
    }

    // ════════════════════════════════════════════════════════════
    //  ENQUEUE——level-0 严格发布（唯一关键层），高层 best-effort 加速
    // ════════════════════════════════════════════════════════════

    /// <summary>入队元素。</summary>
    /// <exception cref="ObjectDisposedException">队列已释放。</exception>
    public void Enqueue(T item, int priority)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var seq = Interlocked.Increment(ref _sequenceCounter);
        var key = ((long)priority << 48) | (seq & 0xFFFFFFFFFFFF);
        var topLevel = RandomLevel();
        var reg = _hp.Register();

        var nodeIdx = RentSlotOrScan();                          // 池空 → 强制扫描（活性契约）
        // 填充（未发布——无读者可达，F1 仪器豁免）
        SlotAtOwned(nodeIdx).Key = key;
        SlotAtOwned(nodeIdx).Sequence = seq;
        SlotAtOwned(nodeIdx).Priority = priority;
        SlotAtOwned(nodeIdx).TopLevel = topLevel;
        SlotAtOwned(nodeIdx).Kind = KindNode;
        SlotAtOwned(nodeIdx).LinkState = LinkDone;               // Route A 形态无需 Linking 门
        _items[nodeIdx] = item;

        Span<long> preds = stackalloc long[_maxLevel + 1];
        Span<long> succs = stackalloc long[_maxLevel + 1];
        var spin = new SpinWait();
        while (true)
        {
            Find(key, preds, succs, reg);
            // ★ 发布纪律（Route A 同款）：全部自身边在 level-0 发布 CAS 之前写完——CAS 是唯一使
            //   节点可达的动作；发布后自身字段只有两类写者：删除者的 marker CAS 与 helping splice。
            for (var i = 0; i <= topLevel; i++)
                Volatile.Write(ref EdgeRef(nodeIdx, i), succs[i]);
            var (p0, _) = Decode(preds[0]);
#if DEBUG
            if (p0 == HeadIndex ? CasHeadEdge(succs[0], Encode(nodeIdx)) : CasEdge(ref EdgeRef(p0, 0), succs[0], Encode(nodeIdx)))
#else
            if (CasEdge(ref EdgeRef(p0, 0), succs[0], Encode(nodeIdx)))
#endif
                break;
            spin.SpinOnce();
        }

        // 高层 best-effort（含尾插必 CAS——尾插退化修复：跳过会让尾插节点永不进该层，持续尾插负载
        // 下索引退化 → Find 线性扫描）。迟到链接的 CAS 基（succs[L]）已被删除 walk 先行失效——
        // 成功 ⟹ walk 必在其后经过并经本节点继续清理（无残余可达入边）。
        for (var i = 1; i <= topLevel; i++)
        {
            var (pi, _) = Decode(preds[i]);
            CasEdge(ref EdgeRef(pi, i), succs[i], Encode(nodeIdx));
        }

        Interlocked.Increment(ref _count);
        _signal.Set();
        ClearHazards(reg);
        DebugValidateThrottled();
    }

    // ════════════════════════════════════════════════════════════
    //  DEQUEUE——mark level-0 决胜 → 高层标记到落地 → 位置性 walk → Retire
    // ════════════════════════════════════════════════════════════

    /// <summary>尝试出队最小元素。</summary>
    public bool TryDequeue(out T item)
    {
        item = default!;
        if (_disposed != 0) return false;
        var reg = _hp.Register();

        Span<long> preds = stackalloc long[_maxLevel + 1];
        Span<long> succs = stackalloc long[_maxLevel + 1];
        var spin = new SpinWait();

        while (true)
        {
            // level-0 首节点（head 永久——唯一的永真 splice 基座）
            var victimRaw = TryProtectEdge(reg, 0, HeadIndex, 0);
            if (victimRaw == 0) return false;
            var (vIdx, vGen) = Decode(victimRaw);
            CheckGen(vIdx, vGen);

            // victim 已被并发删除？（其 level-0 边带 mark 位——值判断）
            var next0Raw = Volatile.Read(ref EdgeRef(vIdx, 0));
            if (IsMarkedRef(next0Raw))
            {
                // head 边绕过已删 victim（head 永不标记——splice 恒安全）
                var bypass = ReadMarkerNext(reg, 1, Plain(next0Raw));
#if DEBUG
                CasHeadEdge(victimRaw, CheckLive(bypass));
#else
                CasEdge(ref _edges[0], victimRaw, CheckLive(bypass));
#endif
                _hp.Unprotect(reg, 1);
                spin.SpinOnce();
                continue;
            }

            // ★ mark level-0 决胜（线性化点）：租 marker → 填充 → CAS victim 边。
            //   失败 = 决胜败北或被 helping splice 竞争改写（Route A 同型）→ 重试下一个 victim。
            var m0 = RentSlotOrScan();
#if DEBUG
            TraceSlot(7, m0);
#endif
            FillMarker(m0, next0Raw);
            if (!CasEdge(ref EdgeRef(vIdx, 0), next0Raw, Marked(Encode(m0))))
            {
                FreeSlotNow(m0);                                 // 从未发布——立即归还
                spin.SpinOnce();
                continue;
            }

            // 胜者。发布纪律绊线：可达 ⟹ level-0 已发布 ⟹ 自身边全部写完（Route A 形态）——
            // LinkState 恒 Done；非 Done = 发布纪律被破坏（fail-visible）。
            var waits = 0;
            while (SlotAt(vIdx).LinkState != LinkDone)
                if (++waits > 1 << 24)
                    throw new InvalidOperationException(
                        $"[V4] 等待入队链接完成超时——victim={vIdx}#{vGen}（发布纪律被破坏，fail-visible）");

            var vKey = SlotAt(vIdx).Key;                         // 解引用 ✓ hz0
            var vTop = SlotAt(vIdx).TopLevel;

            // 高层标记到落地（与 helping splice 竞争同字段——被打败留下"已删未标"僵尸 → 活性死锁，
            //   Route A 教训：重试直到落地）。marker.Next = 该层旧后继（冻结）。
            for (var i = 1; i <= vTop; i++)
            {
                while (true)
                {
                    var ni = Volatile.Read(ref EdgeRef(vIdx, i));
                    if (IsMarkedRef(ni)) break;                  // 已标（防御——单写者正常不可达）
                    var mi = RentSlotOrScan();
#if DEBUG
                    TraceSlot(7, mi);
#endif
                    FillMarker(mi, Plain(ni));
                    if (CasEdge(ref EdgeRef(vIdx, i), ni, Marked(Encode(mi)))) break;
                    FreeSlotNow(mi);
                }
            }

            item = _items[vIdx]!;                                // 托管数组——无需 hazard

            // ★ sequencing（设计 §4）：先释放 victim hazard 再走摘除 walk——walk 中 victim 仅值
            //   比较（R2），本线程 hazard 峰值 ≤ 2（Find 内部两槽轮换）。
            _hp.Unprotect(reg, 0);

            // ★ 位置性 walk（正确性承重）：摘除 victim 的一切可达入边——Retire 的前置条件。
            Find(vKey, preds, succs, reg);
#if DEBUG
            if (Volatile.Read(ref EdgeRef(HeadIndex, 0)) == victimRaw)
                throw new InvalidOperationException(
                    $"[V4 walk 失职] Retire 前 head.edge 仍指向 victim={vIdx}#{vGen} vKey={vKey}——" +
                    $"walk 未清可达入边（正确性承重违反）chain={DumpChain0()}");
#endif

            // walk 完成 → Retire（R3：全层已标 + 可达入边已清）。markers 由 reclaim 级联退休。
#if DEBUG
            TraceSlot(5, vIdx);
#endif
            _hp.Retire(victimRaw, _recycle);

            Interlocked.Decrement(ref _count);
            ClearHazards(reg);
            DebugValidateThrottled();
            return true;
        }
    }

    /// <summary>填充 marker 槽（NextSlot 存 Key 字段——冻结；发布点 = 入边的 CAS）。</summary>
    private void FillMarker(int mIdx, long nextSlot)
    {
        SlotAtOwned(mIdx).Kind = KindMarker;
        SlotAtOwned(mIdx).LinkState = LinkDone;
        Volatile.Write(ref SlotAtOwned(mIdx).Key, nextSlot);
    }

    // ════════════════════════════════════════════════════════════
    //  PEEK
    // ════════════════════════════════════════════════════════════

    /// <summary>查看队首元素而不移除。</summary>
    public bool TryPeek(out T item)
    {
        item = default!;
        if (_disposed != 0) return false;
        var reg = _hp.Register();
        var spin = new SpinWait();

        while (true)
        {
            var currRaw = TryProtectEdge(reg, 0, HeadIndex, 0);
            if (currRaw == 0) return false;
            var (idx, gen) = Decode(currRaw);
            CheckGen(idx, gen);

            var nextRaw = Volatile.Read(ref EdgeRef(idx, 0));
            if (IsMarkedRef(nextRaw))
            {
                // 已删 → 帮助摘除（head 永不标记）→ 重扫
                var bypass = ReadMarkerNext(reg, 1, Plain(nextRaw));
                CasEdge(ref EdgeRef(HeadIndex, 0), currRaw, CheckLive(bypass));
                _hp.Unprotect(reg, 1);
                spin.SpinOnce();
                continue;
            }
            item = _items[idx]!;                                 // 托管数组——无需 hazard
            _hp.Unprotect(reg, 0);
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  异步出队 / 释放
    // ════════════════════════════════════════════════════════════

    /// <summary>异步出队；队列为空则等待入队或取消。</summary>
    public ValueTask<T> DequeueAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return TryDequeue(out var item) ? new ValueTask<T>(item) : new ValueTask<T>(DequeueSlowAsync(ct));
    }

    private async Task<T> DequeueSlowAsync(CancellationToken ct)
    {
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            _signal.Reset();
            if (TryDequeue(out var item)) return item;
            await _signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _signal.Set();
        // ★ 排空退休链必须先于 arena 释放——reclaim action（含 marker 级联）要解引用槽位内存，
        //   顺序颠倒 = use-after-free-on-free。静默契约（无并发操作 ⟹ 无 hazard）下必可收敛；
        //   收敛不了 = 悬挂 hazard 泄漏——fail-visible。
        for (var guard = 0; guard < 1_000_000 && _hp.RetiredCount > 0; guard++) _hp.Scan();
        if (_hp.RetiredCount > 0)
            throw new InvalidOperationException(
                $"[V4] Dispose 时退休链无法排空（悬挂 hazard？）——retire={_hp.RetiredCount}（fail-visible）");
        _arena.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  DEBUG 仪器：链校验器 / 链快照 / 节流巡检
    // ════════════════════════════════════════════════════════════

    /// <summary>每 64 次操作自动巡检一次（DEBUG 有效，Release 无开销）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DebugValidateThrottled()
    {
        if ((Interlocked.Increment(ref _opCount) & 63) == 0)
            ValidateInvariants();
    }

    /// <summary>走 level-0 主链校验：key 严格递增（防后向边/自环）、marker 链长 ≤1、
    /// 步数护栏（防成环）。★ 巡检在线形态（Route A 同款）：已删节点<b>跳过</b>（跟随冻结
    /// marker.Next）——单调性对陈旧读也成立（链接只前进）；代数漂移/瞬态以重走吸收，
    /// 8 连瞬态不报（热负载下尽力而为）。<b>key 非单调/成环不可能瞬态——必报</b>。
    /// （V2 靠 epoch 静默获得一致快照；HP 无全局静默——巡检语义相应调整。）</summary>
    internal void ValidateInvariants()
    {
#if DEBUG
        var reg = _hp.Register();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            long last = long.MinValue;
            var steps = 0;
            var dirty = false;
            var raw = TryProtectEdge(reg, 0, HeadIndex, 0);
            while (raw != 0)
            {
                if ((uint)Decode(Plain(raw)).Index >= (uint)_capacity) { dirty = true; break; }   // 垃圾值防御（诊断路径容忍）
                if (IsMarkedRef(raw))
                {
                    var (mIdx, mGen) = Decode(Plain(raw));                    // 已删节点：跳过
                    if (Volatile.Read(ref _gen[mIdx]) != mGen) { dirty = true; break; }
                    raw = Volatile.Read(ref SlotAtOwned(mIdx).Key);           // 冻结后继（plain）
                    _hp.Publish(reg, 0, Plain(raw));                          // 发布跟随节点（F1 纪律；gen 双检兜底）
                    continue;
                }
                if (++steps > 1 << 24)
                {
                    Debug.Fail("[V4] level-0 链步数超护栏（疑似成环）");
                    return;
                }
                var (idx, gen) = Decode(raw);
                if (Volatile.Read(ref _gen[idx]) != gen) { dirty = true; break; }   // 读期间被回收——重走
                var key = SlotAt(idx).Key;
                if (Volatile.Read(ref _gen[idx]) != gen) { dirty = true; break; }
                if (key <= last)
                {
                    Debug.Fail($"[V4] level-0 key 非单调：{last} → {key}（结构破坏——链接只前进，不可能瞬态）\n{DumpChain0()}");
                    return;
                }
                last = key;
                raw = TryProtectEdge(reg, 0, idx, 0);
            }
            if (!dirty) return;                                               // 干净快照——通过
        }
#endif
    }

    /// <summary>DEBUG 取证：未掩码的 _count 原始值（负值 = 过量出队——phantom 双消费的直接证据）。</summary>
    internal long DebugRawCount() => Interlocked.Read(ref _count);

    /// <summary>DEBUG 取证：沿 NextFree 走空闲链的真实长度（与容量守恒核对——检测槽位走失）。</summary>
    internal int DebugFreeListLength()
    {
        var steps = 0;
        var idx = (int)(Volatile.Read(ref _freeHead) >> 16);
        while (idx != 0 && steps++ < _capacity * 2)
            idx = (int)SlotAtOwned(idx).NextFree;
        return steps;
    }

    /// <summary>DEBUG 取证：队列状态快照（计数/head 边/容忍链走/退休水位）——楔死取证用。</summary>
    internal string DebugState()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"count={Count} retired={_hp.RetiredCount} freeHead={_freeHead:X} " +
                  $"headEdge0={Volatile.Read(ref EdgeRef(HeadIndex, 0)):X} chain0={DumpChain0()}");
        return sb.ToString();
    }

    /// <summary>DEBUG 取证：dump level-0 主链（容忍瞬态——诊断路径不复用 F1 断言）。</summary>
    private string DumpChain0()
    {
        var sb = new System.Text.StringBuilder();
        var steps = 0;
        var raw = Volatile.Read(ref EdgeRef(HeadIndex, 0));
        while (raw != 0 && steps++ < 64)
        {
            var (idx, gen) = Decode(Plain(raw));
            if (IsMarkedRef(raw))
            {
                sb.Append($" -> [M{idx}#{gen}→{(int)(SlotAtOwned(idx).Key >> GenShift)}]");
                raw = SlotAtOwned(idx).Key;
                continue;
            }
            if (Volatile.Read(ref _gen[idx]) != gen)
            {
                sb.Append($" -> [STALE {idx}#{gen} cur={Volatile.Read(ref _gen[idx])}]");
                break;
            }
            sb.Append($" -> [{SlotAtOwned(idx).Key}|{idx}#{gen}]");
            raw = Volatile.Read(ref EdgeRef(idx, 0));
        }
        return sb.ToString();
    }
}
