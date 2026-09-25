using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierZSet member 点查索引 keyResolver（HashKeyResolver 同构——record key 即索引键：
/// ZSetKey 自述 (域, member 哈希)，直接透传 Ring record key；墓碑感知由 Ring 扫描原生携带）。
/// </summary>
/// <param name="ring">数据 Ring（record key 直读）。</param>
internal sealed class ZSetKeyResolver(RingOfZSetKey ring) : IKeyResolver<ZSetKey>
{
    /// <inheritdoc/>
    public bool TryGetKey(LogicalAddress addr, out ZSetKey key) => ring.TryGetKey(addr, out key);

    /// <inheritdoc/>
    public LogicalAddress GetFlushedWatermark() => ring.GetFlushedWatermark();

    /// <inheritdoc/>
    public async IAsyncEnumerable<(ZSetKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
        LogicalAddress begin, LogicalAddress end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (key, addr, tomb) in ring.ScanAsync(begin, end, ct).ConfigureAwait(false))
            yield return (key, addr, tomb);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(ZSetKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
        => ScanAsync(ring.BeginAddress, ring.TailAddress, ct);
}

/// <summary>
/// ZSet score 有序索引 keyResolver（record key 无 score 槽——读 envelope TZS1 反解
/// (域, score, member 哈希)；墓碑 record 无 envelope score → 不可解析，跳过——陈旧条目由产品层
/// 恢复对账「live 判据」收口，spec §7 双索引各自重建、交集 = 真相）。
/// </summary>
/// <param name="ring">数据 Ring（读 record envelope 反解 score）。</param>
internal sealed class ZScoreKeyResolver(RingOfZSetKey ring) : IKeyResolver<ZScoreKey>
{
    /// <inheritdoc/>
    public bool TryGetKey(LogicalAddress addr, out ZScoreKey key)
    {
        key = default;
        var recordKey = ring.GetKey(addr);
        if (recordKey.ValueLength < ZSetEnvelope.HeaderSize) return false;   // 墓碑/损坏——不参与重放
        var buf = new byte[recordKey.ValueLength];
        if (ring.GetValue(addr, buf) < ZSetEnvelope.HeaderSize) return false;
        if (!ZSetEnvelope.TryPeekHeader(buf, out var domain, out var scoreEncoded, out _)) return false;
        var zsetKey = recordKey.Key;
        key = new ZScoreKey(domain, scoreEncoded, zsetKey.HashLo, zsetKey.HashHi);
        return true;
    }

    /// <inheritdoc/>
    public LogicalAddress GetFlushedWatermark() => ring.GetFlushedWatermark();

    /// <inheritdoc/>
    public async IAsyncEnumerable<(ZScoreKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
        LogicalAddress begin, LogicalAddress end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, addr, tomb) in ring.ScanAsync(begin, end, ct).ConfigureAwait(false))
        {
            if (tomb) continue;
            if (!TryGetKey(addr, out var key)) continue;
            yield return (key, addr, false);
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(ZScoreKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
        => ScanAsync(ring.BeginAddress, ring.TailAddress, ct);
}
