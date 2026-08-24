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
        if (MonotonicUpdateAddr(ref _readOnlyAddress, desiredReadOnly, out _))
            _epoch.BumpCurrentEpoch(() => MonotonicUpdateAddr(ref _safeReadOnlyAddress, desiredReadOnly, out _));
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
        if (MonotonicUpdateAddr(ref _headAddress, newHead, out _))
        {
            _epoch.BumpCurrentEpoch(() => OnPagesClosed(newHead));
        }
        return newHead;
    }

    private void ShiftReadOnlyAddress(LogicalAddress newReadOnlyAddress)
        => MonotonicUpdateAddr(ref _readOnlyAddress, newReadOnlyAddress, out _);

    private void ShiftFlushedUntilAddress(LogicalAddress newFlushedUntil)
        => MonotonicUpdateAddr(ref _flushedUntilAddress, newFlushedUntil, out _);

    private void OnPagesClosed(LogicalAddress newSafeHead)
    {
        if (!MonotonicUpdateAddr(ref _safeHeadAddress, newSafeHead, out _)) return;
        for (;; Thread.Yield())
        {
            LogicalAddress ongoing = _ongoingCloseUntilAddress;
            if (ongoing >= newSafeHead) break;
            if (InterlockedCasAddr(ref _ongoingCloseUntilAddress, newSafeHead, ongoing) == ongoing)
            {
                if (ongoing == LogicalAddress.Empty) OnPagesClosedWorker();
                return;
            }
        }
    }

    private void OnPagesClosedWorker()
    {
        for (;; Thread.Yield())
        {
            LogicalAddress closeStart = ClosedUntilAddress;
            LogicalAddress closeEnd = _ongoingCloseUntilAddress;
            long startDist = _engine.GetDistance(_dataStart, closeStart);
            long startIntra = startDist & PageSizeMask;
            LogicalAddress pageAlignedStart = startIntra == 0
                ? closeStart
                : _engine.CalculationAddress(closeStart, PageSize - startIntra);
            LogicalAddress addr = pageAlignedStart;
            while (addr < closeEnd)
            {
                long pageSeq = _engine.GetDistance(_dataStart, addr) >> PageSizeBits;
                int slot = (int)(pageSeq & PageCountMask);
                FreePage(slot);
                addr = _engine.CalculationAddress(addr, PageSize);
                MonotonicUpdateAddr(ref _closedUntilAddress, addr, out _);
            }
            if (InterlockedCasAddr(ref _ongoingCloseUntilAddress, LogicalAddress.Empty, closeEnd) == closeEnd) break;
        }
    }

    /// <summary>
    /// ★ _ongoingCloseUntilAddress 的 CAS（read-modify-write 形式）。
    /// <para>★ STORAGE-008 设计说明：此处刻意用非原子 read-modify-write，而非 NativeAtomic128 真原子 CAS。
    /// 原因——这是 epoch drain 串行上下文：</para>
    /// <para>1. OnPagesClosed 是 _epoch.BumpCurrentEpoch(onDrain) 的回调（Shift.cs:42）。</para>
    /// <para>2. BumpCurrentEpoch(Action) 的 onDrain 仅在 prior epoch 所有线程退出（safeToReclaim）后执行
    ///   （LightEpoch.cs:255-263），且 drain 机制保证不同线程的 onDrain 串行触发——不会两个 OnPagesClosed 并发。</para>
    /// <para>3. 因此这里的"CAS"实际无真并发竞争，伪 CAS 只是协调"多个已排队的关页请求只有一个继续触发 worker"
    ///   的串行协调写法，read-modify-write 在单线程执行下天然原子。</para>
    /// <para>★ 改用 NativeAtomic128 真原子 CAS 是过度设计：LogicalAddress 是 16B struct，真 CAS 需字段 16B 对齐
    ///   （RingBase class 字段不保证，强用会 #GP）。drain 串行已保证安全，无需承受对齐复杂度与开销。</para>
    /// <para>参见 RingBase.cs:77 水位推进"单写者上下文"注释。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LogicalAddress InterlockedCasAddr(ref LogicalAddress location, LogicalAddress value, LogicalAddress comparand)
    {
        LogicalAddress current = location;
        if (current == comparand) { location = value; return comparand; }
        return current;
    }
}
