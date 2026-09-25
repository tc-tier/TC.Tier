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

    /// <summary>
    /// 键域区间批量删——删除全部 key ∈ [lowerInclusive, upperExclusive) 的条目（dense 逐域
    /// retention trim 专用：键空间含领先前缀字段时，单侧 TruncatePrefix 会把更低前缀域的存活条目
    /// 一并判入"前缀"——必须双侧界住域内区间；BTreeIndex.TruncateRange 同构对称面）。
    /// <para>★ 算法：双侧塔链下降（与 Insert/Delete 同比较语义）——lower 下降记录各层前驱
    ///   （首个 key &lt; lower 的最后节点，head = Empty 哨兵），upper 下降记录各层后继（首个
    ///   key ≥ upper）；各层 relink 前驱.层[i] = 后继即整体旁路区间塔段（被删节点塔链不逐层拆——
    ///   relink 后不可达，arena 块与引擎帧留待整体释放，族契约同 Delete）。计数 = 层 0 链遍历
    ///   （恰经每被删节点一次）。未改动层（前驱.层[i] 已 == 后继）零写零脏标。</para>
    /// </summary>
    /// <param name="lowerInclusive">下界（含）。</param>
    /// <param name="upperExclusive">上界（不含）。</param>
    /// <returns>删除条数。</returns>
    public unsafe long TruncateRange(TKey lowerInclusive, TKey upperExclusive)
    {
        if (KeyComparer.Compare(lowerInclusive, upperExclusive) >= 0) return 0;
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥——多层 relink/计数一致性）
        _epoch.Resume();
        try
        {
            Span<LogicalAddress> predAddrs = stackalloc LogicalAddress[_maxLevel];   // 各层最后 key < lower（head = Empty）
            Span<LogicalAddress> succs = stackalloc LogicalAddress[_maxLevel];      // 各层首个 key ≥ upper

            // lower 下降（Delete 同形——记录各层前驱；不要求精确命中）
            var current = _headPtr;
            LogicalAddress currentAddr = LogicalAddress.Empty;
            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                var nextAddr = ReadLevel(current, i);
                while (nextAddr != LogicalAddress.Empty)
                {
                    var next = GetNode(nextAddr);
                    if (KeyComparer.Compare(ReadKey(next), lowerInclusive) < 0)
                    {
                        current = next;
                        currentAddr = nextAddr;
                        nextAddr = ReadLevel(current, i);
                    }
                    else break;
                }
                predAddrs[i] = currentAddr;
            }

            // upper 下降（从 head 重入——记录各层首个 key ≥ upper）
            current = _headPtr;
            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                var nextAddr = ReadLevel(current, i);
                while (nextAddr != LogicalAddress.Empty)
                {
                    var next = GetNode(nextAddr);
                    if (KeyComparer.Compare(ReadKey(next), upperExclusive) < 0)
                    {
                        current = next;
                        nextAddr = ReadLevel(current, i);
                    }
                    else break;
                }
                succs[i] = nextAddr;
            }

            // 计数（层 0 链遍历——relink 前走，恰经每被删节点一次）
            long deleted = 0;
            var walkAddr = ReadLevel(GetPredNode(predAddrs[0]), 0);
            var stopAddr = succs[0];
            while (walkAddr != LogicalAddress.Empty && walkAddr != stopAddr)
            {
                deleted++;
                walkAddr = ReadLevel(GetNode(walkAddr), 0);
            }

            // 各层 relink（未变动层零写零脏标）
            bool headChanged = false;
            for (int i = 0; i < _currentLevel; i++)
            {
                var succAddr = succs[i];
                if (predAddrs[i] == LogicalAddress.Empty)
                {
                    if (ReadLevel(_headPtr, i) != succAddr)
                    {
                        WriteLevel(_headPtr, i, succAddr);
                        headChanged = true;
                    }
                }
                else
                {
                    var pred = GetNode(predAddrs[i]);
                    if (ReadLevel(pred, i) != succAddr)
                    {
                        WriteLevel(pred, i, succAddr);
                        MarkDirty(predAddrs[i]);
                    }
                }
            }
            if (headChanged)
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

    /// <summary>前驱地址 → 驻留节点指针（Empty = head 哨兵——Delete/Insert 下降同款哨兵约定）。</summary>
    private unsafe byte* GetPredNode(LogicalAddress predAddr)
        => predAddr == LogicalAddress.Empty ? _headPtr : GetNode(predAddr);

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
