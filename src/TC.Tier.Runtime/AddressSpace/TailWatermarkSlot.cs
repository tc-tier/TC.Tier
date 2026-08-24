using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 双尾水位 CAS 原语——第一层（独立 sealed class）。
/// <para>★ 128 位 CAS 机制<b>委托 <see cref="Atomic128{T}"/></b>（16B 对齐背板 + 能力探测降级 + 裸读不撕裂）——
///   本类不再自管 <c>AlignedMemoryManager</c> / <c>ProbeNativeCas</c> / <c>_fallbackLock</c>，只保留双尾业务语义。</para>
/// <para>★ 两个独立 <see cref="Atomic128{T}"/> 槽：AllocatedTail + CommittedTail（各自 64B 对齐背板，
///   独占缓存行——实测两个独立块并发性能优于单个 128B 块，2T 2.19M vs 1.35M ops/s，
///   避免 Intel Spatial Prefetcher 绑定相邻缓存行）。</para>
/// <para>★ 精确 CAS：<see cref="TryUpdateAllocated"/>/<see cref="TryUpdateCommitted"/>（expected→value 位精确比较，含 Extension）。</para>
/// <para>★ 双尾条件回退：<see cref="Retreat"/>（委托 <see cref="RetreatIfHigher"/>，各 CAS 循环，version+1 防 ABA）。</para>
/// <para>★ <see cref="Load"/>/<see cref="Reset"/> 装配内部方法（启动期单线程裸写，不对外开放）。</para>
/// <para>★ 双尾水位独占：<see cref="TryHoldTailWatermark"/>/<see cref="ReleaseTailWatermark"/>/<see cref="IsTailWatermarkHeld"/>——通用机制，回退者持有期间推进者自旋等待、并发回退拒绝。不绑定特定调用方（lease 协议是使用者之一）。</para>
/// <para>★ 关键正确性约束：</para>
/// <list type="number">
/// <item><description><see cref="RetreatIfHigher"/> 必须是 CAS 循环（不是裸写）——否则与并发 <see cref="TryUpdateAllocated"/> 推进冲突，产生 lost update</description></item>
/// <item><description>双尾各自独立 CAS，不保证相对原子——跨水位线一致性靠 <see cref="TryHoldTailWatermark"/> 水位独占（回退期间推进者等待 / 并发回退拒绝），不绑定 lease 协议</description></item>
/// </list>
/// </summary>
internal sealed class TailWatermarkSlot : IDisposable
{
    /// <summary>★ 两个独立 128 位 CAS 槽——各自独占 64B 对齐背板（缓存行），分配在堆上不同位置避免硬件预取干扰。</summary>
    private readonly Atomic128<LogicalAddress> _allocated = new();
    private readonly Atomic128<LogicalAddress> _committed = new();

    /// <summary>双尾水位独占标志——非 0 时水位推进者须等待、回退者须拒绝并发。通用机制，不绑定特定调用方（lease 协议是使用者之一）。</summary>
    private int _tailWatermarkHeld;

    public TailWatermarkSlot()
    {
        // 构造即写 Invalid 标记"未装配"。装配期由 Load 填真实值；Reset 回到 Invalid；disposed 后读返回 Invalid。
        // ★ 构造后/Reset 后/disposed 后统一为 Invalid——与 Empty 的 seg0 起点明确区分。
        Load(LogicalAddress.Invalid, LogicalAddress.Invalid);
    }

    // ── 读（无锁；disposed 返回 Invalid，保持原语义）──

    /// <summary>当前 AllocatedTail（分配水位）。</summary>
    internal LogicalAddress Allocated => ReadVal(_allocated);

    /// <summary>当前 CommittedTail（已提交水位）。</summary>
    internal LogicalAddress Committed => ReadVal(_committed);

    // ★ disposed 返回 Invalid（而非 default=Empty）——LogicalAddress 的 default 是合法 seg0 起点，
    //   不是哨兵；disposed/未装配须用 Invalid 标记。Atomic128.Read disposed 时返回 default，故这里特殊处理。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LogicalAddress ReadVal(Atomic128<LogicalAddress> slot)
        => slot.IsDisposed ? LogicalAddress.Invalid : slot.ReadUnsafe();

    // ── 双尾水位独占（通用机制：推进者等待 / 回退者拒绝并发）──

    /// <summary>尝试持有双尾水位独占（CAS 0→1）。成功=true；失败（已被持有）=false——原子 check-and-set，无 TOCTOU 窗口。调用方据返回值决定等待或抛异常。</summary>
    internal bool TryHoldTailWatermark() => Interlocked.CompareExchange(ref _tailWatermarkHeld, 1, 0) == 0;

    /// <summary>释放双尾水位独占（置 0）。</summary>
    internal void ReleaseTailWatermark() => Interlocked.Exchange(ref _tailWatermarkHeld, 0);

    /// <summary>双尾水位是否被独占——推进路径在自旋循环中检查，true 则退避等待。</summary>
    internal bool IsTailWatermarkHeld => Volatile.Read(ref _tailWatermarkHeld) != 0;

