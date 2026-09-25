using TC.Tier.Runtime.Structures.SortedIndex.Contracts;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// BTreeIndex 游标 partial——<see cref="CreateScanCursor"/> 与 <see cref="BTreeScanCursor"/>
/// （最左叶定位 + 叶链 Next 前向推进 + lower_bound 定位）。
/// </summary>
public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// 有序遍历游标（最左叶子起叶链 Next 前向扫描——range scan 比较族独有能力）。
    /// <para>★ 方向契约（诚实化）：仅 Forward——叶链无 Prev 指针，Backward 显式抛
    /// <see cref="NotSupportedException"/>（旧形接受参数但恒前向产出=谎报契约）。倒序/末位需求经
    /// <see cref="TryGetMax"/>/<see cref="TryGetFloor"/> 组合表达；真倒序遍历待 Prev 指针格式演进。</para>
    /// </summary>
    /// <param name="direction">遍历方向（仅 Forward）。</param>
    /// <returns>叶链扫描游标。</returns>
    public override IIndexScanCursor<TKey> CreateScanCursor(ReadDirection direction)
    {
        if (direction != ReadDirection.Forward)
            throw new NotSupportedException($"BTreeIndex 游标仅支持 Forward（叶链无 Prev 指针）——direction={direction}；倒序经 TryGetMax + TryGetPrev 反向步进（TryGetPrev 原语）");
        return new BTreeScanCursor(this, direction);
    }

    /// <summary>全树条目计数（物化重数实收用——递归遍历）。</summary>
    private long CountEntries()
    {
        long count = 0;
        CountEntries(_rootAddress, ref count);
        return count;
    }

    private void CountEntries(LogicalAddress addr, ref long count)
    {
        if (addr == LogicalAddress.Empty) return;
        // ★ 缓存优先读（脏节点延迟写回后引擎副本可能旧——遍历必须走驻留缓存，miss 引擎读回+回填）
        var node = GetInternalNode(addr);

        if (node.IsLeaf)
        {
            count += node.Count;
        }
        else
        {
            for (int i = 0; i <= node.Count; i++)
                CountEntries(node.GetValue(i), ref count);
        }
    }

    private sealed class BTreeScanCursor : IIndexScanCursor<TKey>
    {
        private readonly BTreeIndex<TKey> _index;
        private readonly ReadDirection _direction;
        private LogicalAddress _currentLeaf;
        private int _currentEntry;
        private BTreeNode _currentNode;
        private bool _disposed;
        private bool _started;

        public ReadDirection Direction => _direction;
        public TKey CurrentKey => _currentNode.GetKey(_currentEntry);
        public LogicalAddress CurrentValue => _currentNode.GetValue(_currentEntry);

        internal BTreeScanCursor(BTreeIndex<TKey> index, ReadDirection direction)
        {
            _index = index;
            _direction = direction;
            _currentLeaf = LogicalAddress.Empty;
            _currentEntry = -1;
        }

        /// <summary>推进到下一条目（操作闸单步粒度——与写者交错）。首次调用定位最左叶首条目。</summary>
        /// <returns>true = 已定位到下一条目（CurrentKey/CurrentValue 有效）；false = 已到扫描末尾（叶链耗尽）。</returns>
        public bool MoveNext()
        {
            using var _ = _index.EnterOp();   // ★ 操作闸（单步粒度——长扫描与写者交错）
            if (!_started)
            {
                if (_index._rootAddress == LogicalAddress.Empty) return false;
                _currentLeaf = FindLeftmostLeaf(_index._rootAddress);
                if (_currentLeaf == LogicalAddress.Empty) return false;
                // ★ 缓存优先读（脏节点延迟写回后引擎副本可能旧——扫描走驻留缓存，miss 引擎读回+回填）
                _currentNode = _index.GetInternalNode(_currentLeaf);
                _currentEntry = 0;
                _started = true;
                if (_currentNode.Count == 0) return AdvanceToNonEmptyLeaf();
                return true;
            }

            _currentEntry++;
            if (_currentEntry >= _currentNode.Count)
                return AdvanceToNonEmptyLeaf();
            return true;
        }

        /// <summary>
        /// 越过条目耗尽的叶推进（沿叶链 Next）——跳过空叶（删除不重平衡可留 Count=0 的叶，
        /// 空叶不是扫描终点）。定位到首个非空叶的条目 0；链尽返回 false。
        /// </summary>
        private bool AdvanceToNonEmptyLeaf()
        {
            while (_currentNode.Count == 0 || _currentEntry >= _currentNode.Count)
            {
                if (_currentNode.Next == LogicalAddress.Empty) return false;
                _currentLeaf = _currentNode.Next;
                _currentNode = _index.GetInternalNode(_currentLeaf);
                _currentEntry = 0;
            }
            return true;
        }

        /// <summary>
        /// 定位到首个 key ≥ <paramref name="key"/> 的条目（lower_bound）——与 Find 同形下降
        /// （缓存优先）到 key 的归属叶，叶内线性定首键位（叶 ≤9 键）；归属叶耗尽则沿叶链推进
        /// （跨空叶）到首个含 ≥ key 条目的叶。
        /// </summary>
        /// <param name="key">定位键（lower_bound——首个 ≥ key 的条目）。</param>
        /// <returns>true = 已定位到首个 ≥ key 的条目；false = 无此条目（空树/键超过全部条目/环防御降级），游标置于末尾。</returns>
        public bool SeekLowerBound(TKey key)
        {
            using var _ = _index.EnterOp();   // ★ 操作闸（定位=单步）
            if (_index._rootAddress == LogicalAddress.Empty)
            {
                // 空树：置于末尾（后续 MoveNext 恒 false）
                _started = true;
                _currentNode = default;
                _currentEntry = -1;
                _currentLeaf = LogicalAddress.Empty;
                return false;
            }

            // 下降（与 FindNoEpoch 同形：首个 i 使 key < nodeKey[i] → 子 i）——深度上限环防御
            var node = _index._cachedRoot;
            int guard = 0;
            while (!node.IsLeaf)
            {
                if (++guard > MaxDescentPath) return false;   // 环防御——坏路径降级为 miss
                int i;
                for (i = 0; i < node.Count; i++)
                {
                    if (_index.KeyComparer.Compare(key, node.GetKey(i)) < 0) break;
                }
                node = _index.GetInternalNode(node.GetValue(i));
            }
            _currentLeaf = LogicalAddress.Empty;   // 叶地址仅诊断用（游标推进走 Node.Next）
            _currentNode = node;

            // 叶内 lower_bound（首个 ≥ key 的槽位）
            int pos = 0;
            while (pos < node.Count && _index.KeyComparer.Compare(node.GetKey(pos), key) < 0)
                pos++;
            _currentEntry = pos;
            _started = true;

            if (pos >= node.Count)
                return AdvanceToNonEmptyLeaf();   // 归属叶全 < key（键隙内）或空叶 → 沿链推进
            return true;
        }

        private LogicalAddress FindLeftmostLeaf(LogicalAddress addr)
        {
            if (addr == LogicalAddress.Empty) return LogicalAddress.Empty;
            var node = _index.GetInternalNode(addr);
            int guard = 0;
            while (!node.IsLeaf)
            {
                if (++guard > MaxDescentPath) return LogicalAddress.Empty;   // 环防御
                addr = node.GetValue(0);
                if (addr == LogicalAddress.Empty) return LogicalAddress.Empty;
                node = _index.GetInternalNode(addr);
            }
            return addr;
        }

        /// <summary>异步推进——无真异步 IO，同步委托 <see cref="MoveNext"/>。</summary>
        /// <param name="cancellationToken">取消令牌（当前实现未消费）。默认 <c>default</c>。</param>
        /// <returns>完成后结果同 <see cref="MoveNext"/>：true = 已推进到下一条目；false = 已到扫描末尾。</returns>
        public ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(MoveNext());

        /// <summary>释放游标（幂等——游标不持有页资源，仅置标记）。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        /// <summary>异步释放游标（同步完成，语义同 <see cref="Dispose"/>）。</summary>
        /// <returns>完成后游标已释放。</returns>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
