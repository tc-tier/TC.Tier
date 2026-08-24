using System.Collections.Concurrent;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// ★ 对照基准：ConcurrentDictionary + LinkedList 经典 LRU 实现。
/// <para>用于和 ClockCache 做性能对比——证明自研 CLOCK 算法是否优于 ConcurrentDictionary 方案。</para>
/// <para>★ 线程安全用 lock（LinkedList.Remove + AddFirst 非原子，必须全局锁）。</para>
/// </summary>
internal sealed class ConcurrentLruCache<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, LinkedListNode<LruItem>> _map;
    private readonly LinkedList<LruItem> _list = new();
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Action<TKey, TValue>? _onEvict;

    private readonly struct LruItem(TKey key, TValue value)
    {
        public readonly TKey Key = key;
        public readonly TValue Value = value;
    }

    public ConcurrentLruCache(int capacity, Action<TKey, TValue>? onEvict = null)
    {
        _capacity = capacity;
        _onEvict = onEvict;
        _map = new(Environment.ProcessorCount, capacity);
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // ★ 经典 LRU：命中后移到链表头部（Remove + AddFirst）——两次指针操作
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default;
            return false;
        }
    }

    public void Put(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _list.Remove(existing);
                var node = _list.AddFirst(new LruItem(key, value));
                _map[key] = node;
                return;
            }

            while (_list.Count >= _capacity)
            {
                var last = _list.Last!;
                _list.RemoveLast();
                _map.TryRemove(last.Value.Key, out _);
                if (last.Value.Value is not null)
                    _onEvict?.Invoke(last.Value.Key, last.Value.Value);
            }

            var newNode = _list.AddFirst(new LruItem(key, value));
            _map[key] = newNode;
        }
    }

    public int Count
    {
        get { lock (_lock) return _list.Count; }
    }

    public void Dispose()
    {
        lock (_lock) _list.Clear();
        _map.Clear();
    }
}
