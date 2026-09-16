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

    // === 安全快照尾（读分档统一判据——页池内完整数据上界）===
    // ★ 语义：所有 < _safeSnapshotTail 的槽保证 header/payload/CRC 完整可见。单条写在 _tailLock 内完成
    //   「分配 + header/payload/CRC 写入」（分配原子化——TailAddress 是分配尾，快照前可能有
    //   在途槽被扫描跳过并越过 ⇒ 写者后续换绑落在被丢弃的旧索引即永久丢失，压强回归实锤），
    //   写完即推进本水位（锁内顺序=分配序，单调无洞）；Batch Append 写完同样推进（Batch.cs Append）。
    // ★ 一水位两用（read-protection-tiering v2）：扫描游标构造快照 + 读路径热冷分档判据
    //   （addr < 水位 → 页池直读；≥ → 设备读/自愈回退）。
    private LogicalAddress _safeSnapshotTail;

    // ★ 读侧无锁判据水位（距 _dataStart 字节数）：16B LogicalAddress 无法原子读（Volatile/Interlocked
    //   不支持，撕裂读会产出垃圾判据）——8B 距离单调且与 16B 水位同点推进，经 Interlocked CAS-max
    //   发布（写完记录后全栅栏）+ Volatile.Read 消费（一次读+比较 ~1ns，热路径无锁无撕裂）。
    private long _safeSnapshotTailDist;

    // ★ head 守卫水位（同 8B 范式）：addr < head ⇒ 其页已被回收/排队回收——页池内容可能是复用重写的
    //   别记录（同形记录同偏移复用时内容校验全通过——假数据），必须判冷走设备。head 推进在
    //   flush 封顶后立即发生（ShiftHeadAddress），先于任何 FreePage（drain）——以 head 为守卫，
    //   「检查通过 → 读到已释放页」的窗口（ns）远小于 head 推进到释放的路径（drain+worker，µs 级）。
    private long _headDist;

    /// <summary>扫描安全快照尾（lock 内采集——之前全部 header 完整可见）。热切换重放/追平推进判据。</summary>
    /// <returns>采集时刻的安全快照尾——该地址之前所有槽的 header/payload/CRC 已完整可见。</returns>
    public LogicalAddress TakeSafeSnapshotTail()
    {
        lock (_tailLock)
            return _safeSnapshotTail;
    }

    /// <summary>
    /// ★ 推进安全快照尾（单条写锁内 / Batch Append 写完调用）——16B 原字段与 8B 读侧判据水位同点推进。
    /// 8B 侧 CAS-max：Batch 多写者并发推进无撕裂、单调不回退。
    /// </summary>
    private void AdvanceSafeSnapshotTail(LogicalAddress addrEnd)
    {
        if (_safeSnapshotTail < addrEnd) _safeSnapshotTail = addrEnd;

        long dist = DistanceFromDataStart(addrEnd);
        long cur = Volatile.Read(ref _safeSnapshotTailDist);
        while (dist > cur)
        {
            long prev = Interlocked.CompareExchange(ref _safeSnapshotTailDist, dist, cur);
            if (prev == cur) break;
            cur = prev;
        }
    }

    /// <summary>★ head 守卫水位推进（ShiftHeadAddress 在 head 推进成功后调用）——CAS-max 单调无撕裂。</summary>
    private void AdvanceHeadDist(LogicalAddress newHead)
    {
        long dist = DistanceFromDataStart(newHead);
        long cur = Volatile.Read(ref _headDist);
        while (dist > cur)
        {
            long prev = Interlocked.CompareExchange(ref _headDist, dist, cur);
            if (prev == cur) break;
            cur = prev;
        }
    }

    /// <summary>
    /// ★ 读分档判据（read-protection-tiering v2 §3.2 + head 守卫补强）：
    /// head ≤ addr &lt; SafeSnapshotTail → 页池直读（页未回收且写者写完 ⇒ 页池必有完整记录）；
    /// addr &lt; head → 页已回收（复用别名风险）——强制设备读；addr ≥ SafeSnapshotTail → 页池不可信——设备读。
    /// </summary>
    /// <param name="distFromDataStart">addr 距 _dataStart 字节数（<see cref="DistanceFromDataStart"/>，调用方已算则复用）。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsHotRecord(long distFromDataStart)
        => distFromDataStart >= Volatile.Read(ref _headDist)
        && distFromDataStart < Volatile.Read(ref _safeSnapshotTailDist);

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
    /// <para>★ Invalid = RETRY（环形满——head 尚未推进淘汰，等 flush/evict）。
    ///   STORAGE-086：不可用 Empty 当哨兵——Empty=(0,0) 是 seg0 起点<b>合法地址</b>
    ///   （首块分配落于此），调用方按 Empty 判重试会吞掉首个合法分配。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogicalAddress TryAllocate(int numSlots)
    {
        lock (_tailLock)
            return TryAllocateLocked(numSlots);
    }

    /// <summary>
    /// ★ 分配核心（<b>调用方须持 _tailLock</b>）——单条写路径与 header/payload 写入共用同一把锁
    ///   （分配原子化：TailAddress 之外的 <see cref="_safeSnapshotTail"/> 随 header 写完在锁内推进）。
    /// <para>★ Invalid = RETRY（环形满——head 尚未推进淘汰，等 flush/evict——调用方须<b>退锁</b>后
    ///   处理背压再重试，锁内禁止 flush/evict 的 IO 路径）。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogicalAddress TryAllocateLocked(int numSlots)
    {
        if (numSlots > PageSize)
            throw new InvalidOperationException($"Entry does not fit on page (numSlots={numSlots} > PageSize={PageSize})");

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
                return LogicalAddress.Invalid;

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

        // ★ STORAGE-086：Empty=(0,0) 保留为"无值"哨兵（索引 Find 未命中/链终止；
        //   测试契约：Write 返回地址不得为 Empty）——首块分配落于该地址时跳过本槽
        //   （旧实现靠 retry 路径顺带跳过，语义隐晦；现 retry=Invalid、Empty=保留槽显式化）。
        //   仅首块可命中（地址单调），递归深度 ≤1。
        if (recordAddr == LogicalAddress.Empty)
            return TryAllocateLocked(numSlots);

        return recordAddr;
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
            _safeSnapshotTail = _dataStart;   // 安全快照尾与分配尾同点初始化
            _safeSnapshotTailDist = 0;        // 读侧判据水位同点（距 _dataStart 零字节）
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

    /// <summary>★ 便捷封装：内部 spin 重试 TryAllocate（Invalid = 环形满，退锁后背压重试）。</summary>
    private LogicalAddress Allocate(int numSlots)
    {
        LogicalAddress addr;
        while (!(addr = TryAllocate(numSlots)).IsValid)
        {
            // ★ 环形满：写穿 readonly 区（不碰 mutable 区——WriteRecord 不自动写穿用户数据），
            //   推进 FlushedUntilAddress 让 ShiftHead 能驱逐旧页腾 slot。
            //   ★ 容量写穿（零 fsync）——页复用的容量语义（≙FASTER on-disk 推进）；断电窗口
            //   归上层 checkpoint/Committed 档，不在驱逐路径支付 fsync 成本。
            var ro = ReadOnlyAddress;
            var flushed = FlushedUntilAddress;
            if (ro > flushed)
                WriteThroughUntil(ro);
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
    /// <param name="logicalAddress">起始逻辑地址。</param>
    /// <param name="length">窗口长度（字节）。</param>
    /// <returns>指向页池 native 内存的可写窗口——调用方须保证该地址的页未被驱逐回收。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe Span<byte> GetSpan(LogicalAddress logicalAddress, int length)
    {
        long phys = GetPhysicalAddress(logicalAddress);
        return new Span<byte>((void*)phys, length);
    }

    /// <summary>
    /// ★ 公开字节距离（产品层组合计算用——队列积压/消费组滞后 lag 的度量原语）。
    /// <para>委托引擎 <c>GetDistance</c>（跨段进位正确，§8 铁律不碰 Offset 算术）；
    /// from ≤ to 语义（水位区间 [floor, tail) 的字节数）。</para>
    /// </summary>
    /// <param name="from">起点（含）。</param>
    /// <param name="to">终点（不含）。</param>
    /// <returns>区间字节数。</returns>
    public long GetByteDistance(LogicalAddress from, LogicalAddress to) => _engine.GetDistance(from, to);

    /// <summary>★ 读 record header 字段。</summary>
    /// <param name="logicalAddress">record 的逻辑地址。</param>
    /// <returns>解码出的 header 字段；header 不合法时为 default。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingRecordFields GetFields(LogicalAddress logicalAddress)
    {
        var headerSpan = GetSpan(logicalAddress, RingCodec.HeaderSize);
        RingCodec.TryReadHeader(headerSpan, out var fields);
        return fields;
    }

    // === record 字节几何插槽（abstract，实现类 override；sealed → JIT 去虚化）===
    /// <summary>固定记录大小（record 字节几何插槽）；0 = 变长（record 大小由 header 自述 PayloadLength/PaddingLength）。</summary>
    protected internal abstract int FixedRecordSize { get; }
    /// <summary>平均记录大小（溢出阈值/容量估算的锚点值）。</summary>
    protected internal abstract int AverageRecordSize { get; }
    /// <summary>读物理地址处 record 的实际几何：filled = header + payload + padding 实际字节数，allocated = 对齐后的槽长。</summary>
    /// <param name="phys">record 的物理地址。</param>
    /// <returns>(实际字节数, 对齐后占用槽长)。</returns>
    protected internal abstract (int filled, int allocated) GetRecordSize(long phys);
    /// <summary>给定可用字节数下覆盖该 record 所需的最小槽长（估算插槽，实现类按自身 record 形态给出）。</summary>
    /// <param name="phys">record 的物理地址。</param>
    /// <param name="availableBytes">可用字节数。</param>
    /// <returns>所需的最小槽长（字节数）。</returns>
    protected internal abstract int GetRequiredRecordSize(long phys, int availableBytes);
}
