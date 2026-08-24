using System.Buffers;
using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 页池 partial——固定 N 页 native 内存 + 数组索引。
/// <para>★ 内存模型（"持久化的内存" + 固定 N 页）：</para>
/// <para>- 固定 PageCount 个页槽（数组下标 0..N-1），槽号 = 下标。</para>
/// <para>- Ring 数据区起点 _firstPageLogical（首个页的 LogicalAddress，构造时确定）。</para>
/// <para>- 全局页序号 pageSeq = GetDistance(_firstPageLogical, 页起点) / PageSize（单调递增）。</para>
/// <para>- 槽位映射 slot = (pageSeq - _pageSeqHead) &amp; PageCountMask（环形缓冲，1 条 AND 指令）。</para>
/// <para>- 页复用 = 驱逐最旧页推进 _pageSeqHead，新页占释放的槽。</para>
/// <para>★ 热读 addr：dist = GetDistance(_firstPageLogical, addr)；seq = dist &gt;&gt; PageSizeBits；
///   slot = (seq - _pageSeqHead) &amp; PageCountMask；pageIntra = dist &amp; PageSizeMask。</para>
/// <para>★ 全程不碰 LogicalAddress.Offset 算术（§8 铁律）。</para>
/// <para>参见 base.md §2.1。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    // === ★ 页内存：固定 PageCount 个页槽 ===
    private  AlignedMemoryManager?[] _pages;          // PageCount 个页槽（null = 未分配/已驱逐）
    private  long[] _nativePagePointers;               // 每页 native 指针值（热路径 GetPhysicalAddress 索引）
    private  PinnedBufferPool _pagePool;               // 页池（Rent/Return 复用）
    private  OverflowPool<AlignedMemoryManager> _freePageCache;  // 驱逐页缓存（O(1) 复用）
    // === ★ 页索引（数组，非 Dictionary）===
    internal LogicalAddress[] _pageLogicalBySlot;                    // slot → 页起点（淘汰/落盘用）
    /// <summary>已分配页数。</summary>
    private protected int AllocatedPageCount;

    /// <summary>初始化页池。Preallocate=true 全量预分配所有页的 native 内存。</summary>
    private void InitializePagePool(bool preallocate)
    {
        _pages = new AlignedMemoryManager?[PageCount];
        _nativePagePointers = new long[PageCount];
        _pageLogicalBySlot = new LogicalAddress[PageCount];
        _pagePool = new PinnedBufferPool();
        _freePageCache = new OverflowPool<AlignedMemoryManager>(4, static p => p.Dispose());

        if (preallocate)
        {
            for (int i = 0; i < PageCount; i++)
                AllocatePage(i);
        }
    }

    /// <summary>★ 确保页槽已分配 native 内存（pageSeq = 窗口内 offset >> PageSizeBits，环形复用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void EnsurePageAllocated(long pageSeq)
    {
        int slot = (int)(pageSeq & PageCountMask);
        _pageLogicalBySlot[slot] = _engine.CalculationAddress(_dataStart, pageSeq << PageSizeBits);
        if (_pages[slot] is null) AllocatePage(slot);
    }

    /// <summary>★ 分配页 native 内存。
    /// <para>★ 对齐取 DIO 地板而非卷扇区：Windows DIO 缓冲地址须 max(扇区, 系统页=4096) 对齐——
    /// 按扇区（512）租页令页地址 7/8 概率失配，flush 落盘随机抛对齐错
    /// （Crash_DioMode_DataDurable_CrossInstance 实锤）。4096 对齐 ⊇ 扇区对齐，Linux 同安。</para></summary>
    private protected unsafe void AllocatePage(int slot)
    {
        AlignedMemoryManager page;
        if (_freePageCache.TryGet(out var recycled) && recycled is not null)
            page = recycled;
        else
            page = _pagePool.RentAligned(PageSize, TC.Tier.Core.IO.DirectIo.BufferAlignmentFloor(SectorSize));
        _pages[slot] = page;
        _nativePagePointers[slot] = (long)page.Ptr;
        Interlocked.Increment(ref AllocatedPageCount);
    }

    /// <summary>★ 释放页（epoch drain 回调里调）：清内容 + 归还缓存 + 推进 _pageSeqHead（驱逐最旧）。</summary>
    private protected void FreePage(int slot)
    {
        var page = _pages[slot];
        if (page is null) return;
        ClearPage(slot);
        _freePageCache.TryAdd(page);
        _pages[slot] = null;
        _pageLogicalBySlot[slot] = LogicalAddress.Empty;
        Interlocked.Decrement(ref AllocatedPageCount);
    }

    /// <summary>清页（归零）。</summary>
    private protected void ClearPage(int slot, int offset = 0)
    {
        if (_pages[slot] is { } page)
            page.GetSpan(offset, PageSize - offset).Clear();
    }

    /// <summary>页是否已分配。</summary>
    private protected bool IsAllocated(int slot) => _pages[slot] is not null;

    // === test-only 只读访问器 ===
    internal int AllocatedPageCountForTest => Volatile.Read(ref AllocatedPageCount);
    internal bool IsAllocatedForTest(int slot) => IsAllocated(slot);

    // === 水位推进（Shift partial 提供 partial void 实现）===
    partial void PageAlignedShiftReadOnlyAddress(LogicalAddress newAddress);
    partial void PageAlignedShiftHeadAddress(LogicalAddress newAddress);
}
