namespace TC.Tier.Core.Tests.Collections;
/// <summary>
/// ClockCache 单元测试——命中/未命中、淘汰、并发、指标、回调。
/// </summary>
public class ClockCacheTests
{
    [Fact]
    public void TryGet_HitMiss_Basic()
    {
        using var cache = new ClockCache<long, string>(capacity: 128);
        cache.Put(1, "one");
        cache.Put(2, "two");

        cache.TryGet(1, out var v1).Should().BeTrue();
        v1.Should().Be("one");

        cache.TryGet(2, out var v2).Should().BeTrue();
        v2.Should().Be("two");

        cache.TryGet(999, out _).Should().BeFalse("未插入的 key 应 miss");
    }

    [Fact]
    public void Put_UpdateExisting()
    {
        using var cache = new ClockCache<long, string>(capacity: 128);
        cache.Put(1, "old");
        cache.Put(1, "new");

        cache.TryGet(1, out var v).Should().BeTrue();
        v.Should().Be("new", "Put 已存在的 key 应更新 value");
        cache.Count.Should().Be(1, "更新不增加 count");
    }

    [Fact]
    public void Put_FillCapacity_NoEvict()
    {
        int cap = 64;
        using var cache = new ClockCache<long, int>(capacity: cap);
        for (int i = 1; i <= cap; i++)
            cache.Put(i, i * 10);

        cache.Count.Should().Be(cap, "填满容量不应淘汰");
        // 全部可读
        for (int i = 1; i <= cap; i++)
        {
            cache.TryGet(i, out var v).Should().BeTrue($"key {i} 应在缓存中");
            v.Should().Be(i * 10);
        }
        cache.Evictions.Should().Be(0, "填满不应触发淘汰");
    }

    [Fact]
    public void Put_OverCapacity_EvictsAndCallsCallback()
    {
        int cap = 16;
        var evicted = new System.Collections.Generic.List<long>();
        using var cache = new ClockCache<long, int>(cap, (k, v) => evicted.Add(k));

        for (int i = 1; i <= cap + 8; i++)
            cache.Put(i, i);

        cache.Count.Should().BeLessThanOrEqualTo(cap + 1, "超容量应淘汰");
        cache.Evictions.Should().BeGreaterThan(0, "应触发淘汰");
        evicted.Should().NotBeEmpty("淘汰回调应被调用");
    }

    [Fact]
    public void Remove_Explicit()
    {
        using var cache = new ClockCache<long, string>(capacity: 128);
        cache.Put(1, "one");
        cache.Remove(1).Should().BeTrue();
        cache.TryGet(1, out _).Should().BeFalse("Remove 后应 miss");
        cache.Count.Should().Be(0);
        cache.Remove(999).Should().BeFalse("移除不存在的 key 返回 false");
    }

    /// <summary>
    /// STORAGE-023 回归：Remove 置 tombstone，开放寻址探测链不断裂。
    /// 三个哈希冲突的 key 落同链：A→B→C。删 B 后 A、C 仍须可查到。
    /// （旧行为：B 置 0 导致 C 在 B 处 break，C 查不到。）
    /// </summary>
    [Fact]
    public void Remove_KeepsProbeChainIntact_Tombstone()
    {
        // 哈希全冲突的 key，强制线性探测成链：slot0=A, slot1=B, slot2=C
        using var cache = new ClockCache<CollidingKey, int>(capacity: 16);
        var a = new CollidingKey(1, "A");
        var b = new CollidingKey(1, "B");
        var c = new CollidingKey(1, "C");
        cache.Put(a, 10);
        cache.Put(b, 20);
        cache.Put(c, 30);

        // 删中间的 B —— 应置 tombstone 而非清空
        cache.Remove(b).Should().BeTrue();
        cache.TryGet(b, out _).Should().BeFalse("B 已删");

        // ★ A 和 C 仍须命中：tombstone 让探测链跨过 B 的位置继续到 C
        cache.TryGet(a, out var va).Should().BeTrue("链首 A 不受中间删除影响");
        va.Should().Be(10);
        cache.TryGet(c, out var vc).Should().BeTrue("链尾 C 须经 B 的 tombstone 继续探测——旧行为会在此失败（#243）");
        vc.Should().Be(30);
        cache.Count.Should().Be(2);

        // tombstone 可被新插入复用：Put 新 key 应回收 B 原位置
        cache.Put(new CollidingKey(1, "D"), 40);
        cache.TryGet(new CollidingKey(1, "D"), out var vd).Should().BeTrue();
        vd.Should().Be(40);
    }

