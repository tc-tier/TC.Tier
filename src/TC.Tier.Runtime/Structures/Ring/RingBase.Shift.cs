using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 水位推进 partial。
/// <para>★ 全程 LogicalAddress + 引擎 API（CalculationAddress/GetDistance）。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    partial void PageAlignedShiftReadOnlyAddress(LogicalAddress newAddress)
    {
        long dist = _engine.GetDistance(_dataStart, newAddress);
        long intraPage = dist & PageSizeMask;
        LogicalAddress pageAligned = intraPage == 0 ? newAddress : _engine.CalculationAddress(newAddress, -intraPage);
        LogicalAddress desiredReadOnly = _readOnlyLagBytes > 0
            ? _engine.CalculationAddress(pageAligned, -_readOnlyLagBytes)
            : pageAligned;
        if (desiredReadOnly < BeginAddress) desiredReadOnly = BeginAddress;
        if (MonotonicUpdateAddr(WmReadOnly, desiredReadOnly, out _))
            _epoch.BumpCurrentEpoch(() => MonotonicUpdateAddr(WmSafeReadOnly, desiredReadOnly, out _));
    }

    partial void PageAlignedShiftHeadAddress(LogicalAddress newAddress)
    {
        long dist = _engine.GetDistance(_dataStart, newAddress);
        long intraPage = dist & PageSizeMask;
        LogicalAddress pageAligned = intraPage == 0 ? newAddress : _engine.CalculationAddress(newAddress, -intraPage);
        LogicalAddress desiredHead = _headOffsetLagBytes > 0
            ? _engine.CalculationAddress(pageAligned, -_headOffsetLagBytes)
            : pageAligned;
        if (desiredHead < BeginAddress) desiredHead = BeginAddress;
        ShiftHeadAddress(desiredHead);
    }

    internal LogicalAddress ShiftHeadAddress(LogicalAddress desiredHeadAddress)
    {
        var flushedUntil = FlushedUntilAddress;
        var newHead = desiredHeadAddress > flushedUntil ? flushedUntil : desiredHeadAddress;
        if (MonotonicUpdateAddr(WmHead, newHead, out _))
        {
            AdvanceHeadDist(newHead);   // ★ 读侧 head 守卫水位同点发布（8B CAS-max——别名守卫，先于任何 FreePage）
            _epoch.BumpCurrentEpoch(() => OnPagesClosed(newHead));
        }
        return newHead;
    }

    private void ShiftFlushedUntilAddress(LogicalAddress newFlushedUntil)
        => MonotonicUpdateAddr(WmFlushedUntil, newFlushedUntil, out _);

    /// <summary>
    /// ★ 关页单飞排他门（#161 修复）：drain action <b>可在不同线程并发触发</b>——LightEpoch.Drain
    /// 按槽 CAS 认领执行，不提供跨 action 串行化（旧注释"drain 串行触发"的主张与实现不符；
    /// 两个 writer 的 Resume/锁窗口交错时可同时挂起两个 OnPagesClosed action）。并发 OnPagesClosed
    /// 曾可双双进入 worker 对同一 slot 重复 FreePage（native double-free）。排他锁下单飞：
    /// worker 在锁内跑到当前在途目标；后到的更远目标接续跑（合并语义保留）。
    /// <para>★ 锁序：_ongoingCloseLock → 页池内部锁（单向，无反路径）；worker 纯内存操作，锁内无 IO。</para>
    /// </summary>
    private readonly object _ongoingCloseLock = new();

    private void OnPagesClosed(LogicalAddress newSafeHead)
    {
        if (!MonotonicUpdateAddr(WmSafeHead, newSafeHead, out _)) return;
        lock (_ongoingCloseLock)
        {
            if (_ongoingCloseUntilAddress >= newSafeHead) return;   // 在途目标已覆盖本次（safeHead 单调，仅推进触发）
            _ongoingCloseUntilAddress = newSafeHead;
            OnPagesClosedWorker();
        }
    }

    /// <summary>关页 worker（调用方须持 _ongoingCloseLock）——每次关一页并重读在途目标
    /// （对重入/目标推进收敛，闭环由 closeStart ≥ closeEnd 判定）。
    /// <para>★ STORAGE-087 让位与页尾全覆盖（#427 根治）：① closeEnd 可为页中间的已刷水位
    ///   （safeHead=min(desiredHead, flushed) 随 flushed 停在页中）——旧判定 <c>addr &lt; closeEnd</c>
    ///   会把「页尾仍越过已刷水位」的活页整页释放（页尾未落盘数据随回收销毁 + 并发 flush 迭代
    ///   撞 null fail-fast），改为页尾全覆盖：仅释放完整落在 closeEnd 之下的页，释放只推迟一页。
    ///   ② 在途 flush 登记（<c>_flushInFlightFromDist</c>）相交的页推迟回收。③ 槽所有权 CAS
    ///   认领（<see cref="TryClaimSlotForFree"/>）——绝对槽映射下同槽下一圈新页已被写入者接管时
    ///   不得清除/置空，仅推进关页水位。</para></summary>
    private void OnPagesClosedWorker()
    {
        while (true)
        {
            var closeEnd = _ongoingCloseUntilAddress;
            LogicalAddress closeStart = ClosedUntilAddress;
            if (closeStart >= closeEnd) return;

            long startDist = _engine.GetDistance(_dataStart, closeStart);
            long startIntra = startDist & PageSizeMask;
            LogicalAddress addr = startIntra == 0
                ? closeStart
                : _engine.CalculationAddress(closeStart, PageSize - startIntra);
            if (addr >= closeEnd) return;

            long pageSeq = _engine.GetDistance(_dataStart, addr) >> PageSizeBits;
            long pageEndDist = (pageSeq + 1) << PageSizeBits;

            long closeEndDist = _engine.GetDistance(_dataStart, closeEnd);
            if (pageEndDist > closeEndDist) return;   // 页尾越过已刷水位——整页未刷 durable，不得释放

            // ★ 在途 flush 相交即内联等待其完成（有界=单次 flush IO 时长）：worker 全程持
            //   _ongoingCloseLock，而 flush 的登记/注销中只有「登记」走该锁（早已完成）、注销
            //   是无锁 Volatile 写——此处自旋等注销不会死锁；换取关闭期无交错窗口且绝不饿死
            //   （让位 return 会因 safeHead 单调不前进而永不重触发，#427 压测 110min 挂起实锤）。
            var inFlightFrom = Volatile.Read(ref _flushInFlightFromDist);
            if (inFlightFrom >= 0 && pageEndDist > inFlightFrom)
            {
                var spin = 0;
                while (Volatile.Read(ref _flushInFlightFromDist) >= 0)
                {
                    if (++spin > 1000) Thread.Yield();
                    Thread.Sleep(1);
                }
            }

            // ★ 槽所有权 CAS 认领：绝对槽映射下 pageSeq 与上一圈同槽页共享槽位——写入者可能
            //   已接管槽写给下一圈页（此刻槽内是活数据）。认领失败=所有权已移交，本页无内存可还
            //   （其数据早已落盘），仅推进关页水位。
            int slot = (int)(pageSeq & PageCountMask);
            if (!TryClaimSlotForFree(slot, pageSeq))
            {
                addr = _engine.CalculationAddress(addr, PageSize);
                MonotonicUpdateAddr(WmClosedUntil, addr, out _);
                continue;
            }
            FreePage(slot);
            addr = _engine.CalculationAddress(addr, PageSize);
            MonotonicUpdateAddr(WmClosedUntil, addr, out _);
        }
    }
}
