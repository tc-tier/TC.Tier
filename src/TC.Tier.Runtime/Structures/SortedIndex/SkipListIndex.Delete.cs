using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    public override unsafe bool Delete(TKey key)
    {
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

            _reclaimedNodes[key] = targetAddr;
            Interlocked.Decrement(ref _entryCount);
            return true;
        }
        finally
        {
            _epoch.Suspend();
        }
    }
}
