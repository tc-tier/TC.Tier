namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>internal 节点容量：k 键须有 k+1 个子指针，struct 仅 9 个 Value 槽（Value0..8）——上限 8 键/9 子。
    /// <para>★ 旧码把叶子容量 MaxEntries(9) 当 internal 键容量用——满 8 键后吸收分离键写 SetValue(9) 越界
    ///   （IndexOutOfRange），且溢出传播错调根叶子分裂路径。internal 容量独立于叶子是结构事实。</para></summary>
    private const int InternalMaxKeys = MaxEntries - 1;

    /// <summary>子节点分裂后待父节点吸收的提升项（分离键 + 右半地址）。</summary>
    private readonly record struct PendingSplit(TKey SeparatorKey, LogicalAddress RightAddress);

    public override LogicalAddress Insert(TKey key, LogicalAddress valueAddress, LogicalAddress beginAddress)
    {
        _epoch.Resume();
        try
        {
            bool inserted = false;
            if (_rootAddress == LogicalAddress.Empty)
            {
                var newRoot = new BTreeNode { IsLeaf = true, IsRoot = true, Count = 0 };
                _rootAddress = AllocateNode(_nodeSize);
                WriteNodeContent(_rootAddress, newRoot);
                _cachedRoot = newRoot;
            }

            var root = _cachedRoot;

            if (root.IsLeaf)
            {
                var pos = root.FindPosition(key, KeyComparer);
                if (pos >= 0)
                {
                    root.SetValue(pos, valueAddress);
                    WriteNodeContent(_rootAddress, root);
                    _cachedRoot = root;
                    return valueAddress;
                }

                if (root.Count < MaxEntries)
                {
                    InsertInLeaf(ref root, key, valueAddress);
                    WriteNodeContent(_rootAddress, root);
                    _cachedRoot = root;
                    inserted = true;
                    OnInserted();
                    return valueAddress;
                }

                // 根叶子满 → 分裂 + 建新根（1 键/2 子）
                var leafRightAddr = AllocateNode(_nodeSize);
                var leafRight = SplitLeafWithInsert(ref root, key, valueAddress, leafRightAddr);
                WriteNodeContent(_rootAddress, root);
                WriteNodeContent(leafRightAddr, leafRight);
                RefreshCache(_rootAddress, root);            // ★ 旧根入缓存（脏写回取值不变式——旧根将变孩子）
                RefreshCache(leafRightAddr, leafRight);      // ★ 新右半入缓存（同上）
                var newRootAddr = AllocateNode(_nodeSize);
                var newRoot = BuildRoot(_rootAddress, leafRightAddr, leafRight.GetKey(0));
                WriteNodeContent(newRootAddr, newRoot);
                PromoteRoot(newRootAddr, newRoot);
                inserted = true;
                OnInserted();
                return valueAddress;
            }

            var pending = InsertRecursive(_rootAddress, root, key, valueAddress, out inserted);
            if (pending is { } p)
            {
                // 根 internal 分裂 → 建新根吸收提升键
                var newRootAddr = AllocateNode(_nodeSize);
                var newRoot = BuildRoot(_rootAddress, p.RightAddress, p.SeparatorKey);
                WriteNodeContent(newRootAddr, newRoot);
                PromoteRoot(newRootAddr, newRoot);
            }
            if (inserted) OnInserted();
            return valueAddress;
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>新条目落位回调（覆写不加——最新写胜出不增计数）。</summary>
    private void OnInserted() => Interlocked.Increment(ref _entryCount);

    /// <summary>递归插入——返回待父吸收的分裂项（null = 已落位）；<paramref name="inserted"/>=本树是否新增条目（覆写=false）。</summary>
    private PendingSplit? InsertRecursive(LogicalAddress nodeAddr, BTreeNode node, TKey key, LogicalAddress valueAddress,
        out bool inserted)
    {
        inserted = false;
        int i;
        for (i = 0; i < node.Count; i++)
        {
            if (KeyComparer.Compare(key, node.GetKey(i)) < 0) break;
        }

        var childAddr = node.GetValue(i);
        var child = GetInternalNode(childAddr);

        if (child.IsLeaf)
        {
            var pos = child.FindPosition(key, KeyComparer);
            if (pos >= 0)
            {
                child.SetValue(pos, valueAddress);
                WriteNodeContent(childAddr, child);
                RefreshCache(childAddr, child);
                return null;
            }

            if (child.Count < MaxEntries)
            {
                InsertInLeaf(ref child, key, valueAddress);
                WriteNodeContent(childAddr, child);
                RefreshCache(childAddr, child);
                inserted = true;
                return null;
            }

            // 子叶子满 → 分裂，首键作分离键提升给本节点
            var rightAddr = AllocateNode(_nodeSize);
            var right = SplitLeafWithInsert(ref child, key, valueAddress, rightAddr);
            WriteNodeContent(childAddr, child);
            WriteNodeContent(rightAddr, right);
            RefreshCache(childAddr, child);
            RefreshCache(rightAddr, right);
            inserted = true;
            return AbsorbSeparator(node, nodeAddr, new PendingSplit(right.GetKey(0), rightAddr));
        }

        var childPending = InsertRecursive(childAddr, child, key, valueAddress, out inserted);
        return childPending is null ? null : AbsorbSeparator(node, nodeAddr, childPending.Value);
    }

    /// <summary>叶子有序插入（未满前提——调用方守卫）。</summary>
    private void InsertInLeaf(ref BTreeNode leaf, TKey key, LogicalAddress valueAddress)
    {
        int j;
        for (j = 0; j < leaf.Count; j++)
        {
            if (KeyComparer.Compare(key, leaf.GetKey(j)) < 0) break;
        }
        leaf.ShiftRight(j, j + 1, leaf.Count);
        leaf.SetKey(j, key);
        leaf.SetValue(j, valueAddress);
        leaf.Count++;
    }

    /// <summary>满叶分裂并同插新键：10 键排序对半（左 5/右 5），右半首键即分离键；左右叶链（Next）接续。</summary>
    private BTreeNode SplitLeafWithInsert(ref BTreeNode leaf, TKey key, LogicalAddress valueAddress,
        LogicalAddress rightAddr)
    {
        int total = leaf.Count + 1;
        var entries = new (TKey Key, LogicalAddress Value)[total];
        for (int j = 0; j < leaf.Count; j++)
            entries[j] = (leaf.GetKey(j), leaf.GetValue(j));
        entries[leaf.Count] = (key, valueAddress);
        Array.Sort(entries, 0, total,
            Comparer<(TKey Key, LogicalAddress Value)>.Create((a, b) => KeyComparer.Compare(a.Key, b.Key)));

        int split = total / 2;
        var right = new BTreeNode { IsLeaf = true, Count = 0 };
        leaf.Count = (ushort)split;
        for (int j = 0; j < split; j++)
        {
            leaf.SetKey(j, entries[j].Key);
            leaf.SetValue(j, entries[j].Value);
        }
        for (int j = split; j < total; j++)
        {
            right.SetKey(j - split, entries[j].Key);
            right.SetValue(j - split, entries[j].Value);
            right.Count++;
        }

        right.Next = leaf.Next;
        leaf.Next = rightAddr;
        return right;
    }

    /// <summary>internal 节点吸收分离键——未满（&lt; 8 键）原位插入；满（8 键/9 子）分裂：左 4 键/5 子、中键提升、右 4 键/5 子。</summary>
    private PendingSplit? AbsorbSeparator(BTreeNode node, LogicalAddress nodeAddr, PendingSplit pending)
    {
        if (node.Count < InternalMaxKeys)
        {
            int i;
            for (i = 0; i < node.Count; i++)
            {
                if (KeyComparer.Compare(pending.SeparatorKey, node.GetKey(i)) < 0) break;
            }
            // keys [i..Count) 右移一格；children [i+1..Count] 右移一格（children 与 keys 错位一格）
            for (int j = node.Count - 1; j >= i; j--)
                node.SetKey(j + 1, node.GetKey(j));
            for (int j = node.Count; j >= i + 1; j--)
                node.SetValue(j + 1, node.GetValue(j));
            node.SetKey(i, pending.SeparatorKey);
            node.SetValue(i + 1, pending.RightAddress);
            node.Count++;
            WriteNodeContent(nodeAddr, node);
            RefreshCache(nodeAddr, node);
            if (nodeAddr == _rootAddress) _cachedRoot = node;
            return null;
        }

        // 已满：8 键 + 分离键 = 9 键、9 子 + 右半 = 10 子——对半分裂，中键（keys[4]）再提升
        var keys = new TKey[InternalMaxKeys + 1];
        var children = new LogicalAddress[InternalMaxKeys + 2];
        int p;
        for (p = 0; p < InternalMaxKeys; p++)
        {
            if (KeyComparer.Compare(pending.SeparatorKey, node.GetKey(p)) < 0) break;
        }
        for (int j = 0; j < p; j++) keys[j] = node.GetKey(j);
        keys[p] = pending.SeparatorKey;
        for (int j = p; j < InternalMaxKeys; j++) keys[j + 1] = node.GetKey(j);
        for (int j = 0; j <= p; j++) children[j] = node.GetValue(j);
        children[p + 1] = pending.RightAddress;
        for (int j = p + 1; j <= InternalMaxKeys; j++) children[j + 1] = node.GetValue(j);

        int mid = (InternalMaxKeys + 1) / 2;
        var rightAddr = AllocateNode(_nodeSize);
        var right = new BTreeNode { IsLeaf = false, Count = 0 };
        node.Count = (ushort)mid;
        for (int j = 0; j < mid; j++)
        {
            node.SetKey(j, keys[j]);
            node.SetValue(j, children[j]);
        }
        node.SetValue(mid, children[mid]);
        for (int j = mid + 1; j < InternalMaxKeys + 1; j++)
        {
            right.SetKey(j - mid - 1, keys[j]);
            right.SetValue(j - mid - 1, children[j]);
            right.Count++;
        }
        right.SetValue(right.Count, children[InternalMaxKeys + 1]);

        WriteNodeContent(nodeAddr, node);
        WriteNodeContent(rightAddr, right);
        RefreshCache(nodeAddr, node);
        RefreshCache(rightAddr, right);
        if (nodeAddr == _rootAddress) _cachedRoot = node;
        return new PendingSplit(keys[mid], rightAddr);
    }

    /// <summary>新根（1 键/2 子）——根分裂（叶/internal 皆然）的吸收容器。</summary>
    private static BTreeNode BuildRoot(LogicalAddress leftChild, LogicalAddress rightChild, TKey separator)
    {
        var root = new BTreeNode { IsLeaf = false, IsRoot = true, Count = 1 };
        root.SetKey(0, separator);
        root.SetValue(0, leftChild);
        root.SetValue(1, rightChild);
        return root;
    }

    /// <summary>提升新根：更新根地址/缓存。旧根条目保留（分裂时已 RefreshCache 为最新内容——
    /// 旧根变孩子后读路径/脏写回都从缓存取；全清会让旧根 miss 引擎读旧（脏节点延迟写回后引擎可能旧）。</summary>
    private void PromoteRoot(LogicalAddress newRootAddr, BTreeNode newRoot)
    {
        _rootAddress = newRootAddr;
        _cachedRoot = newRoot;
        _nodeCache.Upsert(newRootAddr, in newRoot);
    }

    /// <summary>节点变更后刷新缓存（Upsert 定容语义=命中覆写/未命中未满才进，对齐旧准入）。</summary>
    private void RefreshCache(LogicalAddress addr, BTreeNode node)
    {
        _nodeCache.Upsert(addr, in node);
    }
}
