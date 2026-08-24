using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.NativeInterop;
using TC.Tier.Core.Primitives;
using Int128 = TC.Tier.Core.NativeInterop.Int128;

namespace TC.Tier.Core.Collections;

/// <summary>AsyncPriorityQueueV2 原生槽位（32B）。Kind=Node 时 Key/Sequence/Priority/TopLevel 有效；
/// LinkState：1=入队侧正在链接高层（出队摘除前必须等到 0）；空闲态 NextFree 复用 Key 存链指针。
/// <para>★ 独立顶层类型：泛型类内的嵌套 struct 不允许显式布局（CLR 限制）。</para></summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct AsyncPriorityQueueV2Slot
{
    [FieldOffset(0)] public long Key;
    [FieldOffset(8)] public long Sequence;
    [FieldOffset(16)] public int Priority;
    [FieldOffset(20)] public int TopLevel;
    [FieldOffset(24)] public byte Kind;
    [FieldOffset(25)] public byte LinkState;
    [FieldOffset(0)] public long NextFree;
}

/// <summary>
/// ★ 实验版本（Route B' 方案 A 验证版）——**不可用于生产**：仅保留作设计档案与对照实验；
///   生产一律用 <see cref="AsyncPriorityQueue{T}"/>（Route A 基线）。测试默认 Skip，不入基准。
/// AsyncPriorityQueue <b>Route B' 方案 A 验证版——非移动内存 + 槽位寻址 + generation +
/// <b>128 位边对（16B = slotRef + mark 位）</b> + epoch 回收 + 零分配。
/// <para>★ 与 <see cref="AsyncPriorityQueue{T}"/>（Route A，生产基线）同族；本版把删除协议升级为
///   <b>Harris 原版</b>：mark 在 <b>victim 自身边</b>（非 marker 槽），物理摘除时 mark 被 splice 消耗。</para>
/// <para><b>边对设计（16B，NativeAtomic128 CAS + 64B 对齐背板）</b>：</para>
/// <list type="bullet">
/// <item>Lo = <c>(slotIndex &lt;&lt; 16 | generation)</c>，Hi = mark 位（victim 自身边被 CAS 置 1 即逻辑删除）</item>
/// <item>mark 与 splice 冲突同域：pred 已删 ⟹ pred 自身边已 mark ⟹ 对其 splice 的 CAS 必失败 → 自同步收敛</item>
/// <item>victim 自身边发布后永不改写（I1 完全恢复）——splice 永远能读到死节点的 next（I3 由构造消除）</item>
/// </list>
/// <para><b>link-after-splice 竞态（首轮根因）关闭</b>：入队侧 level-0 发布前置 <c>LinkState=Linking</c>、
/// 全部高层链接 CAS 落地后置 <c>Done</c>；出队胜者（level-0 mark 决胜）等 Done 后<b>全层 mark</b>、
/// 再逐层摘除 walk——全部链接先于 mark/walk 落地，walk 必见全层链接并摘除。</para>
/// <para><b>回收前终检（drain-time verification）</b>：静默期保证 ≤ 标签 epoch 段的迟到 splice 全部落地；
/// drain 逐层重走摘除 victim 残余入边 + 帮摘路径全部已删节点——迟到 splice 的 CAS 基先行失效，
/// 陈旧值无法入活链。无 marker 槽——pending 环退化为单 victim + epoch 标签。</para>
/// <para>★ 零分配热路径：侵入式空闲链、stackalloc preds/succs、预分配 pending 环、缓存 drain delegate。</para>
/// <para>★ 约束（原型）：固定容量；generation 16 位（回绕窗口由 fail-visible 兜底）；epoch 必须注入。</para>
/// </summary>
/// <typeparam name="T">队列中存储的元素类型。</typeparam>
[SuppressMessage("Naming", "CA1711:标识符应采用正确的后缀")]
[Experimental("TCTier001")]
internal sealed class AsyncPriorityQueueV2<T> : IDisposable
{
    // ════════════════════════════════════════════════════════════
    //  槽位与常量
    // ════════════════════════════════════════════════════════════

    private const byte KindNode = 0;
    private const byte LinkDone = 0;
    private const byte LinkLinking = 1;
    private const ulong Marked = 1UL;
    private const ulong Unmarked = 0UL;
    private const int GenShift = 16;
    private const int GenMask = 0xFFFF;
    private const int FlushThreshold = 64;
    private const int EdgeSize = 16;

    private const int HeadIndex = 0;
    private const int SlotSize = 32;

    // ════════════════════════════════════════════════════════════
    //  实例字段
    // ════════════════════════════════════════════════════════════

