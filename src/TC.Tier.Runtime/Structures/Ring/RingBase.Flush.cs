using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase flush partial——AsyncFlushPages（整页落盘）。
/// <para>★ FASTER hybrid log 落盘：内存页内容 Write 回它对应的引擎地址（同地址，100% 确定）。</para>
/// <para>★ 页地址 = CalculationAddress(_dataStart, pageSeq × PageSize)（页在数据区的确定位置）。</para>
/// <para>★ Ring 自管 FlushedUntilAddress（已落盘水位）。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// 把 [from, until) 的整页内容 Write 回各自引擎地址。
    /// </summary>
    /// <remarks>★ STORAGE-009 设计契约（中间层 epoch 责任划分）：
    /// 本方法（及 AsyncFlushPagesAsync）<b>不内部持 _epoch/_tailLock</b>。原因：
    /// <list type="bullet">
    /// <item>flush 的调用方是写者上下文（FlushUntil 由 Append 写满页/Prepare 触发，写者经 TryAllocate
    ///   持 _tailLock + 持 _epoch——RingBase.Addressing.cs:63-64 TryAllocate 在 _tailLock 内 BumpCurrentEpoch）。
    ///   写者持 _epoch 期间，驱逐 worker 的 FreePage 经 BumpCurrentEpoch 排 drain，drain 等 epoch 退出才执行，
    ///   故 flush 期间 _pages[slot] 不会被释放/复用。</item>
    /// <item>内部再加 _epoch 会与写者已持的 epoch 嵌套（多余 Resume/Suspend 开销，且 BumpCurrentEpoch 嵌套触发警告）。</item>
    /// </list>
    /// 仍需保护的是"裸调用方未持 epoch 即 flush"——属调用契约违反（中间层不兜底），由上层保证。</remarks>
    private protected void AsyncFlushPages(LogicalAddress from, LogicalAddress until)
    {
        long fromDist = _engine.GetDistance(_dataStart, from);
        long untilDist = _engine.GetDistance(_dataStart, until);
        for (long dist = fromDist; dist < untilDist; )
        {
            long pageIntra = dist & PageSizeMask;
            long pageBaseDist = pageIntra == 0 ? dist : (dist - pageIntra);   // 页起点在数据区的偏移
            long pageSeq = pageBaseDist >> PageSizeBits;
            int slot = (int)(pageSeq & PageCountMask);
            if (_pages[slot] is { } page)
            {
                var pageAddr = _engine.CalculationAddress(_dataStart, pageBaseDist);
                _engine.Write(pageAddr, page.GetSpan(0, PageSize));
            }
            dist = pageBaseDist + PageSize;
        }
        _engine.Flush();
        _overflowEngine?.Flush();
    }

    private protected async ValueTask AsyncFlushPagesAsync(LogicalAddress from, LogicalAddress until, CancellationToken ct)
    {
        long fromDist = _engine.GetDistance(_dataStart, from);
        long untilDist = _engine.GetDistance(_dataStart, until);
        for (long dist = fromDist; dist < untilDist; )
        {
            long pageIntra = dist & PageSizeMask;
            long pageBaseDist = pageIntra == 0 ? dist : (dist - pageIntra);
            long pageSeq = pageBaseDist >> PageSizeBits;
            int slot = (int)(pageSeq & PageCountMask);
            if (_pages[slot] is { } page)
            {
                var pageAddr = _engine.CalculationAddress(_dataStart, pageBaseDist);
                await _engine.WriteAsync(pageAddr, page.Memory, ct).ConfigureAwait(false);
            }
            dist = pageBaseDist + PageSize;
        }
        _engine.Flush();
        _overflowEngine?.Flush();
    }

    /// <summary>★ 同步 flush 到指定地址（Prepare 用）。</summary>
    public void FlushUntil(LogicalAddress untilAddress)
    {
        LogicalAddress currentFlushed = FlushedUntilAddress;
        if (untilAddress > currentFlushed)
        {
            AsyncFlushPages(currentFlushed, untilAddress);
            ShiftFlushedUntilAddress(untilAddress);
        }
    }

    /// <summary>★ 异步 flush 到指定地址（PrepareAsync 用）。</summary>
    public async ValueTask FlushUntilAsync(LogicalAddress untilAddress, CancellationToken ct = default)
    {
        LogicalAddress currentFlushed = FlushedUntilAddress;
        if (untilAddress > currentFlushed)
        {
            await AsyncFlushPagesAsync(currentFlushed, untilAddress, ct).ConfigureAwait(false);
            ShiftFlushedUntilAddress(untilAddress);
        }
    }
}
