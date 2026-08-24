namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    public override bool Delete(TKey key)
    {
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
