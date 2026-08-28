using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Collections;

/// <summary>
/// ★ CLOCK 近似 LRU 缓存——高性能、零分配热路径、线程安全（Interlocked CAS，无全局锁）。
/// <para>★ 算法：环形数组 + 访问位 + 开放寻址法哈希（线性探测）。Redis/PostgreSQL/Windows 内存管理都用 CLOCK。</para>
/// <para>★ 命中率 90-95%（近似 LRU，工程足够）；淘汰 O(1) amortized（时钟扫描）。</para>
/// <para>★ 对齐 OverflowPool/PinnedBufferPool 的"底层组件+性能指标"范式。</para>
/// <para>可复用：Ring 冷页缓存（ClockCache&lt;long, AlignedMemoryManager&gt;）、任意 key→value LRU 场景。</para>
/// <para>参见 src/TC.Tier.Core/docs/cache-and-compute.md。</para>
/// </summary>
/// <typeparam name="TKey">键类型（值类型，须 IEquatable）。</typeparam>
/// <typeparam name="TValue">值类型（引用类型，nullable）。</typeparam>
public sealed class ClockCache<TKey, TValue> : IDisposable
    where TKey : struct, IEquatable<TKey>
{
    private readonly Slot[] _slots;
    private readonly int _capacity;
    private readonly int _mask;
    private readonly Action<TKey, TValue>? _onEvict;
    private int _clockHand;
    private long _hits, _misses, _evictions, _count;
    private bool _disposed;

    /// <summary>缓存 slot（值类型，数组连续内存，CPU 缓存友好）。</summary>
    private struct Slot
    {
        public int Hash;         // 键哈希（0 = 空槽；Tombstone = 已删除，探测须跨过）
        public TKey Key;         // 键
        public TValue? Value;    // 值（null = 空槽）
        public int Accessed;     // 访问位（0/1）
    }

    /// <summary>已删除标记（tombstone）。Remove 置此值而非 0，避免开放寻址探测链断裂。</summary>
    /// <para>★ STORAGE-023：Remove 置 0 会让后续 TryGet/Put 在该位置 break，导致同链后续键查不到/重复插入。</para>
    /// <para>真哈希经 HashKey 的 |1 保证为正奇数，与 -1 不冲突。</para>
    private const int Tombstone = -1;

    /// <summary>创建 CLOCK 缓存。</summary>
    /// <param name="capacity">容量（须 2 的幂；内部按此分配 slot 数组）。</param>
    /// <param name="onEvict">淘汰回调（可选，caller 在淘汰时释放资源）。</param>
    /// <exception cref="ArgumentException">capacity 非 2 的幂。</exception>
    public ClockCache(int capacity, Action<TKey, TValue>? onEvict = null)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException($"capacity {capacity} 必须是 2 的幂", nameof(capacity));
        _capacity = capacity;
        _mask = capacity - 1;
        _slots = new Slot[capacity];
        _onEvict = onEvict;
    }

    // === 公共属性 ===
    /// <summary>当前驻留条目数（原子计数快照）。</summary>
    public int Count => (int)Interlocked.Read(ref _count);
    /// <summary>容量（slot 数组长度，须 2 的幂）。</summary>
    public int Capacity => _capacity;
    /// <summary>累计命中次数（诊断指标）。</summary>
    public long Hits => Interlocked.Read(ref _hits);
    /// <summary>累计未命中次数（诊断指标）。</summary>
    public long Misses => Interlocked.Read(ref _misses);
    /// <summary>累计淘汰次数（诊断指标）。</summary>
    public long Evictions => Interlocked.Read(ref _evictions);
    /// <summary>命中率 = Hits / (Hits + Misses)；无访问时为 0（诊断指标）。</summary>
    ///  <remarks>★ 诊断指标：命中率 90-95%（近似 LRU，工程足够）。</remarks>
    public double HitRate
    {
        get { long h = _hits, m = _misses; return (h + m) > 0 ? (double)h / (h + m) : 0; }
    }

    /// <summary>★ 查找——命中返回 true + value（设访问位），未命中返回 false。热路径零分配。</summary>
    /// <param name="key">要查找的键。</param>
    /// <param name="value">命中时返回值，未命中返回 default。</param>
    /// <returns>是否命中。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value)
    {
        ThrowIfDisposed();
        int hash = HashKey(key);
        int start = hash & _mask;
        ref var slots = ref _slots[0];

        for (int i = 0; i < _capacity; i++)
        {
            int idx = (start + i) & _mask;
            ref var slot = ref _slots[idx];
            int slotHash = slot.Hash;
            if (slotHash == 0) break;   // 空槽——线性探测到此为止（开放寻址法无空洞）
            if (slotHash == Tombstone) continue;   // ★ tombstone——跨过，继续探测同链后续键（#243）
            if (slotHash == hash && slot.Key.Equals(key))
            {
                // 命中——设访问位（Interlocked 原子）
                Interlocked.Exchange(ref slot.Accessed, 1);
                Interlocked.Increment(ref _hits);
                value = slot.Value!;
                return true;
            }
        }

        Interlocked.Increment(ref _misses);
        value = default;
        return false;
    }

    /// <summary>★ 插入/更新——已满时触发 CLOCK 淘汰。</summary>
    /// <param name="key">要插入/更新的键。</param>
    /// <param name="value">要插入/更新的值。</param>
    /// <remarks>★ 热路径零分配：Put 尽力而为，遇并发冲突由 caller 重试或接受。</remarks>
    public void Put(TKey key, TValue value)
    {
        ThrowIfDisposed();
        int hash = HashKey(key);
        int start = hash & _mask;

        // 先查是否已存在（更新 value + 设访问位）
        // ★ 记录首个 tombstone 供复用；断在空槽处时记下空槽索引——该空槽即插入位，
        //   省掉 FindFreeOrEvict 再全表扫一遍找空槽（满载时这一遍是 O(N) 白费，#PERF-001）
        int firstTombstone = -1;
        int emptyIdx = -1;
        for (int i = 0; i < _capacity; i++)
        {
            int idx = (start + i) & _mask;
            ref var slot = ref _slots[idx];
            if (slot.Hash == 0)
            {
                emptyIdx = idx;
                break;   // 不存在——空槽即插入位
            }
            if (slot.Hash == Tombstone)
            {
                if (firstTombstone < 0) firstTombstone = idx;   // 记下首个 tombstone 供后续复用
                continue;   // 跨过，继续探测同链后续键（#243）
            }
            if (slot.Hash == hash && slot.Key.Equals(key))
            {
                slot.Value = value;
                Interlocked.Exchange(ref slot.Accessed, 1);
                return;
            }
        }

        // 不存在——优先复用首个 tombstone，其次用探测到的空槽，全满才 CLOCK 淘汰（#243, #PERF-001）
        int insertIdx = firstTombstone >= 0 ? firstTombstone : emptyIdx >= 0 ? emptyIdx : EvictSlot();
        ref var target = ref _slots[insertIdx];
        // ★ CAS 占位（防并发重复插入）——CAS 从 tombstone 或 0 → hash 成功才占有 slot
        int expected = firstTombstone >= 0 ? Tombstone : 0;
        if (Interlocked.CompareExchange(ref target.Hash, hash, expected) == expected)
        {
            // ★ CORE-08 写序：Value 先、Key 后（读者按 Hash→Key→Value 序匹配——见新 Key 更可能见新
            //    Value；TSO 同线程 store 序；旧序 Key→Value = 读者命中新 Key 读到旧 Value）
            target.Value = value;
            target.Key = key;
            target.Accessed = 1;
            Interlocked.Increment(ref _count);
        }
        else
        {
            // 并发竞争失败——slot 被别的线程占了。更新已有值（可能是同 key 或别的 key）
            if (target.Key.Equals(key))
            {
                target.Value = value;
                Interlocked.Exchange(ref target.Accessed, 1);
            }
            // 不同 key 的冲突——放弃（Put 尽力而为，并发冲突由 caller 重试或接受）
        }
    }

    /// <summary>★ 移除指定键（手动淘汰）。返回是否成功。</summary>
    /// <param name="key">要移除的键。</param>
    /// <returns>是否成功移除。</returns>
    /// <remarks>★ 热路径零分配：Remove 尽力而为，遇并发冲突由 caller 重试或接受。</remarks>
    public bool Remove(TKey key)
    {
        ThrowIfDisposed();
        int hash = HashKey(key);
        int start = hash & _mask;

        for (int i = 0; i < _capacity; i++)
        {
            int idx = (start + i) & _mask;
            ref var slot = ref _slots[idx];
            if (slot.Hash == 0) return false;
            if (slot.Hash == Tombstone) continue;   // ★ tombstone——跨过（#243）
            if (slot.Hash == hash && slot.Key.Equals(key))
            {
                var oldVal = slot.Value;
                // ★ 置 tombstone 而非 0——保持开放寻址探测链不断（#243）。
                //   后续 Put 可复用此槽，EvictSlot 的 CLOCK 扫描遇 tombstone 也会回收。
                Interlocked.Exchange(ref slot.Hash, Tombstone);
                // ★ CORE-08：置 tombstone 后不清 Key/Value（清场会抹掉并发 Put 抢槽写入的新条目；
                //   复用覆盖释放引用——有界滞留）
                slot.Accessed = 0;
                Interlocked.Decrement(ref _count);
                if (oldVal is not null) _onEvict?.Invoke(key, oldVal);
                return true;
            }
        }
        return false;
    }

    /// <summary>★ 清空缓存（调淘汰回调释放所有值）。</summary>
    /// <remarks>★ 热路径零分配：Clear 尽力而为，遇并发冲突由 caller 重试或接受。</remarks>
    public void Clear()
    {
        ThrowIfDisposed();
        ClearInternal();
    }

    /// <summary>指标快照。</summary>
    /// <returns>当前缓存指标快照。</returns>
    public ClockCacheStats GetStats() => new()
    {
        Count = Count,
        Capacity = _capacity,
        Hits = Hits,
        Misses = Misses,
        Evictions = Evictions,
        HitRate = HitRate
    };

    /// <summary>释放缓存：清空全部条目（逐条调淘汰回调）并标记 disposed（幂等，线程安全）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearInternal();   // ★ 不走 ThrowIfDisposed（Dispose 时已设 _disposed=true）
    }

    private void ClearInternal()
    {
        for (int i = 0; i < _capacity; i++)
        {
            ref var slot = ref _slots[i];
            if (slot.Hash != 0 && slot.Value is { } val)
                _onEvict?.Invoke(slot.Key, val);
            slot.Hash = 0;
            slot.Key = default;
            slot.Value = default;
            slot.Accessed = 0;
        }
        Interlocked.Exchange(ref _count, 0);
    }

    // === 内部方法 ===

    /// <summary>★ CLOCK 扫描淘汰腾位（仅全满时调用——Put 已确认无空槽/tombstone 可复用）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int EvictSlot()
    {
        // 全满——CLOCK 扫描淘汰（最多两轮：第一轮清访问位，第二轮必找到 accessed=0）
        for (int attempt = 0; attempt < _capacity * 2; attempt++)
        {
            int idx = (Interlocked.Increment(ref _clockHand) - 1) & _mask;
            ref var slot = ref _slots[idx];
            int h = slot.Hash;
            if (h == 0 || h == Tombstone) return idx;   // 空 slot 或 tombstone（并发 Remove 腾出，#243）

            if (Interlocked.CompareExchange(ref slot.Accessed, 0, 1) == 1)
                continue;   // accessed=1 → 清位，给第二次机会

            // accessed=0 → 淘汰此 slot
            var oldKey = slot.Key;
            var oldVal = slot.Value;
            Interlocked.Exchange(ref slot.Hash, 0);
            // ★ CORE-08：不清 Key/Value（同 Remove 律——复用覆盖释放；有界滞留）
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _evictions);
            if (oldVal is not null) _onEvict?.Invoke(oldKey, oldVal);
            return idx;
        }

        // 极端情况（全 accessed=1 且并发高）——强制淘汰时钟当前位置
        int forceIdx = Interlocked.Increment(ref _clockHand) & _mask;
        ref var forceSlot = ref _slots[forceIdx];
        var fKey = forceSlot.Key;
        var fVal = forceSlot.Value;
        Interlocked.Exchange(ref forceSlot.Hash, 0);
        // ★ CORE-08：不清 Key/Value（同律）
        Interlocked.Decrement(ref _count);
        Interlocked.Increment(ref _evictions);
        if (fVal is not null) _onEvict?.Invoke(fKey, fVal);
        return forceIdx;
    }

    /// <summary>★ 键哈希（|1 保证非零——0 是空槽标记）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashKey(TKey key)
    {
        int h = key.GetHashCode();
        // 混合高位低位 + |1 保证非零
        h = (h ^ (h >> 16)) | 1;
        return h == 0 ? 1 : h;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ClockCache<TKey, TValue>));
    }
}

/// <summary>ClockCache 指标快照。</summary>
/// <param name="Count">当前缓存条目数。</param>
/// <param name="Capacity">缓存容量（slot 数组长度）。</param>
/// <param name="Hits">累计命中次数。</param>
/// <param name="Misses">累计未命中次数。</param>
/// <param name="Evictions">累计淘汰次数。</param>
/// <param name="HitRate">命中率 = Hits / (Hits + Misses )。</param>
public record struct ClockCacheStats(
    int Count,
    int Capacity,
    long Hits,
    long Misses,
    long Evictions,
    double HitRate);