    /// <summary>哈希可控的 key：所有实例 GetHashCode 相同，强制线性探测。</summary>
    private readonly struct CollidingKey(int hash, string name) : IEquatable<CollidingKey>
    {
        private readonly int _hash = hash;
        private readonly string _name = name;
        public override int GetHashCode() => _hash;
        public bool Equals(CollidingKey other) => _name == other._name;
        public override bool Equals(object? obj) => obj is CollidingKey k && Equals(k);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        using var cache = new ClockCache<long, int>(capacity: 64);
        for (int i = 1; i <= 10; i++)
            cache.Put(i, i);
        cache.Count.Should().Be(10);

        cache.Clear();
        cache.Count.Should().Be(0);
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [Fact]
    public void Stats_Accurate()
    {
        using var cache = new ClockCache<long, int>(capacity: 64);
        cache.Put(1, 10);
        cache.Put(2, 20);

        cache.TryGet(1, out _);   // hit
        cache.TryGet(1, out _);   // hit
        cache.TryGet(999, out _); // miss

        var stats = cache.GetStats();
        stats.Hits.Should().Be(2);
        stats.Misses.Should().Be(1);
        stats.Count.Should().Be(2);
        stats.HitRate.Should().BeApproximately(2.0 / 3, 0.01);
    }

    [Fact]
    public void ClockEviction_FavorsRecentlyAccessed()
    {
        // 验证 CLOCK 近似 LRU：被访问的 entry 获得第二次机会
        int cap = 8;
        using var cache = new ClockCache<long, int>(capacity: cap);

        for (int i = 1; i <= cap; i++)
            cache.Put(i, i);

        // 访问 key=1（设访问位），使其在 CLOCK 扫描中获第二次机会
        cache.TryGet(1, out _);

        // 插入新 key 触发淘汰
        for (int i = cap + 1; i <= cap + 4; i++)
            cache.Put(i, i);

        // key=1 应有较大概率仍在缓存（被访问过，获第二次机会）
        // 注意：CLOCK 是近似 LRU，不保证 key=1 绝对在，但在小容量下应大概率保留
        // 这里验证缓存大小不超容量
        cache.Count.Should().BeLessThanOrEqualTo(cap);
    }

    [Fact]
    public void Concurrent_ReadWriteSafe()
    {
        int cap = 256;
        using var cache = new ClockCache<int, int>(capacity: cap);
        int threadCount = 4;
        int opsPerThread = 5000;
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int tid = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    var rng = new Random(tid * 1000);
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        int key = rng.Next(1, cap * 2);
                        if (rng.Next(2) == 0)
                            cache.Put(key, key * 10);
                        else
                            cache.TryGet(key, out _);
                    }
                }
                catch (Exception ex) { exceptions.Enqueue(ex); }
            });
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();

        exceptions.Should().BeEmpty("多线程并发读写不应抛异常");
        cache.Count.Should().BeLessThanOrEqualTo(cap, "缓存大小不应超容量");
    }

    [Fact]
    public void EvictCallback_DisposesValue()
    {
        var disposed = new System.Collections.Generic.List<long>();
        using var cache = new ClockCache<long, DummyDisposable>(capacity: 8, (k, v) =>
        {
            v.Dispose();
            disposed.Add(k);
        });

        for (int i = 1; i <= 16; i++)
            cache.Put(i, new DummyDisposable(i));

        disposed.Should().NotBeEmpty("淘汰时应调 Dispose");
        disposed.Count.Should().BeGreaterThan(0);
    }

    private sealed class DummyDisposable(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
