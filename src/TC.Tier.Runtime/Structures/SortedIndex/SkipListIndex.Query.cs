namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// SkipListIndex 查询 partial——TryGetMax（塔链右行）与 TryGetFloor（floor/前驱语义）。
/// </summary>
public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// 键序最大条目——塔链右行到头（每层沿 Next 走到 Empty；期望 O(log n)），层 0 末节点即最大。
    /// </summary>
    /// <param name="key">输出：最大条目的 key。</param>
    /// <param name="value">输出：最大条目的 value 逻辑地址。</param>
    /// <returns>false = 空索引。</returns>
    public override unsafe bool TryGetMax(out TKey key, out LogicalAddress value)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥）
        _epoch.Resume();
        try
        {
            var current = _headPtr;
            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                var nextAddr = ReadLevel(current, i);
                while (nextAddr != LogicalAddress.Empty)
                {
                    current = GetNode(nextAddr);
                    nextAddr = ReadLevel(current, i);
                }
            }

            if (current == _headPtr)
            {
                key = default!;
                value = LogicalAddress.Empty;
                return false;
            }
            key = ReadKey(current);
            value = ReadValue(current);
            return true;
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>
    /// 键序 ≤ <paramref name="key"/> 的最大条目——与 FindNoEpoch 同形塔链下降
    /// （current 停在最后一个 &lt; key 的节点）；层 0 后继 == key 则取等值命中，否则 current 即前驱。
    /// </summary>
    /// <param name="key">查找键（含）。</param>
    /// <param name="floorKey">输出：命中条目的 key（≤ 查找键）。</param>
    /// <param name="value">输出：命中条目的 value 逻辑地址。</param>
    /// <returns>false = 无 ≤ key 的条目。</returns>
    public override unsafe bool TryGetFloor(TKey key, out TKey floorKey, out LogicalAddress value)
        => FloorByOp(key, includeEqual: true, out floorKey, out value);

    /// <inheritdoc/>
    /// <remarks>与 <see cref="TryGetFloor"/> 同一下降核心（排除等值命中）——反向步进迭代的步进原语。</remarks>
    public override unsafe bool TryGetPrev(TKey key, out TKey prevKey, out LogicalAddress value)
        => FloorByOp(key, includeEqual: false, out prevKey, out value);

    /// <summary>floor/prev 共用体：塔链下降后 current = 最后 &lt; key 的节点；includeEqual 时层 0
    /// 后继 == key 取等值命中，否则 current 即答案（严格排除自身）。</summary>
    private unsafe bool FloorByOp(TKey key, bool includeEqual, out TKey hitKey, out LogicalAddress value)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥）
        _epoch.Resume();
        try
        {
            var current = _headPtr;
            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                var nextAddr = ReadLevel(current, i);
                while (nextAddr != LogicalAddress.Empty)
                {
                    var next = GetNode(nextAddr);
                    if (KeyComparer.Compare(ReadKey(next), key) < 0)
                    {
                        current = next;
                        nextAddr = ReadLevel(current, i);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // 层 0 后继是首个 ≥ key 的节点：== key 且 ≤ 语义时取等值命中
            var succAddr = ReadLevel(current, 0);
            if (includeEqual && succAddr != LogicalAddress.Empty)
            {
                var succ = GetNode(succAddr);
                if (KeyComparer.Equals(ReadKey(succ), key))
                {
                    hitKey = key;
                    value = ReadValue(succ);
                    return true;
                }
            }

            if (current == _headPtr)
            {
                // 无 < key 的节点且（≤ 语义时）无等值（head 哨兵不承载数据）
                hitKey = default!;
                value = LogicalAddress.Empty;
                return false;
            }
            hitKey = ReadKey(current);
            value = ReadValue(current);
            return true;
        }
        finally
        {
            _epoch.Suspend();
        }
    }
}
