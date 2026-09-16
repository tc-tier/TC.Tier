using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// 时序索引专用 keyResolver（tc-tier-timeseries-spec §1/§7——恢复重放的数据面）：
/// TryGetKey 读 record 的 envelope ts 槽 → 索引键 (ts, addr.Offset)；非时序 record
/// （magic 不符）返回 false——天然不参与索引重放。扫描 = Ring 流逐条解 envelope。
/// <para>★ tiebreaker = record 地址 Offset（写后即知——Queue 延迟索引同款，同刻按写入序稳定）。</para>
/// </summary>
/// <param name="ring">数据 Ring（读 record envelope 反解 ts）。</param>
internal sealed class TimeKeyResolver(RingOfTimeKey ring) : IKeyResolver<TimeKey>
{
    /// <inheritdoc/>
    public bool TryGetKey(LogicalAddress addr, out TimeKey key)
    {
        key = default;
        var recordKey = ring.GetKey(addr);
        if (recordKey.ValueLength < TimeSeriesEnvelope.HeaderSize) return false;
        var buf = new byte[recordKey.ValueLength];   // GetValue 需完整 value 长度（头只是前缀）
        if (ring.GetValue(addr, buf) < TimeSeriesEnvelope.HeaderSize) return false;
        if (!TimeSeriesEnvelope.TryUnwrap(buf, out _, out var ts, out _, out _)) return false;
        key = new TimeKey(ts, addr.Offset);
        return true;
    }

    /// <inheritdoc/>
    public LogicalAddress GetFlushedWatermark() => ring.GetFlushedWatermark();

    /// <inheritdoc/>
    public async IAsyncEnumerable<(TimeKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
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
    public IAsyncEnumerable<(TimeKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
        => ScanAsync(ring.BeginAddress, ring.TailAddress, ct);
}
