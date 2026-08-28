using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>点查 key → value 逻辑地址（epoch 读保护内转发 <see cref="FindNoEpoch"/>）。</summary>
    /// <param name="key">查找键。</param>
    /// <returns>命中 = value 逻辑地址；未命中 = <see cref="LogicalAddress.Empty"/>。</returns>
    public override LogicalAddress Find(TKey key)
    {
        _epoch.Resume();
        try
        {
            return FindNoEpoch(key);
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>
    /// ★ 不含 epoch 进出的查找——epoch 由调用方经 <see cref="SortedIndexBase{TKey}.EnterScope"/> /
    /// <see cref="SortedIndexBase{TKey}.FindBatch"/> 在外层持有。逻辑与旧 <see cref="Find"/> 的 try-body 一致。
    /// <para>★ 扁平缓存下降：每层一次探测一步直达 + 单次槽→局部拷贝（旧形 = Dictionary 桶数组→条目数组
    /// 两跳依赖 + out/局部两次 160B 拷贝，点查 ~370ns/层的主成分）。ref 局部仅限迭代内——
    /// 宽窄转义混指（数组元素↔栈局部）是 CS8374，按值单拷贝即绕开且代价 ~10ns/层。</para>
    /// </summary>
    protected override LogicalAddress FindNoEpoch(TKey key)
    {
        if (_rootAddress == LogicalAddress.Empty)
            return LogicalAddress.Empty;

        var node = _cachedRoot;
        while (!node.IsLeaf)
        {
            int i;
            for (i = 0; i < node.Count; i++)
            {
                if (KeyComparer.Compare(key, node.GetKey(i)) < 0) break;
            }
            var nodeAddr = node.GetValue(i);

            // ★ 热路径内联展开（脏不变式：变更节点必在缓存——miss=从未变更=引擎内容最新，直读安全）
            ref readonly var child = ref _nodeCache.Find(nodeAddr);
            if (Unsafe.IsNullRef(in child))
            {
                node = ReadNodeContent(nodeAddr);
                _nodeCache.GetOrAdd(nodeAddr, in node);   // 读后回填（生长模式恒进）
            }
            else
            {
                node = child;
            }
        }

        var pos = node.FindPosition(key, KeyComparer);
        return pos >= 0 ? node.GetValue(pos) : LogicalAddress.Empty;
    }

    /// <summary>
    /// ★ 缓存优先读——Find 下降/写路径/扫描统一入口。
    /// <para>脏节点延迟写回后引擎副本可能旧甚至零——但不变式保证<b>变更节点必在驻留缓存</b>
    ///   （WriteNodeContent 前 RefreshCache/根特例），故缓存 miss = 节点从未变更 = 引擎内容最新，
    ///   无需脏兜底。顺序：根特例（_cachedRoot）→ 缓存 → 引擎读回+回填。</para>
    /// </summary>
    private BTreeNode ReadNodeCached(LogicalAddress addr)
    {
        if (addr == _rootAddress) return _cachedRoot;   // ★ 根特例：叶根阶段根只存 _cachedRoot

        ref readonly var hit = ref _nodeCache.Find(addr);
        if (!Unsafe.IsNullRef(in hit)) return hit;

        var node = ReadNodeContent(addr);
        _nodeCache.GetOrAdd(addr, in node);
        return node;
    }

    /// <summary>写路径下降取节点（Insert 递归/Delete/扫描）——统一走 <see cref="ReadNodeCached"/>。</summary>
    private BTreeNode GetInternalNode(LogicalAddress addr) => ReadNodeCached(addr);
}
