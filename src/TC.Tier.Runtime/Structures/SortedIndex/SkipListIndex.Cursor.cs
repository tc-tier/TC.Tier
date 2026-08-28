using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>有序遍历游标（层 0 链扫描——range scan 比较族独有能力）。</summary>
    /// <param name="direction">遍历方向（扫描沿层 0 链前向产出条目——direction 由游标 Direction 原样暴露）。</param>
    /// <returns>层 0 链扫描游标。</returns>
    public override IIndexScanCursor<TKey> CreateScanCursor(ReadDirection direction)
        => new SkipListScanCursor(this, direction);

    private sealed unsafe class SkipListScanCursor : IIndexScanCursor<TKey>
    {
        private readonly SkipListIndex<TKey> _index;
        private readonly ReadDirection _direction;
        private LogicalAddress _currentAddr;
        private byte* _current;      // 驻留节点指针（arena 恒稳——游标长命持指安全）
        private bool _disposed;
        private bool _started;

        public ReadDirection Direction => _direction;
        public TKey CurrentKey => ReadKey(_current);
        public LogicalAddress CurrentValue => ReadValue(_current);

        internal SkipListScanCursor(SkipListIndex<TKey> index, ReadDirection direction)
        {
            _index = index;
            _direction = direction;
        }

        public bool MoveNext()
        {
            if (!_started)
            {
                _current = _index._headPtr;
                _started = true;
            }

            _currentAddr = ReadLevel(_current, 0);
            if (_currentAddr == LogicalAddress.Empty) return false;
            _current = _index.GetNode(_currentAddr);
            return true;
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
