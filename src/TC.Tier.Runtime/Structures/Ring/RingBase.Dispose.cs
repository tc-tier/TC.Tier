namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase DisposeOverride——落盘 mutable 区 → 释放引擎以外的原生资源。
/// <para>★★ 落盘保障（存储系统底线）：</para>
/// <para>Dispose 必须先把 mutable 区 [FlushedUntilAddress, TailAddress) 的数据落盘（FlushUntil(TailAddress)），
/// 然后才能释放页 native 内存——否则 mutable 区数据永久丢失。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// 同步额外清理钩子（LifecycleBase 模板）：先落盘 mutable 区 [FlushedUntilAddress, TailAddress)
    /// → 写 meta 落盘 → 释放 meta 策略 → 释放页 native 内存/页池（引擎内存归引擎，引擎以外资源在此收）。
    /// 各步异常吞掉互不阻断（存储底线：尽力落盘）。
    /// </summary>
    /// <param name="disposing">true = 用户调 Dispose（可触托管资源）。</param>
    protected override void DisposeOverride(bool disposing)
    {
        try { FlushUntil(TailAddress); }
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
        _pagePool?.Dispose();
        _freePageCache?.Dispose();
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
        _pagePool?.Dispose();
        _freePageCache?.Dispose();

        await ValueTask.CompletedTask;
    }
}
