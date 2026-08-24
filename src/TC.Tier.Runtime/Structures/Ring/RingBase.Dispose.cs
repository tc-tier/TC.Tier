namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase DisposeOverride——落盘 mutable 区 → 释放引擎以外的原生资源。
/// <para>★★ 落盘保障（存储系统底线）：</para>
/// <para>Dispose 必须先把 mutable 区 [FlushedUntilAddress, TailAddress) 的数据落盘（FlushUntil(TailAddress)），
/// 然后才能释放页 native 内存——否则 mutable 区数据永久丢失。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
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
