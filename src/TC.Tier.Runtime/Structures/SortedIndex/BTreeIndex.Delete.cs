namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// BTreeIndex 删除 partial——单键 Delete 与键序前缀批量删 TruncatePrefix（retention trim）。
/// </summary>
public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// 键序前缀批量删——删除全部 key &lt; <paramref name="boundExclusive"/> 的条目（retention trim 专用），
    /// O(路径 + 覆盖叶数) 批量完成而非逐键 Delete 的 O(k·log n)。
    /// <para>★ 算法：Find 同形下降到 bound 的归属叶（沿途记录各层节点+下降位）——归属叶内前段
    ///   （首个 ≥ bound 槽位之前）整段摘除；路径每层左侧完全覆盖子树（c[0..i-1]，键域全 &lt; bound）
    ///   逐叶清零（结构保留——与单键 Delete 同款不重平衡哲学：叶可 Count=0，查询侧空叶容忍
    ///   由游标/前驱算法保证）。节点空间不回收（引擎中段回收=独立课题，现状与单键删除等价）。</para>
    /// </summary>
    /// <param name="boundExclusive">边界键（不含）——全部 key &lt; 此值的条目被删除。</param>
    /// <returns>删除条数。</returns>
    public override long TruncatePrefix(TKey boundExclusive)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥——路径摘除/叶清零一致性）
        _epoch.Resume();
        try
        {
            if (_rootAddress == LogicalAddress.Empty) return 0;

            Span<(LogicalAddress NodeAddr, int Index)> path = stackalloc (LogicalAddress, int)[MaxDescentPath];
            int depth = 0;
            var node = _cachedRoot;
            var nodeAddr = _rootAddress;
            while (!node.IsLeaf)
            {
                int i;
                for (i = 0; i < node.Count; i++)
                {
                    if (KeyComparer.Compare(boundExclusive, node.GetKey(i)) < 0) break;
                }
                path[depth++] = (nodeAddr, i);
                nodeAddr = node.GetValue(i);
                node = GetInternalNode(nodeAddr);
            }

            long deleted = 0;

            // 路径各层左侧完全覆盖子树：逐叶清零 + 计数（层间子树互斥，并集 = 全部 key < bound 的条目）
            for (int d = 0; d < depth; d++)
            {
                var (ancAddr, ancIndex) = path[d];
                var anc = GetInternalNode(ancAddr);
                for (int j = 0; j < ancIndex; j++)
                {
                    deleted += ClearSubtreeLeaves(anc.GetValue(j));
                }
            }

            // 归属叶内前段摘除（首个 ≥ bound 槽位之前）
            int pos = 0;
            while (pos < node.Count && KeyComparer.Compare(node.GetKey(pos), boundExclusive) < 0)
                pos++;
            if (pos > 0)
            {
                node.ShiftLeft(pos, 0, node.Count);   // 前段左移出（后段整体前移 pos 位）
                node.Count -= (ushort)pos;
                WriteNodeContent(nodeAddr, node);
                if (nodeAddr == _rootAddress) _cachedRoot = node;
                else RefreshCache(nodeAddr, node);
                deleted += pos;
            }

            if (deleted > 0)
                Interlocked.Add(ref _entryCount, -deleted);
            return deleted;
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>子树逐叶清零（internal 结构保留路由，叶 Count=0）——返回清零条数。</summary>
    private long ClearSubtreeLeaves(LogicalAddress subtreeRoot)
    {
        var node = GetInternalNode(subtreeRoot);
        if (node.IsLeaf)
        {
            if (node.Count == 0) return 0;
            long n = node.Count;
            node.Count = 0;
            WriteNodeContent(subtreeRoot, node);
            RefreshCache(subtreeRoot, node);
            return n;
        }

        long total = 0;
        for (int j = 0; j <= node.Count; j++)
            total += ClearSubtreeLeaves(node.GetValue(j));
        return total;
    }

    /// <summary>
    /// 键域区间批量删——删除全部 key ∈ [lowerInclusive, upperExclusive) 的条目（dense 逐序列
    /// retention trim 专用：键空间含领先前缀字段时，单侧 TruncatePrefix 会把更低前缀域的存活条目
    /// 一并判入"前缀"——必须双侧界住域内区间）。
    /// <para>★ 算法：双侧下降索引 iL/iU（与 Find/Insert 同比较语义——下降一致性保证子树键域互斥）。
    ///   同子树（iL==iU）递归；跨界时边界子树递归区间删、中间子树（键域必全落区间内）逐叶清零。</para>
    /// </summary>
    /// <param name="lowerInclusive">下界（含）。</param>
    /// <param name="upperExclusive">上界（不含）。</param>
    /// <returns>删除条数。</returns>
    public long TruncateRange(TKey lowerInclusive, TKey upperExclusive)
    {
        if (KeyComparer.Compare(lowerInclusive, upperExclusive) >= 0) return 0;
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥——与 TruncatePrefix 同款一致性窗口）
        _epoch.Resume();
        try
        {
            if (_rootAddress == LogicalAddress.Empty) return 0;
            long deleted = TruncateRangeFromNode(_rootAddress, lowerInclusive, upperExclusive);
            if (deleted > 0)
                Interlocked.Add(ref _entryCount, -deleted);
            return deleted;
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>区间删递归体：叶内 [lo, hi) 段摘除；internal 按双侧下降索引分界（边界递归/中间全清）。</summary>
    private long TruncateRangeFromNode(LogicalAddress nodeAddr, TKey lower, TKey upper)
    {
        var node = GetInternalNode(nodeAddr);

        if (node.IsLeaf)
        {
            int lo = 0;
            while (lo < node.Count && KeyComparer.Compare(node.GetKey(lo), lower) < 0) lo++;
            int hi = lo;
            while (hi < node.Count && KeyComparer.Compare(node.GetKey(hi), upper) < 0) hi++;
            int count = hi - lo;
            if (count <= 0) return 0;
            node.ShiftLeft(hi, lo, node.Count);   // [lo, hi) 左移出（from=hi → to=lo，后段整体前移）
            node.Count -= (ushort)count;
            WriteNodeContent(nodeAddr, node);
            if (nodeAddr == _rootAddress) _cachedRoot = node;
            else RefreshCache(nodeAddr, node);
            return count;
        }

        int iL = DescendIndex(node, lower);
        int iU = DescendIndex(node, upper);
        if (iL == iU)
            return TruncateRangeFromNode(node.GetValue(iL), lower, upper);

        // 边界子树递归（iL 子树含 < lower 的键、iU 子树含 ≥ upper 的键——只删域内段；
        // 各子树自写回——父节点 keys/children 未变，无需写回）。中间子树键域必全落
        // [lower, upper)（下降一致性的直接推论）——整树清零。
        long deleted = TruncateRangeFromNode(node.GetValue(iL), lower, upper);
        for (int j = iL + 1; j < iU; j++)
            deleted += ClearSubtreeLeaves(node.GetValue(j));
        deleted += TruncateRangeFromNode(node.GetValue(iU), lower, upper);
        return deleted;
    }

    /// <summary>下降索引（与 Find/Insert 同比较语义）：首个 separator &gt; key 的子位（无则 Count——最右子树）。</summary>
    private int DescendIndex(BTreeNode node, TKey key)
    {
        for (int i = 0; i < node.Count; i++)
        {
            if (KeyComparer.Compare(key, node.GetKey(i)) < 0) return i;
        }
        return node.Count;
    }

    /// <summary>删除条目——叶根直接移除；internal 树沿 Find 同路径下降到含 key 叶子移除（epoch 读保护内；本轮不重平衡）。</summary>
    /// <param name="key">条目键。</param>
    /// <returns>true = 真删到；false = 不存在。</returns>
    public override bool Delete(TKey key)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥）
        _epoch.Resume();
        try
        {
            if (_rootAddress == LogicalAddress.Empty) return false;

            var root = _cachedRoot;
            if (root.IsLeaf)
            {
                var pos = root.FindPosition(key, KeyComparer);
                if (pos < 0) return false;

                root.ShiftLeft(pos + 1, pos, root.Count);
                root.Count--;
                WriteNodeContent(_rootAddress, root);
                _cachedRoot = root;
                Interlocked.Decrement(ref _entryCount);
                return true;
            }

            return DeleteFromInternal(key);
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    // 本轮最小修复：沿与 Find 一致的遍历路径下降到含 key 的叶子节点（单次下降同步记录地址），
    // 从该叶子移除 entry（ShiftLeft + Count--），返回是否真删到。
    // 注：本轮不实现重平衡（节点合并/借键），删除后节点可能稀疏——正确性保证但填充率不保证。
    private bool DeleteFromInternal(TKey key)
    {
        var node = _cachedRoot;
        var nodeAddr = _rootAddress;

        // 沿内部节点下降到叶子（与 Find 同比较语义），记录地址供写回/缓存刷新
        while (!node.IsLeaf)
        {
            int i;
            for (i = 0; i < node.Count; i++)
            {
                if (KeyComparer.Compare(key, node.GetKey(i)) < 0) break;
            }
            nodeAddr = node.GetValue(i);
            node = GetInternalNode(nodeAddr);
        }

        var pos = node.FindPosition(key, KeyComparer);
        if (pos < 0) return false;

        node.ShiftLeft(pos + 1, pos, node.Count);
        node.Count--;
        WriteNodeContent(nodeAddr, node);
        // ★ 叶子同样经 GetInternalNode 进缓存（Find 下降路径）——写回后必须刷新，
        //   否则后续 Find 读陈旧缓存命中已删 key（旧码断言"叶子不缓存"是错的，删除静默失效）。
        RefreshCache(nodeAddr, node);
        Interlocked.Decrement(ref _entryCount);
        return true;
    }
}
