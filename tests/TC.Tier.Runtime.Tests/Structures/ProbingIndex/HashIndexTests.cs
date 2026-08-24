namespace TC.Tier.Runtime.Tests.Structures.ProbingIndex;

public class HashIndexTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public HashIndexTests()
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

    /// <summary>
    /// 在测试卷上创建一个 HashIndex<long> 及其配套的 MockKeyResolver。
    /// ★ tag-only 桶判等闭环：HashIndex 构造必传 IKeyResolver&lt;TKey&gt;（探测族硬依赖）。
    /// 调用方需用 <see cref="Put"/> 而非直接 index.Insert，以便同步注册 resolver 映射。
    /// </summary>
    static (HashIndex<long> index, MockKeyResolver<long> resolver) CreateIndex(TestVolume vol,
        int hashTableCapacity = 1 << 20,
        int overflowPoolCapacity = 1 << 18)
    {
        var settings = TestProbingIndexSettingsFactory.On(vol, "hash", hashTableCapacity, overflowPoolCapacity);
        var resolver = new MockKeyResolver<long>();
        var hx = TestProbingIndexSettingsFactory.NewHash<long>(vol, settings, resolver);
        return (hx, resolver);
    }

    /// <summary>
    /// Insert 并同步注册 resolver，以保证 Find/Delete 内部判等闭环回调 TryGetKey 时能读回真 key。
    /// 用 Insert 返回的 entry 地址注册最稳妥（它由 HashEntry.CreateOccupied(valueAddr.SegId/Offset, ...) 生成，
    /// SegId/Offset 与 valueAddr 相同；LogicalAddress 等价性只比 SegId/Offset）。
    /// </summary>
    static void Put(HashIndex<long> index, MockKeyResolver<long> resolver, long key, LogicalAddress valueAddr, LogicalAddress begin)
    {
        var inserted = index.Insert(key, valueAddr, begin);
        resolver.Put(inserted, key);
    }

    static LogicalAddress MakeAddr(long offset) => new(0, offset);

    // ★ 复刻 ProbingIndexBase 路由数学（KeyComparer XxHash64 → bucket），用于构造同桶 key。
    private static readonly KeyComparer<long> KeyCmp = new();

    private static long BucketIndex(long key, long mask)
        => (long)(KeyCmp.GetHashCode64(key) & (ulong)mask);

    private static long[] FindManyCollidingKeys(long targetBucket, long mask, int count)
    {
        var result = new List<long>();
        for (long k = 0; result.Count < count; k++)
        {
            if (BucketIndex(k, mask) == targetBucket)
                result.Add(k);
        }
        return result.ToArray();
    }

    [Fact]
    public void Insert_Find_RoundTrip()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var value = MakeAddr(100);
            var begin = index.BeginAddress;

            Put(index, resolver, 42, value, begin);

            var found = index.Find(42);
            found.Should().Be(value);
        }
    }

    [Fact]
    public void Insert_Overwrite_UpdatesEntry()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin = index.BeginAddress;

            var addr1 = MakeAddr(100);
            var addr2 = MakeAddr(200);

            Put(index, resolver, 42, addr1, begin);
            Put(index, resolver, 42, addr2, begin);

            var found = index.Find(42);
            found.Should().Be(addr2);
        }
    }

    [Fact]
    public void Find_NonExistentKey_ReturnsEmpty()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin = index.BeginAddress;

            Put(index, resolver, 1, MakeAddr(10), begin);

            var found = index.Find(999);
            found.Should().Be(LogicalAddress.Empty);
        }
    }

    [Fact]
    public void Delete_ExistingKey_RemovesEntry()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin = index.BeginAddress;

            Put(index, resolver, 50, MakeAddr(500), begin);

            var deleted = index.Delete(50);
            deleted.Should().BeTrue();
            index.Find(50).Should().Be(LogicalAddress.Empty);
        }
    }

    [Fact]
    public void Delete_NonExistentKey_ReturnsFalse()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin = index.BeginAddress;

            Put(index, resolver, 1, MakeAddr(10), begin);

            var deleted = index.Delete(999);
            deleted.Should().BeFalse();
        }
    }

    [Fact]
    public void Insert_AfterDelete_Succeeds()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin = index.BeginAddress;

            Put(index, resolver, 77, MakeAddr(100), begin);
            index.Delete(77).Should().BeTrue();
            index.Find(77).Should().Be(LogicalAddress.Empty);

            var newAddr = MakeAddr(200);
            Put(index, resolver, 77, newAddr, begin);

            var found = index.Find(77);
            found.Should().Be(newAddr);
        }
    }

    [Fact]
    public void ConcurrentInserts_SameBucket_TagCollision()
    {
        const int capacity = 1 << 10;
        var (index, resolver) = CreateIndex(_vol, hashTableCapacity: capacity);
        using (index)
        {
            var begin = index.BeginAddress;
            long mask = capacity - 1;

            var bucket = 0L;
            var keys = FindManyCollidingKeys(bucket, mask, 5);

            keys.Length.Should().BeGreaterOrEqualTo(5);

            for (int i = 0; i < keys.Length; i++)
                Put(index, resolver, keys[i], MakeAddr((i + 1) * 100), begin);

            for (int i = 0; i < keys.Length; i++)
            {
                var found = index.Find(keys[i]);
                found.Should().Be(MakeAddr((i + 1) * 100), $"key {keys[i]} not found correctly");
            }
        }
    }

    [Fact]
    public void OverflowChain_MoreThanSevenEntries_SameHash()
    {
        // 桶容量 8 slot，同桶 12 条 key 必然走溢出链（bucket 路由复刻 KeyComparer XxHash64）。
        const int capacity = 128;
        var (index, resolver) = CreateIndex(_vol, hashTableCapacity: capacity, overflowPoolCapacity: 32);
        using (index)
        {
            var begin = index.BeginAddress;

            var keys = FindManyCollidingKeys(targetBucket: 0L, mask: capacity - 1, count: 12);
            for (int i = 0; i < keys.Length; i++)
                Put(index, resolver, keys[i], new LogicalAddress(0, (i + 1) * 10), begin);

            for (int i = 0; i < keys.Length; i++)
            {
                var found = index.Find(keys[i]);
                found.Should().NotBe(LogicalAddress.Empty, $"key {keys[i]} (bucket={BucketIndex(keys[i], capacity - 1)}) not found");
            }
        }
    }

    [Fact]
    public void GrowIndex_EntriesSurviveResize()
    {
        // ★ 有了 resolver，rehash 时内部 TryGetKey 读 key 重算 bucket 能正确闭环。
        var (index, resolver) = CreateIndex(_vol, hashTableCapacity: 64);
        using (index)
        {
            var begin = index.BeginAddress;

            for (int i = 1; i <= 40; i++)
                Put(index, resolver, i, new LogicalAddress(0, i * 10), begin);

            index.GrowIndex();

            for (int i = 1; i <= 40; i++)
            {
                var found = index.Find(i);
                found.Should().Be(new LogicalAddress(0, i * 10), $"key {i} lost after GrowIndex");
            }
        }
    }

    [Fact]
    public void MultipleEntries_ThousandPlus_AllFound()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin = index.BeginAddress;

            var count = 1500;
            var keys = Enumerable.Range(0, count).Select(i => (long)i).OrderBy(_ => Random.Shared.Next()).ToArray();

            for (int i = 0; i < keys.Length; i++)
                Put(index, resolver, keys[i], MakeAddr(keys[i] * 10), begin);

            for (int i = 0; i < keys.Length; i++)
            {
                var found = index.Find(keys[i]);
                found.Should().Be(MakeAddr(keys[i] * 10), $"key {keys[i]} not found");
            }
        }
    }

    [Fact]
    public void EntryCount_Accuracy()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin = index.BeginAddress;

            index.EntryCount.Should().Be(0);

            for (int i = 0; i < 10; i++)
                Put(index, resolver, i, MakeAddr(i * 10), begin);
            index.EntryCount.Should().Be(10);

            index.Delete(3);
            index.EntryCount.Should().Be(9);

            index.Delete(7);
            index.EntryCount.Should().Be(8);

            Put(index, resolver, 3, MakeAddr(30), begin);
            index.EntryCount.Should().Be(9);

            Put(index, resolver, 99, MakeAddr(990), begin);
            index.EntryCount.Should().Be(10);
        }
    }

    [Fact]
    public void TentativeEntry_NotVisibleToFind()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin = index.BeginAddress;

            var value = MakeAddr(42);
            Put(index, resolver, 100, value, begin);

            var found = index.Find(100);
            found.Should().Be(value);

            var empty = index.Find(101);
            empty.Should().Be(LogicalAddress.Empty);
        }
    }

    [Fact]
    public void BeginAddress_OldEntriesReplaced()
    {
        var (index, resolver) = CreateIndex(_vol);
        using (index)
        {
            var begin1 = index.BeginAddress;

            Put(index, resolver, 42, MakeAddr(100), begin1);

            var begin2 = MakeAddr(999);
            var newAddr = MakeAddr(200);
            Put(index, resolver, 42, newAddr, begin2);

            var found = index.Find(42);
            found.Should().Be(newAddr);
        }
    }

    [Fact]
    public void IndexSize_ReturnsReasonableEstimate()
    {
        var (index, _) = CreateIndex(_vol, hashTableCapacity: 64, overflowPoolCapacity: 32);
        using (index)
        {
            var size = index.IndexSize;
            size.Should().BeGreaterThan(0);
            size.Should().Be(64 * 128L + 32 * 128L);
        }
    }
}
