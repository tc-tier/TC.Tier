namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

public class BTreeIndexTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public BTreeIndexTests()
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

    static BTreeIndex<long> CreateBTreeIndex(TestVolume vol)
    {
        var settings = TestSortedIndexSettingsFactory.BTreeOn(vol, "bt");
        // BTreeIndex 节点内嵌 key,不依赖 IKeyResolver 判等闭环（比较族条目物化 key）。
        return TestSortedIndexSettingsFactory.NewBTree<long>(vol, settings);
    }

    static LogicalAddress MakeAddr(long offset) => new(0, offset);

    [Fact]
    public void Insert_Find_RoundTrip()
    {
        using var index = CreateBTreeIndex(_vol);
        var value = MakeAddr(42);

        var inserted = index.Insert(100, value, LogicalAddress.Empty);
        inserted.Should().Be(value);

        var found = index.Find(100);
        found.Should().Be(value);
    }

    [Fact]
    public void Insert_MultipleKeys_OrderedScan()
    {
        using var index = CreateBTreeIndex(_vol);
        var keys = new long[] { 1, 3, 5, 7, 9 };
        foreach (var key in keys)
            index.Insert(key, MakeAddr(key * 10), LogicalAddress.Empty);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        int i = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().Be(keys[i]);
            cursor.CurrentValue.Should().Be(MakeAddr(keys[i] * 10));
            i++;
        }
        i.Should().Be(keys.Length);
    }

    [Fact]
    public void Insert_MultipleKeys_RandomOrder_OrderedScan()
    {
        using var index = CreateBTreeIndex(_vol);
        var insertOrder = new long[] { 7, 1, 9, 3, 5 };
        var expected = new long[] { 1, 3, 5, 7, 9 };
        foreach (var key in insertOrder)
            index.Insert(key, MakeAddr(key * 10), LogicalAddress.Empty);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        int i = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().Be(expected[i]);
            cursor.CurrentValue.Should().Be(MakeAddr(expected[i] * 10));
            i++;
        }
        i.Should().Be(expected.Length);
    }

    [Fact]
    public void Find_NonExistent_ReturnsEmpty()
    {
        using var index = CreateBTreeIndex(_vol);
        index.Insert(1, MakeAddr(1), LogicalAddress.Empty);

        var found = index.Find(999);
        found.Should().Be(LogicalAddress.Empty);
    }

    [Fact]
    public void Delete_Existing_RemovesEntry()
    {
        using var index = CreateBTreeIndex(_vol);
        index.Insert(50, MakeAddr(500), LogicalAddress.Empty);

        var deleted = index.Delete(50);
        deleted.Should().BeTrue();
        index.Find(50).Should().Be(LogicalAddress.Empty);
    }

    [Fact]
    public void Delete_NonExistent_ReturnsFalse()
    {
        using var index = CreateBTreeIndex(_vol);
        index.Insert(1, MakeAddr(1), LogicalAddress.Empty);

        var deleted = index.Delete(999);
        deleted.Should().BeFalse();
    }

    [Fact]
    public void LeafNodeSplit_ScanReturnsAllKeys()
    {
        using var index = CreateBTreeIndex(_vol);
        int count = 12;
        for (long i = 0; i < count; i++)
            index.Insert(i, MakeAddr(i * 10), LogicalAddress.Empty);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        long expected = 0;
        int scanned = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().Be(expected);
            cursor.CurrentValue.Should().Be(MakeAddr(expected * 10));
            expected++;
            scanned++;
        }
        scanned.Should().Be(count);
    }

    [Fact]
    public void MultiLevelGrowth_AllKeysFindable()
    {
        using var index = CreateBTreeIndex(_vol);
        int count = 15;
        for (long i = 0; i < count; i++)
            index.Insert(i, MakeAddr(i * 10), LogicalAddress.Empty);

        index.EntryCount.Should().Be(count);
        for (long i = 0; i < count; i++)
            index.Find(i).Should().Be(MakeAddr(i * 10));
    }

    [Fact]
    public void RangeScanAcrossMultipleLeafNodes()
    {
        using var index = CreateBTreeIndex(_vol);
        int count = 10;
        for (long i = 0; i < count; i++)
            index.Insert(i, MakeAddr(i * 10), LogicalAddress.Empty);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var results = new List<(long Key, LogicalAddress Value)>();
        while (cursor.MoveNext())
            results.Add((cursor.CurrentKey, cursor.CurrentValue));

        results.Should().HaveCount(count);
        for (int i = 0; i < count; i++)
        {
            results[i].Key.Should().Be(i);
            results[i].Value.Should().Be(MakeAddr(i * 10));
        }
    }

    [Fact]
    public void LargeScaleInsert_FindAndCount()
    {
        using var index = CreateBTreeIndex(_vol);
        int count = 40;
        for (long i = 0; i < count; i++)
            index.Insert(i, MakeAddr(i * 10), LogicalAddress.Empty);

        index.EntryCount.Should().Be(count);
        for (long i = 0; i < count; i++)
            index.Find(i).Should().Be(MakeAddr(i * 10));
    }

    [Fact]
    public void EntryCount_Accuracy()
    {
        using var index = CreateBTreeIndex(_vol);
        index.EntryCount.Should().Be(0);

        for (int i = 0; i < 5; i++)
            index.Insert(i, MakeAddr(i), LogicalAddress.Empty);
        index.EntryCount.Should().Be(5);

        index.Delete(2);
        index.EntryCount.Should().Be(4);

        index.Delete(4);
        index.EntryCount.Should().Be(3);
    }

    [Fact]
    public void Insert_DuplicateKey_Overwrites()
    {
        using var index = CreateBTreeIndex(_vol);
        var first = MakeAddr(100);
        var second = MakeAddr(200);

        index.Insert(1, first, LogicalAddress.Empty);
        index.Insert(1, second, LogicalAddress.Empty);

        index.Find(1).Should().Be(second);
    }

    [Fact]
    public void LargeScaleShuffled_MultiLevelSplits_AllFound()
    {
        // ★ 跨根 internal 分裂（8 键/9 子满 → 深度 3+）的乱序插入——旧码 internal 容量错配
        //   （按叶子 9 键容量用满 8 键后 SetValue(9) 越界）+ 溢出传播错调根叶子分裂，此形态必炸。
        using var index = CreateBTreeIndex(_vol);
        const int count = 500;
        var keys = Enumerable.Range(0, count).Select(i => (long)i).OrderBy(_ => Random.Shared.Next()).ToArray();

        foreach (var key in keys)
            index.Insert(key, MakeAddr(key * 3), LogicalAddress.Empty);

        index.EntryCount.Should().Be(count);
        for (long i = 0; i < count; i++)
            index.Find(i).Should().Be(MakeAddr(i * 3), $"key {i} 多层分裂后应命中");

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        long expected = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().Be(expected);
            expected++;
        }
        expected.Should().Be(count, "乱序插入后全量有序遍历应无缺无重");
    }

    [Fact]
    public void Delete_FromMultiLevelTree_ActuallyRemoves()
    {
        // ★ 深层叶删除（旧码只写引擎不刷缓存——叶子经 GetInternalNode 也进缓存，Find 读陈旧缓存命中已删 key）
        using var index = CreateBTreeIndex(_vol);
        const int count = 60;
        for (long i = 0; i < count; i++)
            index.Insert(i, MakeAddr(i), LogicalAddress.Empty);

        index.Delete(7).Should().BeTrue();
        index.Find(7).Should().Be(LogicalAddress.Empty, "深层叶删除后不应命中");
        index.EntryCount.Should().Be(count - 1);
        index.Find(8).Should().Be(MakeAddr(8), "同叶邻居不受扰");

        int removed = 1;   // key 7 已删
        for (long i = 0; i < count; i += 7)
        {
            var del = index.Delete(i);
            if (i == 7) del.Should().BeFalse("key 7 已删，重复删除应返回 false");
            else
            {
                del.Should().BeTrue();
                removed++;
            }
        }
        index.EntryCount.Should().Be(count - removed);
        for (long i = 0; i < count; i++)
        {
            if (i % 7 == 0)
                index.Find(i).Should().Be(LogicalAddress.Empty);
            else
                index.Find(i).Should().Be(MakeAddr(i));
        }
    }

    [Fact]
    public void ScanCursor_Direction_MatchesParameter()
    {
        using var index = CreateBTreeIndex(_vol);
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        cursor.Direction.Should().Be(ReadDirection.Forward);
    }

    // ══════════════════════════════════════════════════════════
    // TruncatePrefix（键序前缀批量删——retention trim 契约）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void TruncatePrefix_EmptyTree_ReturnsZero()
    {
        using var index = CreateBTreeIndex(_vol);
        index.TruncatePrefix(100).Should().Be(0);
    }

    [Fact]
    public void TruncatePrefix_BelowMin_ReturnsZero()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncatePrefix(5).Should().Be(0);
        index.EntryCount.Should().Be(3);
    }

    [Fact]
    public void TruncatePrefix_SingleLeaf_PartialRemoval()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30, 40, 50 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncatePrefix(30).Should().Be(2, "删除 key < 30 的 {10, 20}");
        index.EntryCount.Should().Be(3);
        index.Find(10).Should().Be(LogicalAddress.Empty);
        index.Find(20).Should().Be(LogicalAddress.Empty);
        index.Find(30).Should().Be(MakeAddr(30), "边界键本身不删（严格 <）");
    }

    [Fact]
    public void TruncatePrefix_MultiLeaf_DeletesPrefixRegionOnly()
    {
        using var index = CreateBTreeIndex(_vol);
        const long count = 60;   // 多叶多分裂形态
        for (long k = 0; k < count; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncatePrefix(25).Should().Be(25);
        index.EntryCount.Should().Be(35);

        for (long k = 0; k < 25; k++)
            index.Find(k).Should().Be(LogicalAddress.Empty, $"key {k} 应已删");
        for (long k = 25; k < count; k++)
            index.Find(k).Should().Be(MakeAddr(k), $"key {k} 应存活");

        // 查询面全量一致：扫描 / Max / Floor
        using (var cursor = index.CreateScanCursor(ReadDirection.Forward))
        {
            var delivered = new List<long>();
            while (cursor.MoveNext())
                delivered.Add(cursor.CurrentKey);
            delivered.Should().Equal(Enumerable.Range(25, 35).Select(k => (long)k),
                "扫描跨清零叶交付存活后缀");
        }
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(59);
        index.TryGetFloor(24, out _, out _).Should().BeFalse("前驱区全空");
        index.TryGetFloor(25, out var floorKey, out _).Should().BeTrue();
        floorKey.Should().Be(25);
    }

    [Fact]
    public void TruncatePrefix_EntireRange_EmptiesIndex()
    {
        using var index = CreateBTreeIndex(_vol);
        for (long k = 0; k < 30; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncatePrefix(long.MaxValue).Should().Be(30);
        index.EntryCount.Should().Be(0);
        index.TryGetMax(out _, out _).Should().BeFalse();
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        cursor.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void TruncatePrefix_RepeatedRetentionLoop_Converges()
    {
        using var index = CreateBTreeIndex(_vol);
        for (long k = 0; k < 100; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        // 模拟 retention 循环：分段推进
        index.TruncatePrefix(20).Should().Be(20);
        index.TruncatePrefix(20).Should().Be(0, "重复同界幂等");
        index.TruncatePrefix(60).Should().Be(40);
        index.EntryCount.Should().Be(40);
        index.TryGetFloor(59, out _, out _).Should().BeFalse();
        index.TryGetFloor(60, out var floorKey, out _).Should().BeTrue();
        floorKey.Should().Be(60);

        // 截断后再插入新键（时序延迟写场景形态）
        index.Insert(150, MakeAddr(150), LogicalAddress.Empty);
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(150);
    }

    [Fact]
    public void TruncatePrefix_Randomized_MatchesOracle()
    {
        using var index = CreateBTreeIndex(_vol);
        var rng = new Random(7);
        var keys = new HashSet<long>();
        while (keys.Count < 200)
            keys.Add(rng.NextInt64(0, 100_000));
        foreach (var k in keys)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        for (int round = 0; round < 5; round++)
        {
            var bound = rng.NextInt64(0, 100_000);
            var expected = keys.Count(k => k < bound);
            index.TruncatePrefix(bound).Should().Be(expected, $"round={round} bound={bound}");
            keys.RemoveWhere(k => k < bound);

            index.EntryCount.Should().Be(keys.Count);
            var sorted = keys.OrderBy(k => k).ToList();
            if (sorted.Count > 0)
            {
                index.TryGetMax(out var maxKey, out _).Should().BeTrue();
                maxKey.Should().Be(sorted[^1]);
            }
            foreach (var probe in sorted.Take(10))
                index.Find(probe).Should().Be(MakeAddr(probe), $"round={round} 存活键 {probe} 可查");
        }
    }
}
