using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 寻址 partial——热路径 TryAllocate/Seal/GetPhysicalAddress/GetSpan/GetInfo。
/// <para>★ FASTER hybrid log 地址模型（照 AllocatorBase，仅 long→LogicalAddress + 引擎 API）：</para>
/// <para>- 地址单调递增，永远向前；不回退（不需要 ReclaimTail，那是 Log 变长页才用的）。</para>
/// <para>- 内存固定 PageCount 个 native 槽，slot = pageSeq &amp; PageCountMask 循环复用。</para>
/// <para>- 写满一圈淘汰旧页（head 推进 FreePage），tail 继续 append（地址继续增长）。</para>
/// <para>★ 引擎地址空间提供 100% 确定的地址。Ring 基于 _dataStart + GetDistance 做 100% 正确寻址。</para>
/// <para>★ _dataStart 构造时 Allocate 确定（GetDistance 锚点）。pageSeq = GetDistance(_dataStart, addr) / PageSize。</para>
/// <para>★ 地址运算只用 CalculationAddress/GetDistance（§8 铁律，不碰 Offset 算术）。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    private readonly object _tailLock = new();

    // === 地址模型核心字段 ===
    private protected LogicalAddress _dataStart;       // 数据区起点（构造时 Allocate，GetDistance 锚点，100% 确定）
    private protected LogicalAddress _tailAddress;     // 写游标（单调递增，照 FASTER TailPageOffset 语义）
    private long _dataCapacity;                        // 已 Allocate 的数据区容量（不够时 EnsureSpace 扩展）

    /// <summary>
    /// ★ 热路径距离快路径：算 addr 距 _dataStart 的字节数。
    /// <para>★ 同段（Ring 单段是常态——PageCount×PageSize 通常 &lt; SegmentSize）时 = 纯 long 减法，
    ///   零引擎调用（对照 Log 热路径零 GetDistance 范式）。跨段才 fallback 到引擎 GetDistance（正确性不降级）。</para>
    /// <para>★ _dataStart.SegId 是 readonly（LogicalAddress 值类型快照），构造后稳定，可安全缓存判断。</para>
    /// <para>★ 同段时等价于 GetDistance 的真实结果（LogicalAddressRegistry.GetDistance 同段分支 = end.Offset - start.Offset）。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long DistanceFromDataStart(LogicalAddress addr)
        => addr.SegId == _dataStart.SegId
            ? addr.Offset - _dataStart.Offset
            : _engine.GetDistance(_dataStart, addr);

    /// <summary>
    /// ★ 热路径：分配 numSlots 字节，返回 LogicalAddress（100% 确定）。
    /// <para>照 FASTER TryAllocate：原子推进 tail，跨页时分配新内存槽。</para>
    /// <para>★ Empty = RETRY（环形满——head 尚未推进淘汰，等 flush/evict）。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogicalAddress TryAllocate(int numSlots)
    {
        if (numSlots > PageSize)
            throw new InvalidOperationException($"Entry does not fit on page (numSlots={numSlots} > PageSize={PageSize})");

        lock (_tailLock)
        {
            // ★ 地址空间不够时 Allocate 扩展（向前，地址单调增长不回退——FASTER hybrid log）
            EnsureSpace(numSlots);
            // 当前 tail 在数据区的偏移（合法 long 算术；★ 同段快路径，零引擎调用）
            long tailDist = DistanceFromDataStart(_tailAddress);
            long intraPage = tailDist & PageSizeMask;
            long newIntra = intraPage + numSlots;

            // 跨页：当前页放不下 → 推进到下一页边界
            if (newIntra > PageSize)
            {
                long advanceToNextPage = intraPage == 0 ? 0 : (PageSize - intraPage);
                var nextPageAddr = _engine.CalculationAddress(_tailAddress, advanceToNextPage);
                PageAlignedShiftReadOnlyAddress(nextPageAddr);
                PageAlignedShiftHeadAddress(nextPageAddr);

                if (NeedToWait(nextPageAddr))
                    return LogicalAddress.Empty;

                long nextPageSeq = (tailDist + advanceToNextPage) >> PageSizeBits;
                EnsurePageAllocated(nextPageSeq);
                EnsurePageAllocated(nextPageSeq + 1);

                _tailAddress = _engine.CalculationAddress(nextPageAddr, numSlots);
                return nextPageAddr;
            }

            // 正常路径：当前页内分配
            var recordAddr = _tailAddress;
            long curPageSeq = tailDist >> PageSizeBits;
            EnsurePageAllocated(curPageSeq);
            _tailAddress = _engine.CalculationAddress(_tailAddress, numSlots);
            return recordAddr;
        }
    }

    /// <summary>★ 地址空间不够时 Allocate 扩展（向前，地址单调增长不回退）。</summary>
    /// <remarks>FASTER hybrid log：地址永远向前，写满已 Allocate 区间后 Allocate 新区间继续。
    /// 不 ReclaimTail（那是 Log 变长页回退用的，Ring 固定页池不需要）。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSpace(long needed)
    {
        // 首次：Allocate 第一块，确定 _dataStart（GetDistance 锚点）
        if (_dataCapacity == 0)
        {
            long initial = Math.Max((long)PageCount * PageSize, needed);
            _dataStart = _engine.Allocate(initial).Start;
            _tailAddress = _dataStart;
            _dataCapacity = initial;
            return;
        }
        long used = DistanceFromDataStart(_tailAddress);   // ★ 同段快路径
        if (used + needed > _dataCapacity)
        {
            // 扩展：Allocate 新区间（_dataStart 不变——地址空间连续，引擎段表自动增长）
            long extend = Math.Max(needed, (long)PageCount * PageSize);
            _engine.Allocate(extend);
            _dataCapacity += extend;
        }
    }

    /// <summary>★ 便捷封装：内部 spin 重试 TryAllocate。</summary>
    private LogicalAddress Allocate(int numSlots)
    {
        LogicalAddress addr;
        while ((addr = TryAllocate(numSlots)) == LogicalAddress.Empty)
        {
            // ★ 环形满：flush readonly 区（不碰 mutable 区——WriteRecord 不自动 flush 用户数据），
            //   推进 FlushedUntilAddress 让 ShiftHead 能驱逐旧页腾 slot。
            var ro = ReadOnlyAddress;
            var flushed = FlushedUntilAddress;
            if (ro > flushed)
                FlushUntil(ro);
            else
                Thread.Yield();
        }
        return addr;
    }

    /// <summary>★ 封装 record：header flags 标记 VALID + SEALED。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Seal(LogicalAddress logicalAddress, int entrySize)
    {
        var headerSpan = GetSpan(logicalAddress, RingCodec.HeaderSize);
        RingCodec.OrFlags(headerSpan, RecordFlags.FLAG_RINGRECORD_VALID | RecordFlags.FLAG_RINGRECORD_SEALED);
    }

    // === 环形满背压（Ring 自管水位，照 FASTER NeedToWait）===
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool NeedToWait(LogicalAddress nextPageAddr)
    {
        // 环形满：tail 距 head 超过 PageCount 页（tail 追上 head 一圈）→ 需等驱逐
        // ★ head 是已驱逐边界，tail 不能超过 head + PageCount 页（否则覆盖未淘汰的活页）
        long dist = _engine.GetDistance(HeadAddress, nextPageAddr);
        return dist >= (long)PageCount * PageSize;
    }

    /// <summary>
    /// ★ 热路径：逻辑地址 → 物理 native 指针（照 FASTER GetPhysicalAddress）。
    /// <para>pageSeq = GetDistance(_dataStart, addr) / PageSize（合法 long 算术）</para>
    /// <para>slot = pageSeq &amp; PageCountMask（环形槽，内存数组下标）</para>
    /// <para>pageIntra = GetDistance(_dataStart, addr) % PageSize</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetPhysicalAddress(LogicalAddress logicalAddress)
    {
        var dist = DistanceFromDataStart(logicalAddress);   // ★ 同段快路径（零引擎调用），跨段 fallback
        var pageSeq = dist >> PageSizeBits;
        var slot = (int)(pageSeq & PageCountMask);
        var pageIntra = (int)(dist & PageSizeMask);
        return _nativePagePointers[slot] + pageIntra;
    }

    /// <summary>★ Span（带边界，不暴露 byte*）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe Span<byte> GetSpan(LogicalAddress logicalAddress, int length)
    {
        long phys = GetPhysicalAddress(logicalAddress);
        return new Span<byte>((void*)phys, length);
    }

    /// <summary>★ 读 record header 字段。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingRecordFields GetFields(LogicalAddress logicalAddress)
    {
        var headerSpan = GetSpan(logicalAddress, RingCodec.HeaderSize);
        RingCodec.TryReadHeader(headerSpan, out var fields);
        return fields;
    }

    // === record 字节几何插槽（abstract，实现类 override；sealed → JIT 去虚化）===
    protected internal abstract int FixedRecordSize { get; }
    protected internal abstract int AverageRecordSize { get; }
    protected internal abstract (int filled, int allocated) GetRecordSize(long phys);
    protected internal abstract int GetRequiredRecordSize(long phys, int availableBytes);
}
