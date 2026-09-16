namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// BTreeIndex 查询 partial——TryGetMax（Latest 语义）与 TryGetFloor（floor/前驱语义）。
/// </summary>
public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>下降路径容量上限（内节点扇出 ≤10——10^32 条目才达 32 层；亦是环防御阈值）。</summary>
    private const int MaxDescentPath = 32;

    /// <summary>
    /// 键序最大条目——右下降取末叶末条，O(log n)（正常路径）。
    /// <para>★ 空叶容忍（删除不重平衡可留 Count=0 的叶）：最右子树全空时逐子左移重试
    ///   （迭代 + 显式栈回溯，最坏退化 O(节点数)，仅出现在右区大面积清空后的查询）。</para>
    /// <para>★ 环防御：子指针哨兵模糊（未设置槽 = Empty = 合法地址——(0,0) 处可能是锚点帧
    ///   字节而非节点，读出幻影 internal 可成环；实测自递归栈溢出 2026-08-27）——
    ///   下降深度超 <see cref="MaxDescentPath"/> 视为坏路径返回 miss，任何环从崩溃降级为 miss。</para>
    /// </summary>
    /// <param name="key">输出：最大条目的 key。</param>
    /// <param name="value">输出：最大条目的 value 逻辑地址。</param>
    /// <returns>false = 空索引。</returns>
    public override bool TryGetMax(out TKey key, out LogicalAddress value)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥）
        _epoch.Resume();
        try
        {
            key = default!;
            value = LogicalAddress.Empty;
            if (_rootAddress == LogicalAddress.Empty) return false;
            return TryGetRightmostEntry(_rootAddress, _cachedRoot, out key, out value);
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>
    /// 键序 ≤ <paramref name="key"/> 的最大条目（前驱查询），O(log n)。
    /// <para>★ 无 Prev 指针的标准 B+ 树前驱算法：下降到 key 的归属叶（沿途记录各层节点+下降位），
    ///   叶内有 ≤ key 的条目即得；否则自最深祖先逐层向左重试右降（c[i-1] → c[i-2] → …），
    ///   首个命中子树的右降末条即前驱——空叶（全删）自然跳过（左移重试），无左邻可试 = key 小于全树键。</para>
    /// </summary>
    /// <param name="key">查找键（含）。</param>
    /// <param name="floorKey">输出：命中条目的 key（≤ 查找键）。</param>
    /// <param name="value">输出：命中条目的 value 逻辑地址。</param>
    /// <returns>false = 无 ≤ key 的条目。</returns>
    public override bool TryGetFloor(TKey key, out TKey floorKey, out LogicalAddress value)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥）
        _epoch.Resume();
        try
        {
            floorKey = default!;
            value = LogicalAddress.Empty;
            if (_rootAddress == LogicalAddress.Empty) return false;
            return FloorInSubtree(_rootAddress, _cachedRoot, key, out floorKey, out value);
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>
    /// 子树内 floor 下降核心：与 Find 同形下降（i = 首个使 key &lt; nodeKey[i] 的位 → 子 i），
    /// i &gt; 0 时记录左邻子树（GetValue(i-1)）地址；归属叶无 ≤ key 条目（含空叶）时自最深祖先
    /// 逐层向左重试右降（环防御同右降）。递归换为祖先栈回溯。
    /// </summary>
    private bool FloorInSubtree(LogicalAddress subtreeAddr, BTreeNode subtreeRoot, TKey key,
        out TKey floorKey, out LogicalAddress value)
    {
        floorKey = default!;
        value = LogicalAddress.Empty;

        Span<(LogicalAddress NodeAddr, int Index)> path = stackalloc (LogicalAddress, int)[MaxDescentPath];
        int depth = 0;
        var node = subtreeRoot;
        var nodeAddr = subtreeAddr;
        while (!node.IsLeaf)
        {
            if (depth >= MaxDescentPath) return false;   // 环防御
            int i;
            for (i = 0; i < node.Count; i++)
            {
                if (KeyComparer.Compare(key, node.GetKey(i)) < 0) break;
            }
            path[depth++] = (nodeAddr, i);
            nodeAddr = node.GetValue(i);
            node = GetInternalNode(nodeAddr);
        }

        // 叶内定位：首个 ≥ key 的槽位 pos
        int pos = 0;
        while (pos < node.Count && KeyComparer.Compare(node.GetKey(pos), key) < 0)
            pos++;

        // 精确命中（== key，≤ 语义含等）优先
        if (pos < node.Count && KeyComparer.Equals(node.GetKey(pos), key))
        {
            floorKey = node.GetKey(pos);
            value = node.GetValue(pos);
            return true;
        }
        // 本叶有 < key 的条目（pos-1 即本叶内前驱）
        if (pos > 0)
        {
            floorKey = node.GetKey(pos - 1);
            value = node.GetValue(pos - 1);
            return true;
        }

        // 归属叶无 ≤ key 条目（含空叶）：自最深祖先逐层向左重试右降——首个命中的右降末条即前驱
        for (int d = depth - 1; d >= 0; d--)
        {
            var (ancAddr, ancIndex) = path[d];
            var anc = GetInternalNode(ancAddr);
            for (int j = ancIndex - 1; j >= 0; j--)
            {
                var childAddr = anc.GetValue(j);
                if (TryGetRightmostEntry(childAddr, GetInternalNode(childAddr), out floorKey, out value))
                    return true;
            }
        }
        return false;   // key 小于全树键（全路径无左邻）
    }

    /// <summary>
    /// 子树内最右条目——迭代右降（每层取最右子）+ 显式栈回溯（空叶/空子树时逐子左移；
    /// 帧保留各自剩余子下标，仅穷尽时弹出）；下降深度超 <see cref="MaxDescentPath"/> 返回 false
    /// （环防御，见 <see cref="TryGetMax"/> 注）。
    /// </summary>
    private bool TryGetRightmostEntry(LogicalAddress rootAddr, BTreeNode root, out TKey key, out LogicalAddress value)
    {
        key = default!;
        value = LogicalAddress.Empty;

        // 栈元素（帧）：(该层节点地址, 剩余未试的最右子下标)——尝试即消耗（减一），穷尽（<0）才弹出
        Span<(LogicalAddress Addr, int NextChild)> descent = stackalloc (LogicalAddress, int)[MaxDescentPath];
        int depth = 0;
        var node = root;
        var nodeAddr = rootAddr;

        while (true)
        {
            if (depth >= MaxDescentPath) return false;   // 环防御——超树高即坏路径

            if (node.IsLeaf)
            {
                if (node.Count > 0)
                {
                    key = node.GetKey(node.Count - 1);
                    value = node.GetValue(node.Count - 1);
                    return true;
                }

                // 空叶：找最深含未试左邻子的帧，换子重降（穷尽帧永久弹出）
                bool advanced = false;
                while (depth > 0)
                {
                    var (ancAddr, child) = descent[depth - 1];
                    if (child < 0)
                    {
                        depth--;   // 该帧子全试尽——弹出
                        continue;
                    }
                    descent[depth - 1] = (ancAddr, child - 1);   // 消耗该子
                    var anc = GetInternalNode(ancAddr);
                    nodeAddr = anc.GetValue(child);
                    node = GetInternalNode(nodeAddr);
                    advanced = true;
                    break;   // depth 不变——新子树的重降将覆写其下帧，祖先帧保留
                }
                if (!advanced) return false;   // 全树空
                continue;
            }

            // internal：记帧（最右子下标 Count 正在试，记剩余 Count-1），右降
            descent[depth] = (nodeAddr, node.Count - 1);
            depth++;
            nodeAddr = node.GetValue(node.Count);
            node = GetInternalNode(nodeAddr);
        }
    }
}
