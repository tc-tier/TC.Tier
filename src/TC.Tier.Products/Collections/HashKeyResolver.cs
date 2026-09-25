using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierHash 点查索引专用 keyResolver（TimeSeries DenseTimeKeyResolver 同构——record key 即索引键：
/// HashKey 自述 (域, field 哈希)，直接透传 Ring record key；墓碑感知由 Ring 扫描原生携带）。
/// </summary>
/// <param name="ring">数据 Ring（record key 直读）。</param>
internal sealed class HashKeyResolver(RingOfHashKey ring) : IKeyResolver<HashKey>
{
    /// <inheritdoc/>
    public bool TryGetKey(LogicalAddress addr, out HashKey key) => ring.TryGetKey(addr, out key);

    /// <inheritdoc/>
    public LogicalAddress GetFlushedWatermark() => ring.GetFlushedWatermark();

    /// <inheritdoc/>
    public async IAsyncEnumerable<(HashKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
        LogicalAddress begin, LogicalAddress end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (key, addr, tomb) in ring.ScanAsync(begin, end, ct).ConfigureAwait(false))
            yield return (key, addr, tomb);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(HashKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
        => ScanAsync(ring.BeginAddress, ring.TailAddress, ct);
}
