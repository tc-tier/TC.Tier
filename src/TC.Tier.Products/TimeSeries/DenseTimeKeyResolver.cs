using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// dense 时间索引专用 keyResolver（<see cref="TimeKeyResolver"/> 的 TTS2 同构——#443 设计稿 §5.5）：
/// TryGetKey 读 record 的 envelope (SeriesId, ts) 槽 → 索引键 (sid, ts, addr.Offset)；
/// 非 dense record（TTS2 magic 不符）返回 false——天然不参与索引重放（单序列 TTS1 record 互不误读）。
/// <para>★ tiebreaker = record 地址 Offset（写后即知，同刻按写入序稳定）。</para>
/// </summary>
/// <param name="ring">数据 Ring（读 record envelope 反解 sid/ts）。</param>
internal sealed class DenseTimeKeyResolver(RingOfDenseTimeKey ring) : IKeyResolver<DenseTimeKey>
{
    /// <inheritdoc/>
    public bool TryGetKey(LogicalAddress addr, out DenseTimeKey key)
    {
        key = default;
        var recordKey = ring.GetKey(addr);
        if (recordKey.ValueLength < TimeSeriesEnvelope.DenseHeaderSize) return false;
        var buf = new byte[recordKey.ValueLength];   // GetValue 需完整 value 长度（头只是前缀）
        if (ring.GetValue(addr, buf) < TimeSeriesEnvelope.DenseHeaderSize) return false;
        if (!TimeSeriesEnvelope.TryUnwrapDense(buf, out _, out var sid, out var ts, out _, out _)) return false;
        key = new DenseTimeKey(sid, ts, addr.Offset);
        return true;
    }

    /// <inheritdoc/>
    public LogicalAddress GetFlushedWatermark() => ring.GetFlushedWatermark();

    /// <inheritdoc/>
    public async IAsyncEnumerable<(DenseTimeKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
        LogicalAddress begin, LogicalAddress end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, addr, _) in ring.ScanAsync(begin, end, ct).ConfigureAwait(false))
        {
            if (TryGetKey(addr, out var key))
                yield return (key, addr, false);
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(DenseTimeKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
        => ScanAsync(ring.BeginAddress, ring.TailAddress, ct);
}
