using TC.Tier.Runtime.Structures.SortedIndex.Contracts;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>有序遍历游标（最左叶子起叶链 Next 扫描——range scan 比较族独有能力）。</summary>
    /// <param name="direction">遍历方向（扫描沿叶链前向产出条目——direction 由游标 Direction 原样暴露）。</param>
    /// <returns>叶链扫描游标。</returns>
    public override IIndexScanCursor<TKey> CreateScanCursor(ReadDirection direction)
        => new BTreeScanCursor(this, direction);

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

        public bool MoveNext()
        {
            if (!_started)
            {
                if (_index._rootAddress == LogicalAddress.Empty) return false;
                _currentLeaf = FindLeftmostLeaf(_index._rootAddress);
                if (_currentLeaf == LogicalAddress.Empty) return false;
                // ★ 缓存优先读（脏节点延迟写回后引擎副本可能旧——扫描走驻留缓存，miss 引擎读回+回填）
                _currentNode = _index.GetInternalNode(_currentLeaf);
                _currentEntry = 0;
                _started = true;
                return _currentNode.Count > 0;
            }

            _currentEntry++;
            if (_currentEntry >= _currentNode.Count)
            {
                if (_currentNode.Next == LogicalAddress.Empty) return false;
                _currentLeaf = _currentNode.Next;
                _currentNode = _index.GetInternalNode(_currentLeaf);
                _currentEntry = 0;
                return _currentNode.Count > 0;
            }

            return true;
        }

        private LogicalAddress FindLeftmostLeaf(LogicalAddress addr)
        {
            if (addr == LogicalAddress.Empty) return LogicalAddress.Empty;
            var node = _index.GetInternalNode(addr);
            while (!node.IsLeaf)
            {
                addr = node.GetValue(0);
                if (addr == LogicalAddress.Empty) return LogicalAddress.Empty;
                node = _index.GetInternalNode(addr);
            }
            return addr;
        }

        public ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(MoveNext());

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
