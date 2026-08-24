namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 截断 partial——头截断（TruncatePrefix，快照后回收旧前缀用）。
/// <para>★ 新模型：全部委托引擎 ReclaimHead（照 LogBase.Truncate.cs:22）。</para>
/// <para>★ 截断是快照的必要前提——快照后截断旧前缀释放空间。</para>
/// <para>★ 前置：截断点必须 &lt;= FlushedUntilAddress（已落盘），否则丢未落盘数据。</para>
/// <para>水位用 Utility.MonotonicUpdate 单调推进（不回退）。</para>
/// <para>参见 base.md §2.9。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// ★ 头截断：推进 BeginAddress 到 address + 可选物理段回收（engine.ReclaimHead）。
    /// </summary>
    public void TruncatePrefix(LogicalAddress address, bool truncateDevice = false)
    {
        EnsureReady();
        EnsureNotDisposed();

        // 单调不回退
        if (address < BeginAddress) return;

        // 前置检查：截断区域必须已落盘
        if (address > FlushedUntilAddress)
            throw new InvalidOperationException(
                $"TruncatePrefix({address}) beyond FlushedUntilAddress({FlushedUntilAddress}) — 未落盘数据会丢失");

        // CAS 单调推进 BeginAddress
        if (!MonotonicUpdateAddr(ref _beginAddress, address, out _)) return;

        // 引擎物理段回收（可选——逻辑截断不受物理失败影响）
        if (truncateDevice)
        {
            try { _engine.ReclaimHead(address); }
            catch { /* 物理截断失败不阻断逻辑截断 */ }
        }
    }

    /// <summary>
    /// ★ 尾截断（D2 决策——放松"地址单调不回退"铁律的<b>唯一异常路径</b>）：回退尾水位到 address，
    /// 物理销毁 [address, TailAddress) 区间（引擎 ReclaimTail），回退内存水位/页池/冷缓存。
    /// <para>调用方：Abort（2PC 回滚悬干数据）。独立公开面供 Raft-leader 式显式回退场景使用。</para>
    /// <para>★ 回退四件套（顺序敏感）：</para>
    /// <para>1. 物理回收：engine.ReclaimTail（整段删除 + 段内打洞，字节级销毁不可恢复）；</para>
    /// <para>2. 内存水位条件回退：Flushed/ReadOnly/SafeReadOnly/Tail 各自独立判断
    ///     current &gt; target 才回退（盲赋会把低于目标的字段前推——多字段回退铁律）；</para>
    /// <para>3. 页池清零：[address, 旧尾] 的 native 页字节清零（含半页起点 + 整页）——
    ///     防回退区陈旧字节被后续热路径读成"有效 record"；</para>
    /// <para>4. 冷缓存失效：回退区内页的缓存条目逐页 Remove——防冷读路径供旧字节。</para>
    /// <para>⚠️ 守卫：address 不得落入已驱逐区（&lt; SafeHeadAddress）——已驱逐页 native 内存已归还，
    ///     回退进该区意味着事务窗口横跨大规模非事务流量（协议违例），fail-fast 不静默损坏水位格。</para>
    /// <para>⚠️ 调用契约：与并发 Write 串行（事务终态点调用；TransactionLog 协议天然满足）。
    ///     Head/SafeHead 不回退（已驱逐物理页不可复活）；若 Head 暂超前新尾，下次页环绕自愈
    ///     （读路由按 FlushedUntil，Head 只影响背压与驱逐）。</para>
    /// </summary>
    /// <param name="address">新的尾边界（[address, TailAddress) 被销毁）。</param>
    /// <exception cref="InvalidOperationException">地址越界 / 落入已驱逐区。</exception>
    public void TruncateSuffix(LogicalAddress address)
    {
        EnsureReady();
        EnsureNotDisposed();

        lock (_tailLock)
        {
            var oldTail = _tailAddress;
            if (address < _dataStart || address > oldTail)
                throw new InvalidOperationException(
                    $"TruncateSuffix({address}) 越界 [dataStart={_dataStart}, tail={oldTail})——尾截断目标必须在数据区当前尾之内");
            if (address < _safeHeadAddress)
                throw new InvalidOperationException(
                    $"TruncateSuffix({address}) 落入已驱逐区（SafeHeadAddress={_safeHeadAddress}）——事务窗口横跨已驱逐页，" +
                    "无安全回退边界（协议违例：Abort 前置窗口内不应发生大规模驱逐）");

            long oldTailDist = DistanceFromDataStart(oldTail);
            long targetDist = DistanceFromDataStart(address);

            // 1) 物理销毁 [address, 引擎 AllocatedTail)——含 Allocate 预留空洞一并退回
            _engine.ReclaimTail(address);

            // 2) 内存水位条件回退（各字段独立判断，绝不盲赋）
            if (_flushedUntilAddress > address) _flushedUntilAddress = address;
            if (_readOnlyAddress > address) _readOnlyAddress = address;
            if (_safeReadOnlyAddress > address) _safeReadOnlyAddress = address;
            _tailAddress = address;
            _dataCapacity = targetDist;   // 引擎容量已随 ReclaimTail 退回——簿记同步，EnsureSpace 按需重扩

            // 3) 页池清零 [address, oldTail]（半页起点 + 整页 + lookahead 预建页）
            long intra = targetDist & PageSizeMask;
            long pageSeq = targetDist >> PageSizeBits;
            if (intra > 0)
            {
                ClearPage((int)(pageSeq & PageCountMask), (int)intra);
                pageSeq++;
            }
            long lastPageSeq = (oldTailDist >> PageSizeBits) + 1;   // +1：覆盖 TryAllocate 的 lookahead 预建页
            for (; pageSeq <= lastPageSeq; pageSeq++)
                ClearPage((int)(pageSeq & PageCountMask));

            // 4) 冷缓存失效——回退区（含半页所在页）逐页 Remove，防冷读供旧字节
            if (_coldPageCache is { } cache)
            {
                long firstPageBase = (targetDist - intra) & ~ (long)PageSizeMask;
                for (long d = firstPageBase; d <= oldTailDist; d += PageSize)
                    cache.Remove(_engine.CalculationAddress(_dataStart, d));
            }
        }
    }
}
