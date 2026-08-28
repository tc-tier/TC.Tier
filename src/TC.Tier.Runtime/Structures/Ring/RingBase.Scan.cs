using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase IKeyResolver 实现 partial——ScanAsync 异步迭代器（包装 OpenScanCursor，冷区真异步 IO）。
/// <para>★ Index 恢复自建的数据面（设计稿 §3.4/§4）：拉流循环内聚在 index 恢复核心，组合层只给锚点触发。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>已落盘水位（IKeyResolver 契约——派生结构后台持久化的 footer 锚点 W）。</summary>
    public LogicalAddress GetFlushedWatermark() => FlushedUntilAddress;

    /// <summary>
    /// 范围扫描（IKeyResolver 契约）——从 begin 扫到 end（开区间），异步迭代器流式回源（冷区真异步 IO，不阻塞调用线程）。
    /// 仅吐用户数据 record（过滤 FLAG_ENTRY_IS_META 的 meta record）。
    /// </summary>
    /// <param name="begin">扫描起点（小于 BeginAddress 时游标自动夹取）。</param>
    /// <param name="end">扫描终点（开区间，不含；为 default 时取当前尾）。</param>
    /// <param name="ct">取消令牌——冷区异步回源途中响应取消（EnumeratorCancellation）。</param>
    /// <returns>(Key, Address) 异步流。</returns>
    public async IAsyncEnumerable<(TKey Key, LogicalAddress Address)> ScanAsync(
        LogicalAddress begin, LogicalAddress end,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        await using var cursor = OpenScanCursor(begin, end);
        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            // meta record 非用户数据（水位块），索引重建语义只吐数据 record
            var fields = cursor.GetFields();
            if ((fields.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0) continue;
            if (TryGetKey(cursor.CurrentAddress, out var key))
                yield return (key, cursor.CurrentAddress);
        }
    }

    /// <summary>全量扫描（IKeyResolver 重载契约）——从 BeginAddress 扫到当前尾（TailAddress）。</summary>
    /// <param name="ct">取消令牌——冷区异步回源途中响应取消。</param>
    /// <returns>(Key, Address) 异步流。</returns>
    public IAsyncEnumerable<(TKey Key, LogicalAddress Address)> ScanAsync(CancellationToken ct = default)
        => ScanAsync(BeginAddress, TailAddress, ct);
}
