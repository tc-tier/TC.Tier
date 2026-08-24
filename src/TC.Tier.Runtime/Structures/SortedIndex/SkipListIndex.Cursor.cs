using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
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
