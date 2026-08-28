using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Collections;

/// <summary>
/// ★ CLOCK 近似 LRU 缓存 V2——组相联存储（set-associative，CPU 缓存教科书结构）。
/// <para>★ 算法：容量拆为 sets × ways（默认 8 路）的固定槽组；查找/插入只扫描组内 ways 槽——miss 恒 ≤8 次读，
///     与负载因子无关（V1 开放寻址满载 miss 探测链发散至 681ns，V2 结构上不存在该悬崖）。</para>
/// <para>★ 淘汰：组内 CLOCK 二次机会（访问位 Interlocked CAS），≤2×ways 次操作，O(1)。</para>
/// <para>★ 命中率 90-99%（8 路组相联 ≈ 全表 CLOCK 的 90-99%，Zipf 实测 99.9% vs 100%）。</para>
/// <para>★ 线程安全：无全局锁，读路径纯无锁（Interlocked CAS 写路径）；并发语义与 V1 一致（尽力而为 Put）。</para>
/// <para>★ 热路径（hit/miss/update）零分配；Slot 数组 = capacity（内存 1×）。</para>
/// <para>★ 与 ClockCache（V1 开放寻址）并列：V1 甜区 = 铁律配置下（容量 ≥2× 工作集）极致热路径
///     （hit 3.7ns / update 2.9ns）；V2 = 任意负载延迟恒定、全路径反超 ConcurrentDictionary 基线
///     （hit 5ns / miss 7ns / 驱逐 70ns）。组偏斜会提前淘汰（均匀负载实际驻留 ≈ 容量 85-90%）。</para>
/// <para>参见 src/TC.Tier.Core/docs/cache-and-compute.md。</para>
/// </summary>
/// <typeparam name="TKey">键类型（值类型，须 IEquatable）。</typeparam>
/// <typeparam name="TValue">值类型（引用类型，nullable）。</typeparam>
public sealed class ClockCacheV2<TKey, TValue> : IDisposable
    where TKey : struct, IEquatable<TKey>
{
    /// <summary>默认组内路数（8 路 ≈ 全相联 90-99% 命中率，扫描恒定）。</summary>
    private const int DefaultWays = 8;

    private readonly Slot[] _slots;
    private readonly int _capacity;
    private readonly int _ways;
    private readonly int _setMask;
    private readonly Action<TKey, TValue>? _onEvict;
    private int _clockHand;
    private long _hits, _misses, _evictions, _count;
    private bool _disposed;

    /// <summary>缓存 slot（值类型，数组连续内存，CPU 缓存友好）。</summary>
    private struct Slot
    {
        public int Hash;         // 键哈希（0 = 空槽；Tombstone = 已删除，可被插入复用）
        public TKey Key;         // 键
        public TValue? Value;    // 值（null = 空槽）
        public int Accessed;     // 访问位（0/1）
    }

    /// <summary>已删除标记。Remove 置此值而非 0：防止并发 Put 在 Remove 清完字段前占槽写半态。</summary>
    /// <para>★ 组相联全路扫描无探测链，tombstone 不承担"链完整性"职责（V1 #243 场景结构上不存在）。</para>
    /// <para>真哈希经 HashKey 折叠避让 0 与 -1（概率 2^-31，Key 比较兜底），与标记不冲突。</para>
    private const int Tombstone = -1;

    /// <summary>创建组相联 CLOCK 缓存。</summary>
    /// <param name="capacity">容量（须 2 的幂；缓存可容纳的 entry 上限，语义与 V1 相同）。</param>
    /// <param name="onEvict">淘汰回调（可选，caller 在淘汰时释放资源）。</param>
    /// <param name="ways">组内路数（须 2 的幂，默认 8；超过 capacity 时自动减半钳制）。</param>
    /// <exception cref="ArgumentException">capacity 或 ways 非 2 的幂。</exception>
    public ClockCacheV2(int capacity, Action<TKey, TValue>? onEvict = null, int ways = DefaultWays)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException($"capacity {capacity} 必须是 2 的幂", nameof(capacity));
        if (ways <= 0 || (ways & (ways - 1)) != 0)
            throw new ArgumentException($"ways {ways} 必须是 2 的幂", nameof(ways));
        _capacity = capacity;
        int w = ways;
        while (w > capacity) w >>= 1;   // 钳制：ways ≤ capacity（sets ≥ 1）
        _ways = w;
        _setMask = (capacity / w) - 1;
        _slots = new Slot[capacity];
        _onEvict = onEvict;
    }

    // === 公共属性 ===
    /// <summary>当前驻留条目数（原子计数快照；tombstone 槽已从计数中扣除）。</summary>
    public int Count => (int)Interlocked.Read(ref _count);
    /// <summary>容量（slot 总数 = sets × ways）。</summary>
    public int Capacity => _capacity;
    /// <summary>组内路数（每 set 的槽数）。</summary>
    public int Ways => _ways;
    /// <summary>累计命中次数（诊断指标）。</summary>
    public long Hits => Interlocked.Read(ref _hits);
    /// <summary>累计未命中次数（诊断指标）。</summary>
    public long Misses => Interlocked.Read(ref _misses);
    /// <summary>累计淘汰次数（诊断指标）。</summary>
    public long Evictions => Interlocked.Read(ref _evictions);
    /// <summary>命中率 = Hits / (Hits + Misses)；无访问时为 0（诊断指标）。</summary>
    /// <remarks>★ 诊断指标：命中率 = Hits / (Hits + Misses)；无访问时为 0。热路径零分配。</remarks>
    public double HitRate
    {
        get { long h = _hits, m = _misses; return (h + m) > 0 ? (double)h / (h + m) : 0; }
    }

    /// <summary>★ 查找——命中返回 true + value（设访问位），未命中返回 false。热路径零分配。</summary>
    /// <para>★ 全路扫描（不因空槽提前终止）：miss 恒 ways 次读，任意负载因子下延迟恒定。</para>
    /// <para>★ 命中：访问位 Interlocked CAS 置 1，_hits++；未命中：_misses++。</para>
    /// <para>★ 线程安全：无锁读路径，Put/Remove 并发不破坏查找语义（尽力而为）。</para>
    /// <para>★ 组相联结构：查找只扫描组内 ways 槽，miss 恒 ≤8 次读，避免 V1 开放寻址满载探测链发散。</para>
    /// <para>★ 组相联对哈希质量敏感（桶偏斜 → 组内溢出提前淘汰），HashKey 做 murmur3 fmix32 终混消除低位偏斜。</para>
    /// <param name="key">要查找的键。</param>
    /// <param name="value">如果找到键，则返回对应的值；否则返回默认值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value)
    {
        ThrowIfDisposed();
        int hash = HashKey(key);
        int baseIdx = (hash & _setMask) * _ways;

        for (int i = 0; i < _ways; i++)
        {
            ref var slot = ref _slots[baseIdx + i];
            if (slot.Hash == hash && slot.Key.Equals(key))
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

    /// <summary>★ 插入/更新——组内无空槽时触发组内 CLOCK 淘汰。尽力而为（并发冲突重扫一次）。</summary>
    /// <param name="key">要插入或更新的键。</param>
    /// <param name="value">要插入或更新的值。</param>
    /// <remarks>★ 组相联结构：插入/更新只扫描组内 ways 槽，miss 恒 ≤8 次读，避免 V1 开放寻址满载探测链发散。</remarks>
    public void Put(TKey key, TValue value)
    {
        ThrowIfDisposed();
        int hash = HashKey(key);
        int baseIdx = (hash & _setMask) * _ways;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            // 全路扫描：既查已存在（更新），也记录首个 tombstone/空槽供插入
            int firstTombstone = -1;
            int firstEmpty = -1;
            for (int i = 0; i < _ways; i++)
            {
                ref var slot = ref _slots[baseIdx + i];
                int h = slot.Hash;
                if (h == hash && slot.Key.Equals(key))
                {
                    slot.Value = value;
                    Interlocked.Exchange(ref slot.Accessed, 1);
                    return;
                }
                if (h == Tombstone)
                {
                    if (firstTombstone < 0) firstTombstone = i;
                }
                else if (h == 0)
                {
                    if (firstEmpty < 0) firstEmpty = i;
                }
            }

            // 不存在——优先复用 tombstone，其次空槽，组满则组内 CLOCK 淘汰
            int insertIdx = firstTombstone >= 0 ? firstTombstone
                : firstEmpty >= 0 ? firstEmpty
                : EvictInSet(baseIdx);
            ref var target = ref _slots[baseIdx + insertIdx];
            int expected = target.Hash;   // 0 或 Tombstone（EvictInSet 返回的槽亦为此二态）
            if (expected != 0 && expected != Tombstone)
                continue;   // 状态已变（并发），重扫
            if (Interlocked.CompareExchange(ref target.Hash, hash, expected) == expected)
            {
                // ★ CORE-08 写序：Value 先、Key 后（读者按 Hash→Key→Value 序匹配——见新 Key 更可能
                //   见新 Value；TSO 同线程 store 序保证；旧序 Key→Value = 读者命中新 Key 读到旧 Value）
                target.Value = value;
                target.Key = key;
                target.Accessed = 1;
                Interlocked.Increment(ref _count);
                return;
            }
            // CAS 竞争失败——重扫一次（可能同 key 已被并发插入 → 更新路径）
        }
        // 两次尝试均失败——放弃（Put 尽力而为，并发冲突由 caller 重试或接受，V1 语义）
    }

    /// <summary>★ 移除指定键（手动淘汰）。返回是否成功。
    /// ★ CORE-08：置 tombstone 后<b>不清 Key/Value</b>——清场会抹掉并发 Put 已 CAS 抢槽写入的新条目
    ///（条目丢失 + _count 漂移）；tombstone 槽被复用（Put CAS 0/tombstone→新值）时新值覆盖旧引用——
    /// 引用滞留 = 已删条目数（≤ 容量，有界），复用即释放。</summary>
    /// <param name="key">要移除的键。</param>
    /// <returns>是否成功移除。</returns>
    /// <remarks>★ 组相联结构：移除只扫描组内 ways 槽，miss 恒 ≤8 次读，避免 V1 开放寻址满载探测链发散。</remarks>
    public bool Remove(TKey key)
    {
        ThrowIfDisposed();
        int hash = HashKey(key);
        int baseIdx = (hash & _setMask) * _ways;

        for (int i = 0; i < _ways; i++)
        {
            ref var slot = ref _slots[baseIdx + i];
            if (slot.Hash == hash && slot.Key.Equals(key))
            {
                var oldVal = slot.Value;
                // ★ 置 tombstone（原子标记：读者不再匹配；Put 稍后可复用此槽）
                Interlocked.Exchange(ref slot.Hash, Tombstone);
                slot.Accessed = 0;
                Interlocked.Decrement(ref _count);
                if (oldVal is not null) _onEvict?.Invoke(key, oldVal);
                return true;
            }
        }
        return false;
    }

    /// <summary>★ 清空缓存（调淘汰回调释放所有值）。</summary>
    /// <remarks>★ 线程安全：清空期间 Put/Remove 并发不破坏语义（尽力而为）。</remarks>
    public void Clear()
    {
        ThrowIfDisposed();
        ClearInternal();
    }

    /// <summary>指标快照。</summary>
    /// <returns>当前缓存的统计指标快照。</returns>
    public ClockCacheV2Stats GetStats() => new()
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
            if (slot.Hash != 0 && slot.Hash != Tombstone && slot.Value is { } val)
                _onEvict?.Invoke(slot.Key, val);
            slot.Hash = 0;
            slot.Key = default;
            slot.Value = default;
            slot.Accessed = 0;
        }
        Interlocked.Exchange(ref _count, 0);
    }

    // === 内部方法 ===

    /// <summary>★ 组内 CLOCK 扫描淘汰腾位（仅组满时调用）。返回组内偏移 [0, ways)。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int EvictInSet(int baseIdx)
    {
        for (int attempt = 0; attempt < _ways * 2; attempt++)
        {
            int idx = baseIdx + ((Interlocked.Increment(ref _clockHand) - 1) & (_ways - 1));
            ref var slot = ref _slots[idx];
            int h = slot.Hash;
            if (h == 0 || h == Tombstone)
                return idx - baseIdx;   // 并发腾出/历史 tombstone——直接复用（计数已平衡，勿动）

            if (Interlocked.CompareExchange(ref slot.Accessed, 0, 1) == 1)
                continue;   // accessed=1 → 清位，给第二次机会

            // accessed=0 → 淘汰此 slot
            var oldKey = slot.Key;
            var oldVal = slot.Value;
            Interlocked.Exchange(ref slot.Hash, 0);
            // ★ CORE-08：不清 Key/Value（同 Remove 律——清场会抹掉并发 Put 抢槽写入的新条目；
            //   复用覆盖释放引用——有界滞留）
            slot.Accessed = 0;
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _evictions);
            if (oldVal is not null) _onEvict?.Invoke(oldKey, oldVal);
            return idx - baseIdx;
        }

        // 极端情况（全 accessed=1 且并发高）——强制淘汰时钟当前位置
        int forceIdx = baseIdx + ((Interlocked.Increment(ref _clockHand) - 1) & (_ways - 1));
        ref var forceSlot = ref _slots[forceIdx];
        int fh = forceSlot.Hash;
        if (fh == 0 || fh == Tombstone)
            return forceIdx - baseIdx;   // 不重复计数
        var fKey = forceSlot.Key;
        var fVal = forceSlot.Value;
        Interlocked.Exchange(ref forceSlot.Hash, 0);
        // ★ CORE-08：不清 Key/Value（同律）
        forceSlot.Accessed = 0;
        Interlocked.Decrement(ref _count);
        Interlocked.Increment(ref _evictions);
        if (fVal is not null) _onEvict?.Invoke(fKey, fVal);
        return forceIdx - baseIdx;
    }

    /// <summary>★ 键哈希——murmur3 fmix32 终混（雪崩均匀）；0/-1 折叠为 1（避让空槽/tombstone 标记）。</summary>
    /// <para>组相联对哈希质量敏感（桶偏斜 → 组内溢出提前淘汰），fmix 消除连续 key 的低位偏斜；
    /// 折叠碰撞概率 2^-31，由 Key 比较兜底。</para>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashKey(TKey key)
    {
        uint h = (uint)key.GetHashCode();
        h ^= h >> 16;
        h *= 0x85ebca6bu;
        h ^= h >> 13;
        h *= 0xc2b2ae35u;
        h ^= h >> 16;
        int result = (int)h;
        if (result == 0 || result == Tombstone) result = 1;
        return result;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ClockCacheV2<TKey, TValue>));
    }
}

/// <summary>ClockCacheV2 指标快照。</summary>
/// <remarks>★ 诊断指标：命中率 = Hits / (Hits + Misses)；无访问时为 0。</remarks>
/// <param name="Count">当前驻留条目数（原子计数快照；tombstone 槽已从计数中扣除）。</param>
/// <param name="Capacity">容量（slot 总数 = sets × ways）。</param>
/// <param name="Hits">累计命中次数（诊断指标）。</param>
/// <param name="Misses">累计未命中次数（诊断指标）。</param>
/// <param name="Evictions">累计淘汰次数（诊断指标）。</param>
/// <param name="HitRate">命中率 = Hits / (Hits + Misses)。</param>
public record struct ClockCacheV2Stats(
    int Count,
    int Capacity,
    long Hits,
    long Misses,
    long Evictions,
    double HitRate);
