using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// SkipListIndex 删除 partial——单键 Delete（逐层拆链）与键序前缀批量删（各层边界推进）。
/// </summary>
public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// 键序前缀批量删——删除全部 key &lt; <paramref name="boundExclusive"/> 的条目，O(k) 链处理。
    /// <para>★ 前缀性质：各层的逻辑边界同为"首个 ≥ bound 的节点"——逐层把 head 指针直接推进到
    ///   该层边界即可整体旁路被删区（无需逐节点拆链）；层 0 链遍历恰好经过每个被删节点一次
    ///   （回收簿记 + 计数在此完成）。</para>
    /// </summary>
    /// <param name="boundExclusive">边界键（不含）——全部 key &lt; 此值的条目被删除。</param>
    /// <returns>删除条数。</returns>
    public override unsafe long TruncatePrefix(TKey boundExclusive)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥——head 指针推进/回收簿记一致性）
        _epoch.Resume();
        try
        {
            long deleted = 0;
            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                var nextAddr = ReadLevel(_headPtr, i);
                var firstGE = LogicalAddress.Empty;
                while (nextAddr != LogicalAddress.Empty)
                {
                    var next = GetNode(nextAddr);
                    if (KeyComparer.Compare(ReadKey(next), boundExclusive) < 0)
                    {
                        if (i == 0)
                        {
                            deleted++;
                        }
                        nextAddr = ReadLevel(next, i);
                    }
                    else
                    {
                        firstGE = nextAddr;
                        break;
                    }
                }
                WriteLevel(_headPtr, i, firstGE);   // head 指针越过被删前缀（各层同界）
            }

            MarkDirty(_headAddress);
            if (deleted > 0)
                Interlocked.Add(ref _entryCount, -deleted);
            return deleted;
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>删除条目——塔链逐层下降记录前驱、逐层拆链回收（epoch 读保护内）。</summary>
    /// <param name="key">条目键。</param>
    /// <returns>true = 真删到；false = 不存在。</returns>
    public override unsafe bool Delete(TKey key)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥——塔链拆链/前驱覆写一致性）
        _epoch.Resume();
        try
        {
            // ★ 前驱驻留指针 + 地址各一栈表（旧形两堆数组——顺手 stackalloc 化）
            var preds = stackalloc byte*[_maxLevel];
            var addrs = stackalloc LogicalAddress[_maxLevel];
            var current = _headPtr;
            LogicalAddress currentAddr = LogicalAddress.Empty;

            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                var nextAddr = ReadLevel(current, i);
                while (nextAddr != LogicalAddress.Empty)
                {
                    var next = GetNode(nextAddr);
                    if (KeyComparer.Compare(ReadKey(next), key) < 0)
                    {
                        current = next;
                        currentAddr = nextAddr;
                        nextAddr = ReadLevel(current, i);
                    }
                    else
                    {
                        break;
                    }
                }
                preds[i] = current;
                addrs[i] = currentAddr;
            }

            var targetAddr = ReadLevel(preds[0], 0);
            if (targetAddr == LogicalAddress.Empty) return false;

            var target = GetNode(targetAddr);
            if (!KeyComparer.Equals(ReadKey(target), key)) return false;

            bool headChanged = false;
            for (int i = 0; i < target[LevelCountOffset]; i++)
            {
                var targetLevelAddr = ReadLevel(target, i);
                var pred = preds[i];
                if (ReadLevel(pred, i) == targetAddr)
                {
                    var predAddr = addrs[i];
                    if (predAddr == LogicalAddress.Empty)
                    {
                        CasLevel(ref LevelRef(_headPtr, i), targetAddr, targetLevelAddr);
                        headChanged = true;
                    }
                    else
                    {
                        // 指针直写驻留前驱（旧形：缓存命中即覆写缓存副本——驻留形缓存即唯一真相）
                        WriteLevel(pred, i, targetLevelAddr);
                        MarkDirty(predAddr);   // ★ 前驱链变更延迟写回（物化前 dump 批量写回）
                    }
                }
            }
            if (headChanged)
                MarkDirty(_headAddress);   // ★ 塔顶变更延迟写回（同上）

            // ★ 节点空间不回收（族契约，同 BTreeIndex.Delete）——arena 块只增不搬移，删除只摘链；
            //   删除节点的 arena 块留待结构 Dispose 整体释放（无写侧回收账本）。
            Interlocked.Decrement(ref _entryCount);
            return true;
        }
        finally
        {
            _epoch.Suspend();
        }
    }
}