    private readonly LightEpoch _epoch;
    private readonly NativeArena _arena;
    private readonly IntPtr _slotsPtr;
    private readonly int _capacity;
    private readonly int _maxLevel;
    private readonly int _edgeStride;              // maxLevel + 1
    private readonly AlignedMemoryManager _edgeMem; // 边表：16B/边，(slot * stride + level) * 16 字节偏移
    private readonly int[] _gen;                   // 槽代数（回收即 +1）
    private readonly T?[] _items;                  // 元素根集（GC 存活可见）
    private readonly AsyncManualResetEvent _signal = new();

    private long _freeHead;                        // ★ 带 tag 的空闲链头：(head << 16) | pushTag——ABA 免疫
    private long _sequenceCounter;
    private long _count;
    private int _disposed;

    // pending 回收环形缓冲（块 = [victim]；_pendingTags 存块入环 epoch 标签，_ringFilled 存块填充完成标志）
    // ★ 环容量绑定槽容量（capacity + 256）——每个槽一次出队最多占一个块，环满在契约内不可能；
    //   CAS 领取 + 空间预检：满则自旋让位（绝不消耗 claim 留下洞——旧 relay 协议下洞会永久卡死后续发布）。
    private readonly int[] _pending;
    private readonly long[] _pendingTags;          // 块入环 epoch 标签（claim % _ringBlocks 索引）
    private readonly int[] _ringFilled;            // 块填充完成标志（release 写 / acquire 读）
    private readonly int _ringBlocks;
    private int _ringClaim;                        // CAS 领取块号
    private int _ringDrained;                      // 已回收水位
    private long _pendingCount;
    private readonly object _recycleLock = new();
    private readonly Action _drainAction;          // 缓存 delegate——bump 路径零分配
#if DEBUG
    private readonly bool[] _inFree;               // DEBUG 双归还探测器（PushFree 断言）
    private readonly byte[] _lastFreeSrc;          // DEBUG：上次归还来源（1=FreeSlotNow 2=RecycleSlot）
    private readonly long[] _slotOps;              // DEBUG：租还历史环——单 long 原子条目（op:4|slot:20|gen:16|tid:24），失败时才格式化
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

    /// <summary>创建 B' 方案 A 验证队列。</summary>
    /// <param name="epoch">共享 epoch 实例（<b>必需</b>——槽位回收靠 epoch 静默期，调用方持有）。</param>
    /// <param name="capacity">槽位容量（固定；耗尽抛 <see cref="InvalidOperationException"/>）。</param>
    /// <param name="maxLevel">跳表最大层数。</param>
    public AsyncPriorityQueueV2(LightEpoch epoch, int capacity = 4096, int maxLevel = 31)
    {
        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLevel);

        _capacity = capacity;
        _maxLevel = maxLevel;
        _edgeStride = maxLevel + 1;

        _arena = new NativeArena(capacity * SlotSize);
        _slotsPtr = _arena.Pointer;
        _edgeMem = new AlignedMemoryManager(checked(capacity * _edgeStride * EdgeSize), AlignmentConst.Alignment64B, zeroed: true);
        _gen = new int[capacity];
        _items = new T?[capacity];
        _ringBlocks = capacity + 256;
        _pending = new int[_ringBlocks];
        _pendingTags = new long[_ringBlocks];
        _ringFilled = new int[_ringBlocks];
        _drainAction = DrainReclaims;
#if DEBUG
        _inFree = new bool[capacity];
        _lastFreeSrc = new byte[capacity];
        _slotOps = new long[1 << 16];
        for (var i = 1; i < capacity; i++) _inFree[i] = true;
#endif

        // 除 head 外全部入空闲链（单线程构造：NextFree = 前一个索引，1 为链尾）
        for (var i = capacity - 1; i >= 1; i--)
            SlotAt(i).NextFree = i - 1;
        _freeHead = ((long)(capacity - 1) << 16) | 1;

