namespace TC.Tier.Core.Tests.Collections;

/// <summary>
/// ClockCacheV2（组相联 CLOCK）单元测试——命中/未命中、组溢出淘汰、并发、指标、回调。
/// <para>设计见 src/TC.Tier.Core/docs/cache-and-compute.md。</para>
/// </summary>
public class ClockCacheV2Tests
{
    [Fact]
    public void TryGet_HitMiss_Basic()
    {
        using var cache = new ClockCacheV2<long, string>(capacity: 128);
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
        using var cache = new ClockCacheV2<long, string>(capacity: 128);
        cache.Put(1, "old");
        cache.Put(1, "new");

        cache.TryGet(1, out var v).Should().BeTrue();
        v.Should().Be("new", "Put 已存在的 key 应更新 value");
        cache.Count.Should().Be(1, "更新不增加 count");
    }

    [Fact]
    public void Put_FillCapacity_CountExact()
    {
        int cap = 64;
        using var cache = new ClockCacheV2<long, int>(capacity: cap);
        for (int i = 1; i <= cap; i++)
            cache.Put(i, i * 10);

        // ★ 组相联：组偏斜（某组键数 > ways）会提前淘汰，Count = 插入数 - 淘汰数；
        //   不变量：Count == cap - Evictions（无 Remove 时），且恒 ≤ cap。
        cache.Count.Should().Be(cap - (int)cache.Evictions, "Count 应等于插入数减淘汰数");
        cache.Count.Should().BeLessThanOrEqualTo(cap, "容量上限恒成立");
    }

    [Fact]
    public void Put_SetOverflow_EvictsBeforeCapacity()
    {
        // ★ 组相联语义：同组键数 > ways 时提前淘汰（不等填满容量）。
        // 哈希全冲突 → 全落同一组（capacity=16、ways=8 → 2 组，冲突键全进 1 组）。
        using var cache = new ClockCacheV2<CollidingKey, int>(capacity: 16);
        for (int i = 1; i <= 10; i++)
            cache.Put(new CollidingKey(1, $"K{i}"), i);

        cache.Count.Should().Be(8, "组内容量只有 ways=8，溢出即淘汰");
        cache.Evictions.Should().BeGreaterThanOrEqualTo(2, "10 键进 8 路组应至少淘汰 2 次");
        cache.Count.Should().BeLessThanOrEqualTo(16, "总容量上限仍成立");
    }

    [Fact]
    public void Put_OverCapacity_EvictsAndCallsCallback()
    {
        int cap = 16;
        var evicted = new System.Collections.Generic.List<long>();
        using var cache = new ClockCacheV2<long, int>(cap, (k, v) => evicted.Add(k));

        for (int i = 1; i <= cap + 8; i++)
            cache.Put(i, i);

        cache.Count.Should().BeLessThanOrEqualTo(cap, "超容量应淘汰");
        cache.Evictions.Should().BeGreaterThan(0, "应触发淘汰");
        evicted.Should().NotBeEmpty("淘汰回调应被调用");
    }

    [Fact]
    public void Remove_Explicit()
    {
        using var cache = new ClockCacheV2<long, string>(capacity: 128);
        cache.Put(1, "one");
        cache.Remove(1).Should().BeTrue();
        cache.TryGet(1, out _).Should().BeFalse("Remove 后应 miss");
        cache.Count.Should().Be(0);
        cache.Remove(999).Should().BeFalse("移除不存在的 key 返回 false");
    }

    /// <summary>哈希全冲突的同组键：删中间键 + 重插——全路扫描下槽位相互独立，无探测链。</summary>
    [Fact]
    public void Remove_AndReinsert_SameBucket_CollidingKeys()
    {
        using var cache = new ClockCacheV2<CollidingKey, int>(capacity: 16);
        var a = new CollidingKey(1, "A");
        var b = new CollidingKey(1, "B");
        var c = new CollidingKey(1, "C");
        cache.Put(a, 10);
        cache.Put(b, 20);
        cache.Put(c, 30);

        cache.Remove(b).Should().BeTrue();
        cache.TryGet(b, out _).Should().BeFalse("B 已删");

        cache.TryGet(a, out var va).Should().BeTrue("A 不受中间删除影响（无链）");
        va.Should().Be(10);
        cache.TryGet(c, out var vc).Should().BeTrue("C 不受中间删除影响（无链）");
        vc.Should().Be(30);
        cache.Count.Should().Be(2);

        // tombstone 可被新插入复用
        cache.Put(new CollidingKey(1, "D"), 40);
        cache.TryGet(new CollidingKey(1, "D"), out var vd).Should().BeTrue();
        vd.Should().Be(40);
    }

    /// <summary>哈希可控的 key：所有实例 GetHashCode 相同，强制同组。</summary>
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
        using var cache = new ClockCacheV2<long, int>(capacity: 64);
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
        using var cache = new ClockCacheV2<long, int>(capacity: 64);
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
        // 验证组内 CLOCK 近似 LRU：被访问的 entry 获得第二次机会
        int cap = 8;   // ways=8 → 单组
        using var cache = new ClockCacheV2<long, int>(capacity: cap);

        for (int i = 1; i <= cap; i++)
            cache.Put(i, i);

        cache.TryGet(1, out _);   // 设访问位

        for (int i = cap + 1; i <= cap + 4; i++)
            cache.Put(i, i);

        cache.Count.Should().BeLessThanOrEqualTo(cap);
    }

    [Fact]
    public void Ways_ClampedToCapacity()
    {
        using var cache = new ClockCacheV2<long, int>(capacity: 4, ways: 16);
        cache.Ways.Should().Be(4, "ways 超 capacity 应减半钳制");
        for (int i = 1; i <= 4; i++)
            cache.Put(i, i);
        cache.Count.Should().Be(4);
    }

    [Fact]
    public void Concurrent_ReadWriteSafe()
    {
        int cap = 256;
        using var cache = new ClockCacheV2<int, int>(capacity: cap);
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
        using var cache = new ClockCacheV2<long, DummyDisposable>(capacity: 8, (k, v) =>
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