    /// <summary>
    /// ★ L13/L10（）：Allocated 是否已低于 <paramref name="v"/>——撕裂/CSE 免疫读。
    /// <para>★ 快路径：no-op CAS（原子）——当前值恰为 v 即未退（调用方 to 通常就是最新推进值）。</para>
    /// <para>★ 慢路径：MemoryBarrier + 稳定双读（两读一致才采信；16B 裸读可撕裂，JIT 可 CSE——
    ///   屏障阻提升、双读防撕裂；不一致自旋重试）。裸 <see cref="Allocated"/> 单读用于越界判定
    ///   会在 exact-fill 段界（下一推进 segId+offset 双变）读到旧值假阳性（L13 修复自伤实录）。</para>
    /// </summary>
    internal bool IsAllocatedBelow(LogicalAddress v)
    {
        if (_allocated.TryCompareExchangeUnsafe(v, v)) return false;   // == v：未退（原子判定）
        var spinner = new SpinWait();
        while (true)
        {
            Interlocked.MemoryBarrier();
            var r1 = _allocated.ReadUnsafe();
            Interlocked.MemoryBarrier();
            var r2 = _allocated.ReadUnsafe();
            if (r1 == r2) return r1 < v;
            spinner.SpinOnce();
        }
    }

    // ── 精确 CAS 推进（委托 Atomic128.TryCompareExchange，含 Extension 位精确比较）──

    /// <summary>精确 CAS 推进 AllocatedTail（expected → value，含 Extension 位精确比较）。
    /// ★ Unsafe 快路径——SegmentTable Dispose 顺序保证（先停 worker/lease 再 Dispose slot），
    ///   不会 disposed 后调，故跳过 IsDisposed/CasEnabled 检查榨性能。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryUpdateAllocated(LogicalAddress expected, LogicalAddress value)
    {
        // ★ L13 修复（，双向闭合）：水位独占期间的推进要么被拒绝要么自我撤销——
        //   ① 前检：hold 已置位直接失败（AllocateRaw 既有自旋退避重试）；
        //   ② 后验：CAS 成功瞬间 hold 被置位（检查→CAS 窗口被 ReclaimTail 插入）= 本推进
        //     可能越过后退边界——回退（CAS 回旧值）并报失败，调用方下一轮被 ① 挡住等待。
        //   ② 的回退 CAS 在 hold 下进行是安全的：回退到 expected（推进前原值），不越任何边界；
        //   ReclaimTail 侧"持 hold 后重读"看到的就是含/不含本推进的自洽快照，两序都正确。
        if (Volatile.Read(ref _tailWatermarkHeld) != 0) return false;
        if (!_allocated.TryCompareExchangeUnsafe(expected, value)) return false;
        if (Volatile.Read(ref _tailWatermarkHeld) == 0) return true;
        _allocated.TryCompareExchangeUnsafe(value, expected);   // 自我撤销（失败可容忍——持有者快照已含本推进，亦自洽）
        return false;
    }

    /// <summary>精确 CAS 推进 CommittedTail（expected → value，含 Extension 位精确比较）。Unsafe 快路径（同上）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryUpdateCommitted(LogicalAddress expected, LogicalAddress value)
        => _committed.TryCompareExchangeUnsafe(expected, value);

    // ── 双尾条件回退（各 CAS 循环，不持锁）──

    internal void Retreat(LogicalAddress newTail)
    {
        // ★ L13（）：先 Committed 后 Allocated——两槽无法原子双退，中间窗必现；
        //   先退 Committed 的瞬态是 C<A（不变量安全侧），先退 Allocated 则瞬态 C>A（探针
        //   稳定双读可捕获的假性破坏）。
        RetreatIfHigher(_committed, newTail);
        RetreatIfHigher(_allocated, newTail);
    }

    internal void RetreatAllocatedOnly(LogicalAddress newTail)
    {
        RetreatIfHigher(_allocated, newTail);
    }

    private void RetreatIfHigher(Atomic128<LogicalAddress> slot, LogicalAddress newTail)
    {
        var spinner = new SpinWait();
        while (true)
        {
            if (slot.IsDisposed) return;
            var cur = slot.ReadUnsafe();   // 循环开头已 IsDisposed 检查，循环内 Unsafe 省开销
            if (cur <= newTail) return;
            // ★ Extension+1 作 ABA 版本——Extension 是 int(32-bit)，理论上 2^31 次 Retreat 后回绕；
            //   实际不可达（每次 Retreat 对应一次 ReclaimTail 截断），不升级位宽
            var versioned = new LogicalAddress(newTail.SegId, cur.Extension + 1, newTail.Offset);
            if (slot.TryCompareExchangeUnsafe(cur, versioned)) return;
            spinner.SpinOnce();
        }
    }

    // ── 装配内部方法（不对外开放，仅供 LoadAddressTable 装配用）──

    internal void Load(LogicalAddress allocated, LogicalAddress committed)
    {
        if (_allocated.IsDisposed) return;
        _allocated.Store(allocated);
        _committed.Store(committed);
    }

    internal void Reset() => Load(LogicalAddress.Invalid, LogicalAddress.Invalid);

    // ── ApplyHints 裸写入口（启动期单线程，无并发写者）──

    internal void WriteAllocated(LogicalAddress value)
    {
        if (_allocated.IsDisposed) return;
        _allocated.Store(value);
    }

    internal void WriteCommitted(LogicalAddress value)
    {
        if (_committed.IsDisposed) return;
        _committed.Store(value);
    }

    public void Dispose()
    {
        _allocated.Dispose();
        _committed.Dispose();
    }
}
