namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// LogBase 截断 partial——头截断（TruncatePrefix，EntryLog retention 用）+ 尾截断（TruncateSuffix，EntryLog Raft leader 切换用）。
/// <para>★ 新模型：全部委托给引擎——引擎管段回收（ReclaimHead/ReclaimTail），Log 只做前置校验。</para>
/// </summary>
public abstract partial class LogBase
{
    /// <summary>
    /// ★ 头截断：推进引擎 MinAddress（=BeginAddress）到 address。
    /// <para>引擎 ReclaimHead 回收 [oldMinAddress, address) 之间的物理段。</para>
    /// <para>EntryLog retention（SizeBasedRetention）用——头截断回收旧数据。</para>
    /// </summary>
    /// <param name="address">新的头截断边界（此地址之前的数据已逻辑删除）。</param>
    public void TruncatePrefix(LogicalAddress address)
    {
        EnsureNotDisposed();
        if (address <= BeginAddress) return;
        _engine.ReclaimHead(address);
    }

    /// <summary>
    /// ★ 尾截断：回退引擎 AllocatedTail 到 address。
    /// <para>引擎 ReclaimTail 回收 [address, oldTail] 之间的段 + 回退 AllocatedTail。</para>
    /// <para>EntryLog Raft leader 切换用——截断未 commit 的 entry。</para>
    /// </summary>
    /// <param name="address">新的尾截断边界（截断此地址之后的未 commit entry）。</param>
    /// <returns>true = 截断成功；false = 地址无效。</returns>
    public bool TruncateSuffix(LogicalAddress address)
    {
        EnsureNotDisposed();
        if (address < BeginAddress) return false;
        if (address > TailAddress) return false;
        lock (_writeLock)
        {
#pragma warning disable TCSG031 // 设计必需：同步截断 API 契约——返回前数据必须已落盘
            if (_inFlightFlush is { } task) { task.GetAwaiter().GetResult(); _inFlightFlush = null; }
#pragma warning restore TCSG031
        _engine.ReclaimTail(address);
        _pageA?.GetSpan(0, PageSize).Clear();
        _pageB?.GetSpan(0, PageSize).Clear();
        _pageUsedA = 0;
        _pageUsedB = 0;
        _activePage = 0;
        _spaceStart = LogicalAddress.Empty;
        _spaceWriteOffset = 0;
        _spaceCapacity = 0;
        _spaceAllocated = false;
            _logicalTail = address;
        }

        // ★ 截断完成钩子——EntryLog override 夹 CommittedOffset（截断后 commit 边界不得越过物理尾，
        //   否则重放越界跳段报 Segment not found——2PC Abort 路径 OnAborted 同款语义）。
        OnTailTruncated(address);

        return true;
    }

    /// <summary>尾截断完成回调（默认空——EntryLog 夹 CommittedOffset 用）。</summary>
    protected virtual void OnTailTruncated(LogicalAddress rollbackTail) { }
}
