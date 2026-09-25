using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// SkipListIndex 游标 partial——<see cref="CreateScanCursor"/> 与 <see cref="SkipListScanCursor"/>
/// （层 0 链前向推进 + lower_bound 塔链定位）。
/// </summary>
public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// 有序遍历游标（层 0 链前向扫描——range scan 比较族独有能力）。
    /// <para>★ 方向契约（诚实化）：仅 Forward——层 0 链单向，Backward 显式抛
    /// <see cref="NotSupportedException"/>（旧形接受参数但恒前向产出=谎报契约）。倒序/末位需求经
    /// <see cref="TryGetMax"/>/<see cref="TryGetFloor"/> 组合表达。</para>
    /// </summary>
    /// <param name="direction">遍历方向（仅 Forward）。</param>
    /// <returns>层 0 链扫描游标。</returns>
    public override IIndexScanCursor<TKey> CreateScanCursor(ReadDirection direction)
    {
        if (direction != ReadDirection.Forward)
            throw new NotSupportedException($"SkipListIndex 游标仅支持 Forward（层 0 链单向）——direction={direction}；倒序经 TryGetMax + TryGetPrev 反向步进（TryGetPrev 原语）");
        return new SkipListScanCursor(this, direction);
    }

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

        /// <summary>推进到下一条目（操作闸单步粒度——与写者交错）。首次调用从头哨兵起步。</summary>
        /// <returns>true = 已推进到下一条目（CurrentKey/CurrentValue 有效）；false = 已到扫描末尾（层 0 链耗尽）。</returns>
        public bool MoveNext()
        {
            using var _ = _index.EnterOp();   // ★ 操作闸（单步粒度——长扫描与写者交错）
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

        /// <summary>
        /// 定位到首个 key ≥ <paramref name="key"/> 的条目（lower_bound）——与 FindNoEpoch 同形
        /// 塔链下降到前驱，游标直接置于首个 ≥ key 的节点（Current* 即该条目，
        /// MoveNext 产出其后续——与 BTree 游标同契约）。
        /// </summary>
        /// <param name="key">定位键（lower_bound——首个 ≥ key 的条目）。</param>
        /// <returns>true = 已定位到首个 ≥ key 的条目；false = 无此条目（游标置于末尾，MoveNext 恒 false）。</returns>
        public bool SeekLowerBound(TKey key)
        {
            using var _ = _index.EnterOp();   // ★ 操作闸（定位=单步）
            var current = _index._headPtr;
            for (int i = _index._currentLevel - 1; i >= 0; i--)
            {
                var nextAddr = ReadLevel(current, i);
                while (nextAddr != LogicalAddress.Empty)
                {
                    var next = _index.GetNode(nextAddr);
                    if (_index.KeyComparer.Compare(ReadKey(next), key) < 0)
                    {
                        current = next;
                        nextAddr = ReadLevel(current, i);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            _started = true;
            var succAddr = ReadLevel(current, 0);
            if (succAddr == LogicalAddress.Empty)
            {
                // 无 ≥ key 条目：current = 最后一个节点（或 head），置末尾——MoveNext 恒 false
                _current = current;
                _currentAddr = LogicalAddress.Empty;
                return false;
            }
            _current = _index.GetNode(succAddr);
            _currentAddr = succAddr;
            return true;
        }

        /// <summary>异步推进——无真异步 IO，同步委托 <see cref="MoveNext"/>。</summary>
        /// <param name="cancellationToken">取消令牌（当前实现未消费）。默认 <c>default</c>。</param>
        /// <returns>完成后结果同 <see cref="MoveNext"/>：true = 已推进到下一条目；false = 已到扫描末尾。</returns>
        public ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(MoveNext());

        /// <summary>释放游标（幂等——不持有页资源，仅置标记）。</summary>
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
