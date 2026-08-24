namespace TC.Tier.Runtime.Tests.Structures.ProbingIndex;

/// <summary>
/// HashIndex 正确性专项测试——证明 tag 冲突判等闭环、rehash 等 bug 已修复。
/// <para>★ 这些用例在修复前（无判等闭环、rehash 不真搬迁）会失败，修复后通过。</para>
/// <para>★ 路由数学复刻 ProbingIndexBase：hash = KeyComparer XxHash64(key 字节)；
///   tag = (ushort)(hash >> 50)；bucket = hash &amp; SizeMask。旧 GetHashCode 折叠公式已随拆族消亡。</para>
/// </summary>
public class HashIndexCorrectnessTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public HashIndexCorrectnessTests()
    {
        _vol = new TestVolume();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        _vol.Dispose();
    }

    static (HashIndex<long> index, MockKeyResolver<long> resolver) CreateIndex(TestVolume vol,
        int hashTableCapacity = 1 << 20,
        int overflowPoolCapacity = 1 << 18)
    {
        var settings = TestProbingIndexSettingsFactory.On(vol, "hash", hashTableCapacity, overflowPoolCapacity);
        var resolver = new MockKeyResolver<long>();
        var hx = TestProbingIndexSettingsFactory.NewHash<long>(vol, settings, resolver);
        hx.WaitForReady();
        return (hx, resolver);
    }

    static void Put(HashIndex<long> index, MockKeyResolver<long> resolver, long key, LogicalAddress valueAddr, LogicalAddress begin)
    {
        var inserted = index.Insert(key, valueAddr, begin);
        resolver.Put(inserted, key);
    }

    // ★ 复刻 ProbingIndexBase<TKey>.ComputeTag/ComputeHash（KeyComparer XxHash64 路由），用于构造 tag 冲突。
    private static readonly KeyComparer<long> KeyCmp = new();

    static ushort ComputeTag(long key)
    {
        ulong hash = KeyCmp.GetHashCode64(key);
        return (ushort)(hash >> 50);
    }

    static long BucketOf(long key, long mask) => (long)(KeyCmp.GetHashCode64(key) & (ulong)mask);

    /// <summary>
    /// 暴力搜索两个不同 key，同 bucket 且同 tag（真 tag 冲突场景）。
    /// ★ 用小 capacity(16)：bucket 少 → 同 bucket 内 key 多 → tag 14 位易碰撞。
    ///   返回碰撞对 + 搜索所用的 capacity（测试用它建 index）。
    /// </summary>
    static (long k1, long k2, int capacity) FindTagCollision()
    {
        const int capacity = 16;
        long mask = capacity - 1;
        var seen = new Dictionary<(long bucket, ushort tag), long>();
        for (long k = 1; k < 5_000_000; k++)
        {
            var sig = (BucketOf(k, mask), ComputeTag(k));
            if (seen.TryGetValue(sig, out var prev))
                return (prev, k, capacity);
            seen[sig] = k;
        }
        throw new InvalidOperationException("未找到 tag 冲突(数据量不足)");
    }

    [Fact]
    public void TagCollision_DifferentKeys_DoesNotCrossReturn()
    {
        // ★ 核心正确性：两个不同 key 落同 bucket 同 tag，Find 不能把 A 查成 B 的 value。
        //   修复前(无判等闭环)：tag 命中即返回 → 后插入的会覆盖前者或 Find 串值。
        //   修复后(判等闭环)：tag 命中读回真 key 比对，各查各的。
        var (k1, k2, capacity) = FindTagCollision();
        var (idx, resolver) = CreateIndex(_vol, hashTableCapacity: capacity, overflowPoolCapacity: 1 << 18);
        var begin = idx.BeginAddress;

        // 确认确实是冲突对
        (BucketOf(k1, capacity - 1) == BucketOf(k2, capacity - 1)).Should().BeTrue();
        (ComputeTag(k1) == ComputeTag(k2)).Should().BeTrue();
        k1.Should().NotBe(k2);

        var v1 = new LogicalAddress(0, 111);
        var v2 = new LogicalAddress(0, 222);
        Put(idx, resolver, k1, v1, begin);
        Put(idx, resolver, k2, v2, begin);

        // 两个 key 都应查到各自的 value,不串
        using (idx)
        {
            idx.Find(k1).Should().Be(v1, $"key {k1} 不应被 key {k2} 的 value 覆盖");
            idx.Find(k2).Should().Be(v2, $"key {k2} 不应被 key {k1} 的 value 覆盖");
        }
    }

    [Fact]
    public void TagCollision_InsertDoesNotOverwrite()
    {
        // ★ 核心正确性：Insert 同 tag 异 key 时不能静默覆盖，应建独立条目。
        var (k1, k2, capacity) = FindTagCollision();
        var (idx, resolver) = CreateIndex(_vol, hashTableCapacity: capacity, overflowPoolCapacity: 1 << 18);
        var begin = idx.BeginAddress;

        var v1 = new LogicalAddress(0, 111);
        var v2 = new LogicalAddress(0, 222);

        Put(idx, resolver, k1, v1, begin);
        Put(idx, resolver, k2, v2, begin);

        // k1 插入 k2 后仍应查到 v1(未被覆盖)
        using (idx)
        {
            idx.Find(k1).Should().Be(v1, "同 tag 异 key 的 Insert 不应覆盖已有条目");
            idx.EntryCount.Should().Be(2, "两个不同 key 应是两个独立条目");
        }
    }

    [Fact]
    public void TagCollision_DeleteDoesNotAffectSibling()
    {
        // ★ 判等闭环：Delete 一个 key 不能误删同 tag 异 key 的兄弟条目。
        var (k1, k2, capacity) = FindTagCollision();
        var (idx, resolver) = CreateIndex(_vol, hashTableCapacity: capacity, overflowPoolCapacity: 1 << 18);
        var begin = idx.BeginAddress;

        Put(idx, resolver, k1, new LogicalAddress(0, 111), begin);
        Put(idx, resolver, k2, new LogicalAddress(0, 222), begin);

        using (idx)
        {
            idx.Delete(k1).Should().BeTrue();
            // k2 不应受影响
            idx.Find(k2).Should().Be(new LogicalAddress(0, 222), "删除 k1 不应影响同 tag 的 k2");
            idx.Find(k1).Should().Be(LogicalAddress.Empty);
        }
    }

    [Fact]
    public void GrowIndex_RealRehash_AllKeysSurvive()
    {
        // ★ rehash 正确性：GrowIndex 前后全量 Find 校验。
        //   修复前(原下标复制)：扩容后 Find 按新 mask 查会丢数据。
        //   修复后(读 key 重算 bucket)：任意 key 都正确。
        const int capacity = 1 << 6;   // 64,小表强制 rehash
        var (idx, resolver) = CreateIndex(_vol, hashTableCapacity: capacity, overflowPoolCapacity: 1 << 14);
        var begin = idx.BeginAddress;

        // 用跨容量边界的小整数 key(分布到不同 bucket,避免 overflow 极限干扰)
        var keyList = Enumerable.Range(1, 200).Select(i => (long)i).ToList();
        var valueOf = new Dictionary<long, LogicalAddress>();
        foreach (var k in keyList)
        {
            var v = new LogicalAddress(0, k);
            valueOf[k] = v;
            Put(idx, resolver, k, v, begin);
        }

        idx.GrowIndex();

        // GrowIndex 后所有 key 都应查到原 value
        using (idx)
        {
            int found = 0;
            foreach (var k in keyList)
            {
                var f = idx.Find(k);
                if (f == valueOf[k]) found++;
            }
            found.Should().Be(keyList.Count, "GrowIndex 真 rehash 后所有 key 应存活");
        }
    }

    [Fact]
    public void LargeKeySet_AllFound_NoFalsePositive()
    {
        // ★ 大规模 key 全量校验：排除任何残留假阳性/丢失。
        //   用打乱顺序的小整数 key(XxHash64 分布均匀,不触发 overflow 极限)。
        var (idx, resolver) = CreateIndex(_vol);
        var begin = idx.BeginAddress;

        var keys = Enumerable.Range(1, 2000).Select(i => (long)i)
                              .OrderBy(_ => Random.Shared.Next()).ToArray();

        var valueOf = new Dictionary<long, LogicalAddress>();
        foreach (var k in keys)
        {
            var v = new LogicalAddress(0, k);
            valueOf[k] = v;
            Put(idx, resolver, k, v, begin);
        }

        using (idx)
        {
            int hits = 0;
            foreach (var k in keys)
                if (idx.Find(k) == valueOf[k]) hits++;

            hits.Should().Be(keys.Length, "全部 key 都应精确命中,无假阳性无丢失");

            // 负向:未插入的 key 应查不到
            for (long k = 100000; k < 101000; k++)
                idx.Find(k).Should().Be(LogicalAddress.Empty, $"未插入的 key {k} 不应命中");
        }
    }
}