        // head 槽（永久，不回收）：key = long.MinValue
        SlotAt(HeadIndex).Key = long.MinValue;
        SlotAt(HeadIndex).TopLevel = maxLevel;
        SlotAt(HeadIndex).Kind = KindNode;
        SlotAt(HeadIndex).LinkState = LinkDone;
        _gen[HeadIndex] = 1;
    }

    /// <summary>槽位原生内存访问（类保持非 unsafe——async 方法需要安全上下文）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe ref AsyncPriorityQueueV2Slot SlotAt(int idx)
    {
#if DEBUG
        if ((uint)idx >= (uint)_capacity)
            throw new InvalidOperationException(
                $"[AsyncPriorityQueueV2] SlotAt 越界：idx={idx} capacity={_capacity}（空闲链/边表被破坏）");
#endif
        return ref Unsafe.AsRef<AsyncPriorityQueueV2Slot>((void*)(_slotsPtr + idx * SlotSize));
    }

    // ════════════════════════════════════════════════════════════
    //  边表原语（16B 边对：Lo=slotRef，Hi=mark 位）
    // ════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe ref Int128 EdgeAt(int slot, int level) =>
        ref _edgeMem.GetRefUnsafe<Int128>((slot * _edgeStride + level) * EdgeSize);

    /// <summary>读 16B 边。★ 分两次 Volatile.Read 逐 8B——mark 仅改 flags（ref 不变）、
    /// link/splice 仅改 ref（flags 恒 0），撕裂组合必然仍为合法状态。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (long Ref, ulong Flags) ReadEdge(int slot, int level)
    {
        var off = (slot * _edgeStride + level) * EdgeSize;
        var lo = Volatile.Read(ref _edgeMem.GetRefUnsafe<ulong>(off));
        var hi = Volatile.Read(ref _edgeMem.GetRefUnsafe<ulong>(off + 8));
        return ((long)lo, hi);
    }

    /// <summary>16B CAS（location 64B 对齐背板内，16B 对齐）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CasEdge(int slot, int level, long expRef, ulong expFlags, long newRef, ulong newFlags) =>
        NativeAtomic128.CompareExchange(ref EdgeAt(slot, level),
            new Int128((ulong)expRef, expFlags), new Int128((ulong)newRef, newFlags));

    // ════════════════════════════════════════════════════════════
    //  槽位寻址原语
    // ════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Encode(int idx) => ((long)idx << GenShift) | (uint)_gen[idx];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int Index, int Gen) Decode(long slotRef) => ((int)(slotRef >> GenShift), (int)(slotRef & GenMask));

    /// <summary>fail-visible 绊线：代数不匹配 = 陈旧边（epoch 严格性被破坏或世代回绕）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckGen(int idx, int gen)
    {
        if (Volatile.Read(ref _gen[idx]) != gen)
            ThrowStaleGen(idx, gen, -1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckGen(int idx, int gen, int level)
    {
        if (Volatile.Read(ref _gen[idx]) != gen)
            ThrowStaleGen(idx, gen, level);
    }

    [DoesNotReturn]
    private void ThrowStaleGen(int idx, int gen, int level)
        => throw new InvalidOperationException(
            $"[AsyncPriorityQueueV2] fail-visible：陈旧槽位引用 slot={idx} gen={gen} " +
            $"当前gen={Volatile.Read(ref _gen[idx])} level={level}——level-0 链快照：{DumpChain0()}"
#if DEBUG
            + $"\n── slot={idx} 租还历史──{SlotHistory(idx)}"
#endif
            );

    [DoesNotReturn]
    private static void ThrowStaleRef(int idx, int gen)
        => throw new InvalidOperationException(
            $"[AsyncPriorityQueueV2] fail-visible：陈旧槽位引用 slot={idx} gen={gen} " +
            $"（epoch 严格性被破坏，或 16 位世代回绕）——对照 epoch 示波器取证");

    /// <summary>DEBUG 取证：dump level-0 主链（key/gen/边 mark 状态）。</summary>
    private string DumpChain0()
    {
        var sb = new System.Text.StringBuilder();
        var steps = 0;
        var (curr, flags) = ReadEdge(HeadIndex, 0);
        while (curr != 0 && steps++ < 64)
        {
            var (idx, gen) = Decode(curr);
            if (Volatile.Read(ref _gen[idx]) != gen)
            {
                sb.Append($" -> [STALE slot={idx} gen={gen} cur={Volatile.Read(ref _gen[idx])}]");
                break;
            }
            var key = SlotAt(idx).Key;
            var next = ReadEdge(idx, 0);
            var mark = flags != 0 ? "M" : "";
            var nextDesc = next.Ref == 0 ? "0" : $"{Decode(next.Ref).Index}#{Decode(next.Ref).Gen}";
            sb.Append($" -> [{key}|{idx}#{gen}{mark}|→{nextDesc}]");
            curr = next.Ref;
            flags = 0;
        }
        return sb.ToString();
    }

    /// <summary>写链校验：splice/发布写进活链的 realNext 必须代数有效（写时存活性=写入安全性的前置）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long CheckLiveRef(long slotRef)
    {
        if (slotRef != 0)
        {
            var (idx, gen) = Decode(slotRef);
            CheckGen(idx, gen);
        }
        return slotRef;
    }

    // ════════════════════════════════════════════════════════════
    //  槽位池（侵入式空闲链 + 64 位 tag CAS——零分配、ABA 免疫）
    // ════════════════════════════════════════════════════════════

    /// <summary>非阻塞租槽（-1 = 池空）。<b>禁止在保护区内为池空自旋</b>——回收需要 epoch
    /// 静默，而自旋线程自身持保护会永久推迟 drain（B' 首轮压力楔死根因）。池空时上层
    /// 退出保护区重试（flush 在循环顶）。</summary>
    private int TryRentSlot()
    {
        var spin = new SpinWait();
        var tries = 0;
        while (true)
        {
            var h = Volatile.Read(ref _freeHead);
            var head = (int)(h >> 16);
            if (head == 0) return -1;
            // ★ 归还必推进 tag → 本 CAS 必失败重试（旧 32 位头会读到节点 Key 当索引 → AV 根因）。
            var next = SlotAt(head).NextFree;
            if (Interlocked.CompareExchange(ref _freeHead, ((long)next << 16) | ((h + 1) & 0xFFFF), h) == h)
            {
#if DEBUG
                Volatile.Write(ref _inFree[head], false);
                TraceSlot(1, head);
#endif
                return head;
            }
            if (++tries > 64) return -1;   // 竞争激烈也让位给上层重试（保护区外）
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
                    $"[AsyncPriorityQueueV2] 双归还！slot={idx} gen={_gen[idx]} 上次来源={_lastFreeSrc[idx]}（1=FreeSlotNow 2=RecycleSlot）" +
                    $"\n── 环状态：drained={_ringDrained} claim={Volatile.Read(ref _ringClaim)} pending={Volatile.Read(ref _pendingCount)}" +
                    $"\n── slot={idx} 租还历史──{SlotHistory(idx)}");
#endif
            var h = Volatile.Read(ref _freeHead);
            SlotAt(idx).NextFree = (int)(h >> 16);
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

    /// <summary>立即归还（仅限<b>从未发布</b>的槽——无任何边指向，无需 epoch）。</summary>
    private void FreeSlotNow(int idx, byte reason = 0)
    {
        _items[idx] = default;
        SlotAt(idx).Kind = KindNode;
        SlotAt(idx).LinkState = LinkDone;
        Volatile.Write(ref _gen[idx], (_gen[idx] + 1) & GenMask);
#if DEBUG
        TraceSlot(4, idx);
        _lastFreeSrc[idx] = reason;
#endif
        PushFree(idx);
    }

    /// <summary>drain action 专用归还（epoch 静默后，无读者）。</summary>
    private void RecycleSlot(int idx)
    {
        _items[idx] = default;
        SlotAt(idx).Kind = KindNode;
        SlotAt(idx).LinkState = LinkDone;
        Volatile.Write(ref _gen[idx], (_gen[idx] + 1) & GenMask);
#if DEBUG
        TraceSlot(2, idx);
        _lastFreeSrc[idx] = 2;
#endif
        PushFree(idx);
    }

    // ════════════════════════════════════════════════════════════
    //  pending 环 + epoch 批量回收
    // ════════════════════════════════════════════════════════════

    /// <summary>全层摘除完成后才可入 pending。入环时打 epoch 标签（= 入环时刻全局 epoch）——
    /// 一切可能持陈旧引用的读者都在 ≤ 标签 epoch 启动的段内，drain 只回收标签 ≤ 安全 epoch 的块。
    /// <para><b>CAS 领取 + filled 标志（无 relay、无洞）</b>：空间预检与领取原子（CAS 失败重试）；
    /// 满则自旋让位——绝不先消耗 claim 再抛异常（旧 relay 协议下，洞会永久卡死后续块的顺序发布）。
    /// 领取后填充窗口无任何 throw：内容 → 标签 → filled=1（release）。</para></summary>
    private void ScheduleReclaim(int victim)
    {
#if DEBUG
        // ★ 必须在领取前校验（领取后任何 throw 都会在 claim 序列留洞）
        if (Volatile.Read(ref _inFree[victim]))
            throw new InvalidOperationException(
                $"[AsyncPriorityQueueV2] schedule 重复！slot={victim} gen={_gen[victim]} " +
                $"drained={Volatile.Read(ref _ringDrained)} claim={Volatile.Read(ref _ringClaim)}" +
                $"\n── slot={victim} 租还历史──{SlotHistory(victim)}");
#endif
        var spin = new SpinWait();
        while (true)
        {
            var c = Volatile.Read(ref _ringClaim);
            if (c - Volatile.Read(ref _ringDrained) >= _ringBlocks)
            {
                spin.SpinOnce();   // 环满：让位等 drain 前进（契约内不可能——每槽一次出队最多一块）
                continue;
            }
            if (Interlocked.CompareExchange(ref _ringClaim, c + 1, c) != c) continue;
            var claim = c;
            _pending[claim % _ringBlocks] = victim;
            Volatile.Write(ref _pendingTags[claim % _ringBlocks], _epoch.CurrentEpoch);
#if DEBUG
            TraceSlot(3, victim);
#endif
            Volatile.Write(ref _ringFilled[claim % _ringBlocks], 1);   // release：内容/标签对 drain 可见
            Interlocked.Increment(ref _pendingCount);
            return;
        }
    }

    /// <summary>阈值降频 flush——<b>必须在 epoch 保护区外调用</b>（本方法内部 fresh Resume）。
    /// ★ 教训：保护区内调 bump，bumper 自持旧 epoch——16 槽 drain 列表打满后 action 的
    ///   触发条件（epoch 静默）恰被 bumper 自己挡住 → 槽位循环自旋死锁（旧 AsyncPriorityQueue
    ///   挂死事故同型）。保护区外 fresh Resume 取当前 epoch，不挡任何旧 action。</summary>
    private void FlushReclaims()
    {
        if (Volatile.Read(ref _pendingCount) < FlushThreshold) return;
        _epoch.Resume();
        try { _epoch.BumpCurrentEpoch(_drainAction); }
        finally { _epoch.Suspend(); }
    }

    /// <summary>drain action（缓存 delegate，纯内存、无异常、零分配）：epoch 静默期后批量归还。
    /// <para>★ 只回收「标签 ≤ 当前安全 epoch」的块：块的摘除/入环发生于标签 epoch 或更早，
    /// 一切可能持陈旧引用的读者都在 ≤ 标签 epoch 启动的段内，安全 epoch ≥ 标签 ⟹ 这些段全部退出。
    /// 标签在环序上不保证单调（claim 先行、标签读取可滞后），故只推进「连续已排前缀」——
    /// 高标签块之后的达标块留待后续 drain（safe 单调递增，必然收敛）。</para>
    /// <para>★ <b>回收前终检（I4 补完）</b>：静默期保证一切 ≤ 标签 epoch 段的迟到 splice 均已落地；
    /// 逐层重走——摘除 victim 的残余入边，并帮摘路径上<b>全部</b>已删节点。任何曾读到陈旧后继的
    /// 迟到 splice 都以「已删节点仍挂链」为 CAS 基——本 walk 先一步摘掉该节点 ⟹ 其 CAS 必败，
    /// 陈旧值无法再入活链。终检后无任何边可达 victim，回收安全。</para></summary>
    private void DrainReclaims()
    {
        lock (_recycleLock)
        {
            var safe = _epoch.SafeToReclaimEpoch;
            var claim = Volatile.Read(ref _ringClaim);   // 上界：领取即计——未填充块以 filled=0 跳过
            var drained = Volatile.Read(ref _ringDrained);
            var blk = drained;
            while (blk < claim)
            {
                if (Volatile.Read(ref _ringFilled[blk % _ringBlocks]) == 0) break;   // 未填充（在途/洞）→ 停
                if (Volatile.Read(ref _pendingTags[blk % _ringBlocks]) > safe) break;
                var vIdx = _pending[blk % _ringBlocks];
                var vGen = _gen[vIdx];              // pending 期间未回收——代数即调度代数
                var vKey = SlotAt(vIdx).Key;        // 同样仍有效
                var vTop = SlotAt(vIdx).TopLevel;
                for (var i = 0; i <= vTop; i++)
                    CleanAndFindFirstGe(vKey, i, vIdx, vGen);
                RecycleSlot(vIdx);
                Volatile.Write(ref _ringFilled[blk % _ringBlocks], 0);   // 清标志——环回绕复用同一位置
                blk++;
            }
            if (blk != drained)
            {
                // ★ Volatile.Write：生产者空间检查读的是 Volatile.Read——无此屏障，ring 回绕时
                //   生产者可见陈旧水位 → 覆盖本 drain 正在处理的块 → 回收垃圾索引/活槽 → 双归还。
                Volatile.Write(ref _ringDrained, blk);
                Interlocked.Add(ref _pendingCount, drained - blk);
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  FIND——Harris 遍历（删除检测 = 节点自身边 mark）+ helping 摘除
    // ════════════════════════════════════════════════════════════

    private void Find(long key, Span<long> preds, Span<long> succs)
    {
        for (var level = _maxLevel; level >= 0; level--)
        {
            // ★ 每层从 head 重新遍历——predIdx 不跨层携带（跨层 pred 可能未链接于该层）
            var predIdx = HeadIndex;
            var (curr, _) = ReadEdge(predIdx, level);
            while (true)
            {
                while (curr != 0)
                {
                    var (cIdx, cGen) = Decode(curr);
                    CheckGen(cIdx, cGen, level);
                    var (next, nextFlags) = ReadEdge(cIdx, level);
                    if (nextFlags != Marked) break;
                    // curr 已删 → helping 摘除（CAS 失败也沿自身边继续——遍历正确性不依赖帮助）
                    CasEdge(predIdx, level, curr, Unmarked, CheckLiveRef(next), Unmarked);
                    curr = next;
                }
                if (curr == 0) break;

                var (ccIdx, _) = Decode(curr);
                if (SlotAt(ccIdx).Key < key)
                {
                    predIdx = ccIdx;
                    curr = ReadEdge(predIdx, level).Ref;
                    continue;
                }
                break;
            }
            preds[level] = Encode(predIdx);
            succs[level] = curr;
        }
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
    //  ENQUEUE
    // ════════════════════════════════════════════════════════════

    /// <summary>入队元素。</summary>
    /// <exception cref="ObjectDisposedException">队列已释放。</exception>
    /// <exception cref="InvalidOperationException">槽位池耗尽（容量不足）。</exception>
    public void Enqueue(T item, int priority)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var seq = Interlocked.Increment(ref _sequenceCounter);
        var key = ((long)priority << 48) | (seq & 0xFFFFFFFFFFFF);
        var topLevel = RandomLevel();

        Span<long> preds = stackalloc long[_maxLevel + 1];
        Span<long> succs = stackalloc long[_maxLevel + 1];
        var spin = new SpinWait();

        while (true)
        {
            FlushReclaims();   // ★ 保护区外 flush（见 FlushReclaims 注释）——清上一轮 pending 积累
            _epoch.Resume();
            try
            {
                // 非阻塞租槽：池空 → 退出保护区重试（保护区内等池空 = 自锁死；flush 在循环顶）
                var nodeIdx = TryRentSlot();
                if (nodeIdx < 0)
                {
                    spin.SpinOnce();
                    continue;
                }
                SlotAt(nodeIdx).Key = key;
                SlotAt(nodeIdx).Sequence = seq;
                SlotAt(nodeIdx).Priority = priority;
                SlotAt(nodeIdx).TopLevel = topLevel;
                SlotAt(nodeIdx).Kind = KindNode;
                // ★ 发布前置 Linking：出队胜者摘除 walk 前等 Done——全部高层链接必先于 walk 落地
                Volatile.Write(ref SlotAt(nodeIdx).LinkState, LinkLinking);
                _items[nodeIdx] = item;
                for (var i = 0; i <= topLevel; i++) EdgeAt(nodeIdx, i) = default;   // 清槽复用残留

                Find(key, preds, succs);
                EdgeAt(nodeIdx, 0) = new Int128((ulong)succs[0], Unmarked);
                var (p0Idx, _) = Decode(preds[0]);
                if (CasEdge(p0Idx, 0, succs[0], Unmarked, Encode(nodeIdx), Unmarked))
                {
#if DEBUG
                    if (p0Idx == HeadIndex) TraceSlot(5, nodeIdx);   // head.edge[0] 发布取证
#endif
                }
                else
                {
                    FreeSlotNow(nodeIdx, 11);   // 未发布 → 立即回收（无读者可见）
                    spin.SpinOnce();
                    continue;
                }
                // 高层加速层：尽力链接（level-0 已发布，节点完全可达；失败仅少一条捷径）
                for (var i = 1; i <= topLevel; i++)
                {
                    EdgeAt(nodeIdx, i) = new Int128((ulong)succs[i], Unmarked);
                    var (pIdx, _) = Decode(preds[i]);
                    CasEdge(pIdx, i, succs[i], Unmarked, Encode(nodeIdx), Unmarked);
                }
                // ★ 链接全落地 → Done（release）：胜者 acquire 读 Done 后，walk 必见全部链接
                Volatile.Write(ref SlotAt(nodeIdx).LinkState, LinkDone);

                Interlocked.Increment(ref _count);
                _signal.Set();
                return;
            }
            finally { _epoch.Suspend(); }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  DEQUEUE——level-0 自身边 mark 决胜 → 等入队链接 Done → 全层按身份摘除 → 入 pending
    // ════════════════════════════════════════════════════════════

    /// <summary>尝试出队最小元素。</summary>
    public bool TryDequeue(out T item)
    {
        item = default!;
        if (_disposed != 0) return false;

        var spin = new SpinWait();

        while (true)
        {
            FlushReclaims();   // ★ 保护区外 flush（见 FlushReclaims 注释）
            _epoch.Resume();
            try
            {
                // level-0 首节点（跳过在途 winner 尚未摘除的已删节点）
                var (victimRef, _) = ReadEdge(HeadIndex, 0);
                while (victimRef != 0)
                {
                    var (xIdx, xGen) = Decode(victimRef);
                    CheckGen(xIdx, xGen);
                    var (xNext, xFlags) = ReadEdge(xIdx, 0);
                    if (xFlags != Marked) break;
                    CasEdge(HeadIndex, 0, victimRef, Unmarked, CheckLiveRef(xNext), Unmarked);   // 帮助摘除
                    victimRef = ReadEdge(HeadIndex, 0).Ref;
                }
                if (victimRef == 0) return false;

                var (vIdx, vGen) = Decode(victimRef);
                CheckGen(vIdx, vGen);   // 头边代数校验——失效即活链已有陈旧引用（上游写方违约）
                var topLevel = SlotAt(vIdx).TopLevel;
                var vKey = SlotAt(vIdx).Key;

                // ★ mark level-0 决胜（线性化点）：victim 自身边 CAS 置 mark 位的唯一胜者
                var won = false;
                while (true)
                {
                    var (succ0, f0) = ReadEdge(vIdx, 0);
                    if (f0 == Marked) break;   // 已被并发删除 → 放弃（重试整个操作）
                    if (succ0 != 0)
                    {
                        var (s0Idx, s0Gen) = Decode(succ0);
                        if (Volatile.Read(ref _gen[s0Idx]) != s0Gen)
                            throw new InvalidOperationException(
                                $"[V2] 探测：victim={vIdx}#{vGen} key={vKey} edge[0] 陈旧→{s0Idx}#{s0Gen} " +
                                $"当前gen={Volatile.Read(ref _gen[s0Idx])} victim当前gen={Volatile.Read(ref _gen[vIdx])}——链：{DumpChain0()}");
                    }
                    if (CasEdge(vIdx, 0, succ0, Unmarked, succ0, Marked))
                    {
                        won = true;
                        break;
                    }
                    spin.SpinOnce();
                }
                if (!won)
                {
                    spin.SpinOnce();
                    continue;
                }

                // ★ 胜者：等入队侧全部高层链接落地（LinkState 门——关闭 link-after-splice 竞态）
                var waitSpin = new SpinWait();
                var waits = 0;
                while (Volatile.Read(ref SlotAt(vIdx).LinkState) != LinkDone)
                {
                    if (++waits > 1 << 24)
                        throw new InvalidOperationException(
                            $"[AsyncPriorityQueueV2] 等待入队链接完成超时——victim={vIdx}#{vGen}（fail-visible）");
                    waitSpin.SpinOnce();
                }

                // ★ 全层 mark（Harris 原版）：victim 自身边逐层置 mark——各层删除对 helper 可见，
                //   已删节点的「自身边快照」成为 splice 的合法后继来源；drain 终检的帮摘
                //   使任何迟到 splice 的 CAS 基（已删节点仍挂链）先行失效——陈旧值无法入链。
                //   <b>无条件 mark</b>（含 stale 边——未链接层的垃圾边，冻结无害）：若按
                //   stale 跳过，已删节点在该层无 mark，walk/终检无法经由 mark 分支摘除它，
                //   其迟到 splice 的 CAS 基（pred 边）不被失效 → 陈旧写入穿透终检。
                for (var i = 1; i <= topLevel; i++)
                {
                    while (true)
                    {
                        var (succI, fI) = ReadEdge(vIdx, i);
                        if (fI == Marked) break;   // 已 mark（防御；唯胜者写自身边，正常不会发生）
                        if (CasEdge(vIdx, i, succI, Unmarked, succI, Marked)) break;
                        spin.SpinOnce();
                    }
                }

                // ★ I4 承重墙：全层物理摘除后才允许回收——victim 按身份摘除（高层无 mark，
                //   walk 遇 victim 即 splice）；途中帮摘所有已删节点。
                for (var i = 0; i <= topLevel; i++)
                    CleanAndFindFirstGe(vKey, i, vIdx, vGen);

                item = _items[vIdx]!;

                ScheduleReclaim(vIdx);
                Interlocked.Decrement(ref _count);
                return true;
            }
            finally { _epoch.Suspend(); }
        }
    }

    /// <summary>
    /// I4 前置：从 head 走 level-L 链，帮摘途中所有已删节点（自身边 mark），并按身份摘除 victim，
    /// 直至遇到第一个 key ≥ victimKey 的未删节点。
    /// <para><b>摘除纪律（Harris）</b>：mark 在节点自身边、splice 只 CAS <b>未删 pred 的边</b>；
    /// victim 自身边发布后永不改写——splice 永远读到死节点的真实后继（I1/I3）。</para>
    /// <para><b>无重启 + 基座前移（livelock-free）</b>：splice CAS 失败 = 他方已改边 = 进度，
    /// 重读继续；<b>链边自身被 mark（pred 并发删除）</b> = splice 基失效——沿冻结边前移基座，
    /// 绝不 CAS 冻结边（期望 (e,0) 永不匹配 → 旧版在此无限自旋，压力楔死根因）。位置单调前进，
    /// O(链长) 必终止。</para>
    /// </summary>
    private void CleanAndFindFirstGe(long victimKey, int level, int vIdx, int vGen)
    {
        var predIdx = HeadIndex;
        while (true)
        {
            var (e, f) = ReadEdge(predIdx, level);
            if (e == 0) return;
            // ★ 基座已删（其边被并发 mark）→ 沿冻结边前移（帮助摘除是基座前驱的事）
            while (f == Marked)
            {
                predIdx = Decode(e).Index;
                (e, f) = ReadEdge(predIdx, level);
                if (e == 0) return;
            }
            while (true)
            {
                var (nIdx, nGen) = Decode(e);
                CheckGen(nIdx, nGen, level);
                var (ne, nf) = ReadEdge(nIdx, level);
                var isVictim = nIdx == vIdx && nGen == vGen;
                if (nf != Marked && !isVictim) break;   // 活节点且非 victim → 前进
                // 已删或 victim → 尝试摘除；无论成败重读 pred 边（CAS 失败 = 他方已改 = 前进）
                CasEdge(predIdx, level, e, Unmarked, CheckLiveRef(ne), Unmarked);
                (e, f) = ReadEdge(predIdx, level);
                if (e == 0) return;
                while (f == Marked)
                {
                    predIdx = Decode(e).Index;
                    (e, f) = ReadEdge(predIdx, level);
                    if (e == 0) return;
                }
            }
            var (n2Idx, _) = Decode(e);
            if (SlotAt(n2Idx).Key >= victimKey) return;   // 已走过 victim 位置 → 该层已摘除
            predIdx = n2Idx;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  PEEK
    // ════════════════════════════════════════════════════════════

    /// <summary>查看队首元素而不移除。</summary>
    public bool TryPeek(out T item)
    {
        item = default!;
        if (_disposed != 0) return false;
        _epoch.Resume();
        try
        {
            var (curr, _) = ReadEdge(HeadIndex, 0);
            while (curr != 0)
            {
                var (idx, gen) = Decode(curr);
                CheckGen(idx, gen);
                var (next, flags) = ReadEdge(idx, 0);
                if (flags == Marked)
                {
                    curr = next;
                    continue;
                }
                item = _items[idx]!;
                return true;
            }
            return false;
        }
        finally { _epoch.Suspend(); }
    }

    // ════════════════════════════════════════════════════════════
    //  异步出队 / 计数 / 释放
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

    /// <summary>近似元素数（并发下仅诊断用）。</summary>
    public int Count { get { var c = Interlocked.Read(ref _count); return c < 0 ? 0 : (int)c; } }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _signal.Set();
        // 槽位/边表在原生内存——调用方须保证无并发操作后释放
        _arena.Dispose();
        _edgeMem.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  DEBUG 链校验器
    // ════════════════════════════════════════════════════════════

    /// <summary>走 level-0 主链校验：key 严格递增、代数全部有效、步数护栏（DEBUG 构建有效）。</summary>
    internal void ValidateInvariants()
    {
#if DEBUG
        _epoch.Resume();
        try
        {
            long last = long.MinValue;
            var (curr, _) = ReadEdge(HeadIndex, 0);
            var steps = 0;
            const int maxSteps = 1 << 24;
            while (curr != 0)
            {
                if (++steps > maxSteps)
                {
                    Debug.Fail($"AsyncPriorityQueueV2 level-0 链步数超护栏（疑似成环）：{steps}");
                    return;
                }
                var (idx, gen) = Decode(curr);
                CheckGen(idx, gen);
                var (next, flags) = ReadEdge(idx, 0);
                if (flags == Marked)
                {
                    curr = next;
                    continue;
                }
                Debug.Assert(SlotAt(idx).Key > last, $"level-0 链 key 非严格递增：prev={last} cur={SlotAt(idx).Key}");
                last = SlotAt(idx).Key;
                curr = next;
            }
        }
        finally { _epoch.Suspend(); }
#endif
    }
}
