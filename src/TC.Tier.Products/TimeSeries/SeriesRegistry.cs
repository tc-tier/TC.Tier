using System.Collections.Concurrent;
using TC.Tier.Contracts.Storage;

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// 序列侧账（每序列唯一内存成本，~64B 级——#443 设计稿 §5.3）：路由/统计/水位缓存。
/// </summary>
internal sealed class SeriesEntry
{
    private readonly object _pinLock = new();   // 钉住地址并发维护（首样本 init vs trim raise 竞态）

    /// <summary>存活样本计数（Append/trim/恢复增量维护——Interlocked）。</summary>
    internal long SampleCount;

    /// <summary>显式 trim 水位缓存（早于此 ts 的本序列样本已回收；long.MinValue = 从未 trim——
    /// 未 trim 序列水位 = 派生值（首个索引条目 ts），派生值只作观测不回写）。trim 在 _trimGate 内串行。</summary>
    internal long TrimmedUntil;

    /// <summary>上次显式 trim 的截断锚地址（诊断/对账参考——真相源是 Ring BeginAddress）。</summary>
    internal LogicalAddress TrimmedAddress;

    /// <summary>钉住地址（本序列最早存活样本地址——Ring 截断下限的 min 分量，慢序列钉住）。
    /// 首样本写入时登记（此后地址单调只增不会降低）；本序列 trim 后提升为首个存活条目地址。</summary>
    internal LogicalAddress PinnedAddress;

    /// <summary>首样本钉住（仅当未钉时生效——并发首写取先到者，后到的更大地址不覆盖更小钉点）。</summary>
    internal void InitPinnedAddress(LogicalAddress addr)
    {
        lock (_pinLock)
        {
            if (!PinnedAddress.IsValid) PinnedAddress = addr;
        }
    }

    /// <summary>trim 后钉住地址提升（首个存活条目——只能更大，不能回退到已回收区间）。</summary>
    internal void RaisePinnedAddress(LogicalAddress addr)
    {
        lock (_pinLock)
        {
            if (!PinnedAddress.IsValid || addr > PinnedAddress) PinnedAddress = addr;
        }
    }

    /// <summary>读钉住地址（锁内一致读）。</summary>
    internal LogicalAddress ReadPinnedAddress()
    {
        lock (_pinLock) return PinnedAddress;
    }
}

/// <summary>
/// 序列注册表（内存 ConcurrentDictionary——O(1) 路由/统计；#443 设计稿 §5.3）。
/// <para>★ 惰性创建：首 Append(seriesId) 自动注册（"内部自动按 seriesId 处理"）；恢复装载期
///   按重放事实/水位块种子化。容量护栏 = SeriesCapacity（超限 fail-fast 抛——设计稿裁决点 4）。</para>
/// </summary>
internal sealed class SeriesRegistry
{
    private readonly ConcurrentDictionary<uint, SeriesEntry> _entries = new();
    private readonly uint _capacity;

    /// <summary>构造。</summary>
    /// <param name="capacity">序列容量护栏（0 = 不限——单序列模式）。</param>
    internal SeriesRegistry(uint capacity) => _capacity = capacity;

    /// <summary>已注册序列数。</summary>
    internal int Count => _entries.Count;

    /// <summary>序列 id 快照（retention 轮/统计聚合遍历用）。</summary>
    internal IEnumerable<uint> SeriesIds => _entries.Keys;

    /// <summary>惰性注册（首样本写入——钉住地址 = 首样本地址）。</summary>
    /// <exception cref="InvalidOperationException">超出 SeriesCapacity（fail-fast——容量守卫）。</exception>
    internal SeriesEntry Register(uint seriesId, LogicalAddress firstSampleAddress)
    {
        if (_entries.TryGetValue(seriesId, out var existing)) return existing;
        GuardCapacity(seriesId);
        var entry = new SeriesEntry { PinnedAddress = firstSampleAddress };
        if (_entries.TryAdd(seriesId, entry)) return entry;
        // 并发输家：赢家已注册——取其条目（钉住地址 = 赢家的首样本地址，更老有效）
        return _entries[seriesId];
    }

    /// <summary>恢复装载种子化（不触发容量守卫——重放事实优先于护栏）。</summary>
    internal SeriesEntry Seed(uint seriesId) => _entries.GetOrAdd(seriesId, new SeriesEntry());

    internal bool TryGet(uint seriesId, out SeriesEntry entry) => _entries.TryGetValue(seriesId, out entry!);

    /// <summary>全体序列钉住地址的最小值（Ring 截断下限——慢序列钉住，设计稿 §5.4）。</summary>
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

    /// <summary>存活样本总数（注册表聚合）。</summary>
    internal long TotalSampleCount()
    {
        long total = 0;
        foreach (var kv in _entries) total += Volatile.Read(ref kv.Value.SampleCount);
        return total;
    }

    private void GuardCapacity(uint seriesId)
    {
        if (_capacity == 0 || _entries.Count < _capacity) return;
        if (_entries.ContainsKey(seriesId)) return;
        throw new InvalidOperationException(
            $"序列数超出 SeriesCapacity（{_capacity}）——fail-fast（#443 设计稿裁决点 4；扩容/逐出后置）");
    }
}
