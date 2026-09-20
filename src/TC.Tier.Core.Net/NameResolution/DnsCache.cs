namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// TTL 缓存（internal——#498 诉求 4）：正/负统一形态（负 = NxDomain 空应答，TTL 由调用方传负缓存值）；
/// LRU 容量有界；键 = (类型, 名) 复合、名 OrdinalIgnoreCase 归一（DNS 名大小写不敏感——应答字节不扰动）。
/// <para>★ 过期惰性剔除（读时判 TTL）；Tick 毫秒基准（单调钟——系统对时不影响有效期）。</para>
/// </summary>
internal sealed class DnsCache
{
    /// <summary>条目（不可变应答 + 绝对过期时刻）。</summary>
    private readonly record struct Entry(DnsAnswer Answer, long ExpiresAtMs);

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _lru = new();

    /// <summary>构造。</summary>
    /// <param name="capacity">条目上限（0 = 关闭——TryGet 恒 miss、Put 恒丢弃）。</param>
    public DnsCache(int capacity) => _capacity = capacity;

    /// <summary>当前条目数（诊断）。</summary>
    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    /// <summary>取（命中即 LRU 置顶；过期惰性剔除返 miss）。</summary>
    public bool TryGet(string name, DnsRecordType type, out DnsAnswer answer)
    {
        answer = DnsAnswer.Empty(name, type, TimeSpan.Zero,
            new DnsSecurityStatus(DnsResponseCode.NoError, false, false));
        if (_capacity <= 0) return false;
        var key = CompositeKey(name, type);

        var now = Environment.TickCount64;
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var node)) return false;
            if (node.Value.ExpiresAtMs <= now)
            {
                _map.Remove(key);
                _lru.Remove(node);
                return false;
            }
            _lru.Remove(node);
            _lru.AddFirst(node);
            answer = node.Value.Answer;
            return true;
        }
    }

    /// <summary>放（存在则原位更新 + 置顶；超容量淘汰 LRU 尾）。</summary>
    public void Put(string name, DnsRecordType type, DnsAnswer answer, TimeSpan ttl)
    {
        if (_capacity <= 0 || ttl <= TimeSpan.Zero) return;
        var key = CompositeKey(name, type);

        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(answer, Environment.TickCount64 + (long)ttl.TotalMilliseconds);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }
            while (_map.Count >= _capacity && _lru.Last is { } tail)
            {
                _map.Remove(KeyOf(tail.Value));
                _lru.Remove(tail);
            }
            var node = new LinkedListNode<Entry>(new Entry(
                answer, Environment.TickCount64 + (long)ttl.TotalMilliseconds));
            _lru.AddFirst(node);
            _map[key] = node;
        }
    }

    /// <summary>复合键（类型在前的字符串形态——OrdinalIgnoreCase 由字典侧统一）。</summary>
    private static string CompositeKey(string name, DnsRecordType type) => $"{(ushort)type}:{name}";

    /// <summary>淘汰时从条目反解键（Entry 不存键——复合同一形态重算）。</summary>
    private static string KeyOf(Entry entry)
        => CompositeKey(entry.Answer.Name, entry.Answer.Type);
}
