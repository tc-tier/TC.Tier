namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase DisposeOverride——落盘 mutable 区 → 释放引擎以外的原生资源。
/// <para>★★ 落盘保障（存储系统底线）：</para>
/// <para>Dispose 必须先把 mutable 区 [FlushedUntilAddress, TailAddress) 的数据落盘（FlushUntilTail 原子快照），
/// 然后才能释放页 native 内存——否则 mutable 区数据永久丢失。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// 同步额外清理钩子（LifecycleBase 模板）：先落盘 mutable 区 [FlushedUntilAddress, TailAddress)
    /// → 写 meta 落盘 → 释放 meta 策略 → 释放页 native 内存/页池（引擎内存归引擎，引擎以外资源在此收）。
    /// 各步异常吞掉互不阻断（存储底线：尽力落盘）。
    /// </summary>
    /// <summary>★ Dispose 排他门（#258）：双 Dispose / Dispose 与落盘序列重入互斥（CAS 0→1）。
    /// <para>与并发读写的协议边界：读写在生命周期翻转后经 EnsureReady fail-fast（Write/Flush 入口
    /// 已接入）；落盘窗口内的极窄竞态由模板排序兜底（flush 先于页释放）。</para></summary>
    private int _disposeGate;

    /// <summary>同步释放钩子（LifecycleBase 模板）：排他门幂等（重入/双 Dispose 直接返回）→
    /// 直走 Core 落盘 mutable 区 [FlushedUntilAddress, TailAddress)（tail 原子快照——尽力落盘，
    /// 异常吞掉互不阻断）→ 写 meta → 释放 meta 策略 → 逐页释放 native 内存与页池。</summary>
    /// <param name="disposing">true = 用户调 Dispose（可触托管资源）。</param>
    protected override void DisposeOverride(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposeGate, 1) == 1) return;   // #258：重入/双 Dispose 排他

        // ★ #197/#207：tail 原子快照；直走 Core（免 Dispose 门——门禁抛异常会被尽力落盘的 catch 吞掉 = 丢数据）
        LogicalAddress disposeTail;
        lock (_tailLock) disposeTail = _tailAddress;
        try { FlushUntilCore(disposeTail); }
        catch { }

        try { WriteMeta(); }
        catch { }
        try { MetaPolicy.Dispose(); }
        catch { }

        if (_pages is not null)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i]?.Dispose();
                _pages[i] = null;
            }
        }
        _coldPageCache?.Dispose();
        _evictionQuarantine?.Dispose();   // 先排空隔离页（溢出回调回落 freePageCache）
        _freePageCache?.Dispose();
        _pagePool?.Dispose();
    }

    /// <summary>
    /// 异步额外清理钩子（LifecycleBase 模板）：对等 <see cref="DisposeOverride"/> 的异步轨——
    /// 异步落盘 mutable 区 → 异步写 meta → 异步释放 meta 策略 → 释放页 native 内存/页池。
    /// 各步异常吞掉互不阻断（存储底线：尽力落盘）。
    /// </summary>
    /// <param name="disposing">true = 用户调 DisposeAsync（可触托管资源）。</param>
    /// <returns>释放完成的 ValueTask。</returns>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        try { await FlushUntilAsync(TailAddress).ConfigureAwait(false); }
        catch { }

        try { await WriteMetaAsync().ConfigureAwait(false); }
        catch { }
        try
        {
            await MetaPolicy.DisposeAsync().ConfigureAwait(false);
        }
        catch { }

        if (_pages is not null)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i]?.Dispose();
                _pages[i] = null;
            }
        }
        _coldPageCache?.Dispose();
        _evictionQuarantine?.Dispose();   // 先排空隔离页（溢出回调回落 freePageCache）
        _freePageCache?.Dispose();
        _pagePool?.Dispose();

        await ValueTask.CompletedTask;
    }
}
