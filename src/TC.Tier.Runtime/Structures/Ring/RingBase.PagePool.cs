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
    /// <summary>★ ClockCache 淘汰隔离池（#165/#177）：淘汰页先进隔离（FIFO），溢出才回落
    /// freePageCache——「淘汰→可复用/释放」间隔 ≥Q 次淘汰，同步栈内读者持有的缓存页 span
    /// 窗口（ns 级）≪ 隔离期，use-after-free/复用别名窗口关闭。</summary>
    private  OverflowPool<AlignedMemoryManager>? _evictionQuarantine;
    // === ★ 页索引（数组，非 Dictionary）===
    internal LogicalAddress[] _pageLogicalBySlot;                    // slot → 页起点（淘汰/落盘用）
    /// <summary>slot → 当前租户页序号（-1=空闲/未分配）。绝对槽映射下 pageSeq 与 pageSeq±k·PageCount
    /// 共享同一槽——关页 worker 释放上一圈页时，同槽下一圈新页可能已被写入者分配并写入
    /// （FreePage 的 ClearPage 会清掉活数据，#427 压测实锤）。所有权交接经 Interlocked CAS 认领：
    /// worker 仅在槽仍归被释放页所有时才获准释放；写入者 EnsurePageAllocated 写入新租户序号即接管。</summary>
    private long[] _pageSeqBySlot;               // slot → 租户页序号（-1=未分配/已释放）
    /// <summary>已分配页数。</summary>
    private protected int AllocatedPageCount;

    /// <summary>初始化页池。Preallocate=true 全量预分配所有页的 native 内存。</summary>
    private void InitializePagePool(bool preallocate)
    {
        _pages = new AlignedMemoryManager?[PageCount];
        _nativePagePointers = new long[PageCount];
        _pageLogicalBySlot = new LogicalAddress[PageCount];
        _pageSeqBySlot = new long[PageCount];
        _pagePool = new PinnedBufferPool();
        // ★ #209：容量 4 → PageCount/8（下限 4）——8192 页轮回时 4 槽池 99.95% 淘汰直落
        //   Dispose+重分配（纯损耗），池化命中率随容量回升；内存驻留上界 = MemorySize/8（页本身是
        //   回收复用的，非额外分配）
        _freePageCache = new OverflowPool<AlignedMemoryManager>(Math.Max(4, PageCount / 8), static p => p.Dispose());
        // ★ #165/#177：隔离池容量与 freePageCache 同量级；溢出回落 freePageCache（它满则 Dispose）
        _evictionQuarantine = new OverflowPool<AlignedMemoryManager>(Math.Max(4, PageCount / 8),
            amm => _freePageCache.TryAdd(amm));

        if (preallocate)
        {
            for (int i = 0; i < PageCount; i++)
            {
                AllocatePage(i);
                Volatile.Write(ref _pageSeqBySlot[i], i);   // 预分配页的首圈租户即 pageSeq=i
            }
        }
    }

    /// <summary>★ 确保页槽已分配 native 内存（pageSeq = 窗口内 offset >> PageSizeBits，环形复用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void EnsurePageAllocated(long pageSeq)
    {
        int slot = (int)(pageSeq & PageCountMask);
        _pageLogicalBySlot[slot] = _engine.CalculationAddress(_dataStart, pageSeq << PageSizeBits);
        if (_pages[slot] is null) AllocatePage(slot);
        // ★ 租户登记放分配之后：seq 非负 ⟹ 槽内有活页内存。写入者写新序号即从上一圈页
        //   （绝对映射同槽）接管所有权——关页 worker 的 CAS 释放看到新序号即让位不碰槽。
        Volatile.Write(ref _pageSeqBySlot[slot], pageSeq);
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
        else if (_evictionQuarantine!.TryGet(out var quarantined) && quarantined is not null)
            page = quarantined;   // ★ 隔离期满页回流——其淘汰后已隔 ≥Q 次淘汰（#165/#177 窗口安全）
        else
            page = _pagePool.RentAligned(PageSize, TC.Tier.Core.IO.DirectIo.BufferAlignmentFloor(SectorSize));
        page.GetSpan(0, PageSize).Clear();   // ★ 新租页清零：租借内存含跨测试/跨分配残留垃圾——预分配形态下
                                             //   残留被写穿/扫描误读为有效记录（幻影条目，跨实例 count 多读实锤）
        _pages[slot] = page;
        _nativePagePointers[slot] = (long)page.Ptr;
        Interlocked.Increment(ref AllocatedPageCount);
    }

    /// <summary>★ 释放页（关页 worker 专用——调用方须先经 <see cref="TryClaimSlotForFree"/> CAS 认领
    /// 槽所有权）：清内容 + 归还缓存 + 置空。</summary>
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

    /// <summary>★ 关页释放的槽所有权 CAS 认领（STORAGE-087 根治）：仅当槽仍归被释放页
    /// （租户序号 = pageSeq）时才获准释放；同槽下一圈新页已接管（租户序号已前移）则返回
    /// false——内存所有权已移交，清除/置空都不得做。认领成功后由调用方 <see cref="FreePage"/>。</summary>
    private protected bool TryClaimSlotForFree(int slot, long pageSeq)
        => Interlocked.CompareExchange(ref _pageSeqBySlot[slot], -1, pageSeq) == pageSeq;

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
    internal ClockCache<LogicalAddress, AlignedMemoryManager>? ColdPageCacheForTest => _coldPageCache;
    /// <summary>★ 测试访问器：注入的 record codec（ScanPageForRecords 撕裂重同步单测用）。</summary>
    internal IRingCodec CodecForTest => RingCodec;
    /// <summary>★ 测试访问器：ClockCache 淘汰隔离池（#165/#177 隔离语义单测用）。</summary>
    internal OverflowPool<AlignedMemoryManager>? EvictionQuarantineForTest => _evictionQuarantine;
    /// <summary>★ 测试访问器：驱逐页缓存池（#209 容量策略单测用）。</summary>
    internal OverflowPool<AlignedMemoryManager>? FreePageCacheForTest => _freePageCache;

    // === 水位推进（Shift partial 提供 partial void 实现）===
    partial void PageAlignedShiftReadOnlyAddress(LogicalAddress newAddress);
    partial void PageAlignedShiftHeadAddress(LogicalAddress newAddress);
}
