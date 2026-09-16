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
    /// <summary>★ flush 串行门（STORAGE-087 修订——并发 flush 互斥）：FlushUntilCore* 的
    /// 「读水位快照 → 迭代刷页 → 推进水位」三步必须原子，否则两个并发 flush 的区间重叠
    /// （后者的推进触发关页链 Shift→OnPagesClosed→FreePage，可把前者迭代中的页置 null——
    /// 组提交并发 Committed 写在慢盘 DIO 下实锤，KvGroupCommitDiskProbe pve-ci 三连复现）。
    /// 串行化后：Y 的快照必读 X 推进后的水位 → Y 区间起点 ≥ X 的已刷终点；关页链可释放的页
    /// 终点不超过 pageAligned(Y 快照)，即 Y 区间起点——迭代期间其页不可能被并发 FreePage。
    /// ★ 门是叶锁：持门体内不获取 _tailLock/_ongoingCloseLock 之外的环内锁，且不触发
    /// 新 flush（无重入）——内部写路径（持 _tailLock+epoch 的 Append 页满 flush）在门上短暂
    /// 等待在途产品 flush 属预期排队，无死锁环。</summary>
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    // ═══ 在途 flush 登记（STORAGE-087 修订第二半——flush×关页互斥）═══
    //
    // 写路径回绕触发的关页 worker（OnPagesClosedWorker→FreePage）不经过 _flushGate：
    // 仅靠门串行化挡不住「写回绕关页 vs 在途 flush 迭代」的交错（16 页小环压测 1 秒实锤）。
    // flush 迭代前在 _ongoingCloseLock 内登记区间（worker 全程持同一把锁——无 TOCTOU），
    // worker 走到与在途区间相交的页即停止本轮回收（deferred），由下一次关页触发续走；
    // 被推迟的页保持驻留，回收只延迟、不缺席（水位单调，后续 shift 必然重触发）。
    private long _flushInFlightFromDist = -1;
    private long _flushInFlightUntilDist = -1;

    /// <summary>登记在途 flush 区间（须在 _ongoingCloseLock 内——与关页 worker 的 FreePage
    /// 判定互斥，消灭登记/回收 TOCTOU）。</summary>
    private void FlushInFlightBegin(long fromDist, long untilDist)
    {
        lock (_ongoingCloseLock)
        {
            Volatile.Write(ref _flushInFlightFromDist, fromDist);
            Volatile.Write(ref _flushInFlightUntilDist, untilDist);
        }
    }

    /// <summary>注销在途 flush 区间（迭代结束/异常均须调）。</summary>
    private void FlushInFlightEnd()
    {
        Volatile.Write(ref _flushInFlightFromDist, -1);
        Volatile.Write(ref _flushInFlightUntilDist, -1);
    }

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
    /// 仍需保护的是"裸调用方未持 epoch 即 flush"——产品层（KvSession 组提交）的并发裸调用已由
    /// <see cref="_flushGate"/> 串行化收口（STORAGE-087 修订：串行化后迭代期间关页链可释放的页
    /// 终点 ≤ pageAligned(本 flush 水位快照) ≤ 区间起点，无交错窗口），invariant 本体保持 fail-fast。</remarks>
    private protected void AsyncFlushPages(LogicalAddress from, LogicalAddress until, bool durableFlush = true)
    {
        // ★ 增量复写（预分配+稳定复写形态）：只写 [from, until) 的增量字节——地址是预分配
        //   空间内的物理事实，按址复写增量即可持久化（整页重拷会把已写穿前缀与水位后数据
        //   一并重复传输，高频增量 flush 下 IO 放大到页粒度）。
        long fromDist = _engine.GetDistance(_dataStart, from);
        long untilDist = _engine.GetDistance(_dataStart, until);
        FlushInFlightBegin(fromDist, untilDist);
        try
        {
            for (long dist = fromDist; dist < untilDist; )
            {
                long pageIntra = dist & PageSizeMask;
                long pageBaseDist = pageIntra == 0 ? dist : (dist - pageIntra);   // 页起点在数据区的偏移
                long pageSeq = pageBaseDist >> PageSizeBits;
                int slot = (int)(pageSeq & PageCountMask);
                if (_pages[slot] is { } page)
                {
                    long startInPage = dist - pageBaseDist;   // 首页=from 的页内偏移；后续整页
                    long endInPage = Math.Min(untilDist, pageBaseDist + PageSize) - pageBaseDist;
                    if (endInPage > startInPage)
                    {
                        var pageAddr = _engine.CalculationAddress(_dataStart, pageBaseDist + startInPage);
                        _engine.Write(pageAddr, page.GetSpan((int)startInPage, (int)(endInPage - startInPage)));
                    }
                }
                else
                {
                    // ★ STORAGE-087 修订：null 的唯一合法来源 = 本 flush 的水位快照陈旧——快照之后
                    //   另一条 flush 完成链（推进水位→关页 worker）已把该页写穿落盘并回收，本区间
                    //   已被覆盖（页尾全覆盖判定下 FreePage 仅作用于 ≤ FlushedUntilAddress 的页且
                    //   水位单调，页数据已在设备）→ 跳过即为正确；null 且页尾越过当前已刷水位 =
                    //   真不变量破坏，保持 fail-fast（静默跳过未落盘页=丢数据）。
                    var pageEndDist = pageBaseDist + PageSize;
                    if (pageEndDist > _engine.GetDistance(_dataStart, FlushedUntilAddress))
                        throw new InvalidOperationException(
                            $"AsyncFlushPages: page slot {slot} unallocated in flush range [{from}, {until}) — " +
                            "flush range pages must stay resident (STORAGE-009/087 invariant)");
                }
                dist = pageBaseDist + PageSize;
            }
        }
        finally
        {
            FlushInFlightEnd();
        }
        if (durableFlush)
        {
            _engine.Flush();
            _overflowEngine?.Flush();
        }
    }

    private protected async ValueTask AsyncFlushPagesAsync(LogicalAddress from, LogicalAddress until, CancellationToken ct, bool durableFlush = true)
    {
        // ★ 增量复写（语义同同步版注释）
        long fromDist = _engine.GetDistance(_dataStart, from);
        long untilDist = _engine.GetDistance(_dataStart, until);
        FlushInFlightBegin(fromDist, untilDist);
        try
        {
            for (long dist = fromDist; dist < untilDist; )
            {
                long pageIntra = dist & PageSizeMask;
                long pageBaseDist = pageIntra == 0 ? dist : (dist - pageIntra);
                long pageSeq = pageBaseDist >> PageSizeBits;
                int slot = (int)(pageSeq & PageCountMask);
                if (_pages[slot] is { } page)
                {
                    long startInPage = dist - pageBaseDist;
                    long endInPage = Math.Min(untilDist, pageBaseDist + PageSize) - pageBaseDist;
                    if (endInPage > startInPage)
                    {
                        var pageAddr = _engine.CalculationAddress(_dataStart, pageBaseDist + startInPage);
                        await _engine.WriteAsync(pageAddr, page.Memory.Slice((int)startInPage, (int)(endInPage - startInPage)), ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    // ★ STORAGE-087 修订：语义同同步版——null 且页尾 ≤ 当前已刷水位 = 快照陈旧
                    //   （区间已被并发 flush 覆盖落盘）→ 跳过；null 且页尾 > 已刷水位 = 真违约 → fail-fast。
                    //   fail-fast 消息携带当场水位快照（seg#0 dist 域十六进制）供竞态取证。
                    var pageEndDist = pageBaseDist + PageSize;
                    var flushedDistNow = _engine.GetDistance(_dataStart, FlushedUntilAddress);
                    if (pageEndDist > flushedDistNow)
                        throw new InvalidOperationException(
                            $"AsyncFlushPagesAsync: page slot {slot} unallocated in flush range [{from}, {until}) — " +
                            "flush range pages must stay resident (STORAGE-009/087 invariant) " +
                            $"[diag slot={slot} pageSeq={pageSeq} pageEnd=0x{pageEndDist:X} flushed=0x{flushedDistNow:X} " +
                            $"safeHead=0x{_engine.GetDistance(_dataStart, SafeHeadAddress):X} " +
                            $"closed=0x{_engine.GetDistance(_dataStart, ClosedUntilAddress):X} " +
                            $"head=0x{_engine.GetDistance(_dataStart, HeadAddress):X} " +
                            $"inFlightFrom=0x{Volatile.Read(ref _flushInFlightFromDist):X}]");
                }
                dist = pageBaseDist + PageSize;
            }
        }
        finally
        {
            FlushInFlightEnd();
        }
        if (durableFlush)
        {
            _engine.Flush();
            _overflowEngine?.Flush();
        }
    }

    /// <summary>★ flush 到写尾快照（#197/#207）：tail 在 _tailLock 内原子读取——读与用之间被并发
    /// 推进的尾不混入 flush 边界，合约精确为「flush 到快照时点的全部数据」。快照之后的并发写入
    /// 由后续 flush 覆盖（flush 水位 CAS128 单调，见 #163 基座）。</summary>
    public void FlushUntilTail()
    {
        EnsureNotDisposing();   // ★ #258：Dispose 竞态 fail-fast（Dispose 自身走 FlushUntilCore 免门）
        LogicalAddress tail;
        lock (_tailLock) tail = _tailAddress;
        FlushUntilCore(tail);
    }

    /// <summary>★ flush 到写尾快照（异步版，语义同 <see cref="FlushUntilTail"/>）。</summary>
    /// <param name="ct">取消令牌，可用于取消异步操作。默认 <c>default</c>。</param>
    /// <returns>完成后已把快照时点写尾之前的全部数据落盘（FlushedUntilAddress 推进到该尾）。</returns>
    public async ValueTask FlushUntilTailAsync(CancellationToken ct = default)
    {
        EnsureNotDisposing();
        LogicalAddress tail;
        lock (_tailLock) tail = _tailAddress;
        await FlushUntilCoreAsync(tail, ct).ConfigureAwait(false);
    }

    /// <summary>★ #258：Dispose 竞态门——门翻开后（Dispose 落盘序列启动）外部 flush fail-fast；
    /// Dispose 自身的落盘走 FlushUntilCore 免门（否则门禁抛异常被尽力落盘的 catch 吞掉 = 丢数据）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureNotDisposing()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeGate) != 0, GetType().Name);
    }

    /// <summary>★ 同步 flush 到指定地址（Prepare 用——持久化契约面：写穿 + flush）。</summary>
    /// <param name="untilAddress">flush 目标地址（flush 区间 [FlushedUntilAddress, untilAddress)）。</param>
    public void FlushUntil(LogicalAddress untilAddress)
    {
        EnsureNotDisposing();   // ★ #258：Dispose 竞态 fail-fast
        FlushUntilCore(untilAddress);
    }

    private void FlushUntilCore(LogicalAddress untilAddress)
    {
        _flushGate.Wait();
        try
        {
            var currentFlushed = FlushedUntilAddress;
            if (untilAddress > currentFlushed)
            {
                AsyncFlushPages(currentFlushed, untilAddress);
                InvalidateColdPageCache(currentFlushed, untilAddress);
                ShiftFlushedUntilAddress(untilAddress);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>★ 容量写穿到指定地址（驱逐链专用——页复用前数据已在设备，<b>零 fsync</b>）。
    /// <para>≙ FASTER on-disk region 推进语义：FlushedUntil = 「已写穿（含 OS page cache）」——
    /// 进程崩溃安全（设备可读），断电窗口由上层 checkpoint/Committed 档承担。与
    /// <see cref="FlushUntil"/> 的差异仅在是否调引擎 Flush（非 WT 介质上=是否 fsync）。</para>
    /// </summary>
    /// <param name="untilAddress">写穿目标地址（区间 [FlushedUntilAddress, untilAddress)）。</param>
    public void WriteThroughUntil(LogicalAddress untilAddress)
    {
        _flushGate.Wait();
        try
        {
            var currentFlushed = FlushedUntilAddress;
            if (untilAddress > currentFlushed)
            {
                AsyncFlushPages(currentFlushed, untilAddress, durableFlush: false);
                InvalidateColdPageCache(currentFlushed, untilAddress);
                ShiftFlushedUntilAddress(untilAddress);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>★ 异步 flush 到指定地址（PrepareAsync 用）。</summary>
    /// <param name="untilAddress">flush 目标地址（flush 区间 [FlushedUntilAddress, untilAddress)）。</param>
    /// <param name="ct">取消令牌，可用于取消异步操作。默认 <c>default</c>。</param>
    /// <returns>完成后区间数据已写回引擎且 FlushedUntilAddress 已单调推进到 untilAddress。</returns>
    public async ValueTask FlushUntilAsync(LogicalAddress untilAddress, CancellationToken ct = default)
    {
        EnsureNotDisposing();   // ★ #258：Dispose 竞态 fail-fast
        await FlushUntilCoreAsync(untilAddress, ct).ConfigureAwait(false);
    }

    private async ValueTask FlushUntilCoreAsync(LogicalAddress untilAddress, CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var currentFlushed = FlushedUntilAddress;
            if (untilAddress > currentFlushed)
            {
                await AsyncFlushPagesAsync(currentFlushed, untilAddress, ct).ConfigureAwait(false);
                InvalidateColdPageCache(currentFlushed, untilAddress);
                ShiftFlushedUntilAddress(untilAddress);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>
    /// ★ 冷页缓存失效（flush 路径必调——W6 Ring flush×冷读互溶缺陷根因修复）：
    /// 页 LogicalAddress 恒定而内容随追加演进，flush 落盘后缓存快照即陈旧——不失效则同页
    /// 后续冷读（LoadColdPage 缓存命中）永久供陈旧快照（resolver 回读 key 不匹配 → Find=Empty /
    /// GetKeyAsync 供错记录）。按被刷页范围逐页 Remove，命中后冷读重新回源引擎（此时引擎副本
    /// 已含本次 flush 的正确内容）。
    /// </summary>
    private void InvalidateColdPageCache(LogicalAddress from, LogicalAddress until)
    {
        if (_coldPageCache is null) return;
        var fromDist = _engine.GetDistance(_dataStart, from);
        var untilDist = _engine.GetDistance(_dataStart, until);
        for (var pageSeq = fromDist >> PageSizeBits; pageSeq <= untilDist >> PageSizeBits; pageSeq++)
            _coldPageCache.Remove(_engine.CalculationAddress(_dataStart, pageSeq << PageSizeBits));
    }
}
