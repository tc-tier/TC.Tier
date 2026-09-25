using System.Collections.Concurrent;

namespace TC.Tier.Products.Collections;

/// <summary>
/// 域侧账（每域唯一内存成本——TimeSeries SeriesEntry 同构，tc-tier-collections-spec §1 域账）：
/// 路由/统计/水位缓存 + 钉住地址。
/// </summary>
internal sealed class DomainEntry
{
    private readonly object _pinLock = new();   // 钉住地址并发维护（首写 init vs 截断 raise 竞态）

    /// <summary>存活成员计数（写/删/恢复增量维护——Interlocked）。</summary>
    internal long MemberCount;

    /// <summary>域字节占用（envelope 逻辑字节口径——写/删/恢复增量维护——Interlocked）。</summary>
    internal long BytesOccupied;

    /// <summary>域最近写入时刻（UTC Ticks——TTL 过期锚；恢复按持久块种子化，数据事实不可重建——
    ///   Ring record 不携带墙钟——缺块时以恢复时刻收口防误过期）。</summary>
    internal long LastWriteTicks;

    /// <summary>钉住地址（本域最早存活 record 地址——Ring 截断下限的 min 分量）。
    /// 首写登记（此后地址单调只增）；本域整域回收即注销（钉住随之解除）。</summary>
    internal LogicalAddress PinnedAddress;

    /// <summary>脏标记（域账未持久化——FlushAsync/retention 轮/注销时机持久化）。</summary>
    internal bool Dirty;

    /// <summary>首写钉住（仅当未钉时生效——并发首写取先到者）。</summary>
    internal void InitPinnedAddress(LogicalAddress addr)
    {
        lock (_pinLock)
        {
            if (!PinnedAddress.IsValid) PinnedAddress = addr;
        }
    }

    /// <summary>读钉住地址（锁内一致读）。</summary>
    internal LogicalAddress ReadPinnedAddress()
    {
        lock (_pinLock) return PinnedAddress;
    }
}

/// <summary>
/// 域注册表（内存 ConcurrentDictionary——O(1) 路由/统计；SeriesRegistry 同构）。
/// <para>★ 惰性创建：首写 (domain) 自动注册；恢复装载期按持久域账块种子化（重放事实校正计数）。
/// 容量护栏 = DomainCapacity（超限 fail-fast——spec §9；0 = 不限）。</para>
/// </summary>
internal sealed class DomainRegistry
{
    private readonly ConcurrentDictionary<uint, DomainEntry> _entries = new();
    private readonly uint _capacity;

    /// <summary>构造。</summary>
    /// <param name="capacity">域容量护栏（0 = 不限）。</param>
    internal DomainRegistry(uint capacity) => _capacity = capacity;

    /// <summary>已注册域数。</summary>
    internal int Count => _entries.Count;

    /// <summary>域 id 快照（retention 轮/统计/备份导出遍历用）。</summary>
    internal IEnumerable<uint> DomainIds => _entries.Keys;

    /// <summary>(域 id, 域侧账) 枚举（域账持久快照收集用——遍历期间并发注册/注销安全）。</summary>
    internal IEnumerable<KeyValuePair<uint, DomainEntry>> Enumerate() => _entries;

    /// <summary>惰性注册（首写——钉住地址 = 首写地址；lastWrite = 写入时刻）。</summary>
    /// <exception cref="InvalidOperationException">超出 DomainCapacity（fail-fast——容量守卫）。</exception>
    internal DomainEntry Register(uint domainId, LogicalAddress firstRecordAddress, long nowTicks)
    {
        if (_entries.TryGetValue(domainId, out var existing)) return existing;
        GuardCapacity(domainId);
        var entry = new DomainEntry
        {
            PinnedAddress = firstRecordAddress,
            LastWriteTicks = nowTicks,
            Dirty = true,
        };
        if (_entries.TryAdd(domainId, entry)) return entry;
        // 并发输家：赢家已注册——取其条目（钉住 = 赢家的首写地址，更老有效）
        return _entries[domainId];
    }

    /// <summary>恢复装载种子化（不触发容量守卫——重放事实优先；lastWrite 缺省 0 由恢复收口）。</summary>
    internal DomainEntry Seed(uint domainId) => _entries.GetOrAdd(domainId, new DomainEntry());

    internal bool TryGet(uint domainId, out DomainEntry entry) => _entries.TryGetValue(domainId, out entry!);

    /// <summary>注销（整域回收/TTL 过期——钉住随之解除）。</summary>
    internal bool Remove(uint domainId) => _entries.TryRemove(domainId, out _);

    /// <summary>全体域钉住地址的最小值（Ring 截断下限；空注册表 = 兜底全可回收）。</summary>
    /// <param name="fallback">注册表空时的兜底（Ring FlushedUntilAddress——全部可回收）。</param>
    internal LogicalAddress MinPinnedAddress(LogicalAddress fallback)
    {
        var min = fallback;
        foreach (var kv in _entries)
        {
            var pin = kv.Value.ReadPinnedAddress();
            if (pin.IsValid && pin < min) min = pin;
        }
        return min;
    }

    private void GuardCapacity(uint domainId)
    {
        if (_capacity == 0 || _entries.Count < _capacity) return;
        if (_entries.ContainsKey(domainId)) return;
        throw new InvalidOperationException(
            $"域数超出 DomainCapacity（{_capacity}）——fail-fast（tc-tier-collections-spec §9；扩容/逐出后置）");
    }
}
