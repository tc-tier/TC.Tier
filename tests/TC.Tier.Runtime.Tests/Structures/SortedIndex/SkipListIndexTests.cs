namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

public class SkipListIndexTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public SkipListIndexTests()
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

    static SkipListIndex<long> CreateSkipListIndex(TestVolume vol, int maxLevel = 12)
    {
        var settings = TestSortedIndexSettingsFactory.SkipListOn(vol, "sl", maxLevel);
        // SkipListIndex 节点内嵌 key,不依赖 IKeyResolver 判等闭环（比较族条目物化 key）。
        return TestSortedIndexSettingsFactory.NewSkipList<long>(vol, settings);
    }

    static LogicalAddress MakeAddr(long offset) => new(0, offset);

    [Fact]
    public void Insert_Find_RoundTrip()
    {
        using var index = CreateSkipListIndex(_vol);
        var value = MakeAddr(42);

        var inserted = index.Insert(100, value, LogicalAddress.Empty);
        inserted.Should().Be(value);

        var found = index.Find(100);
        found.Should().Be(value);
    }

    [Fact]
    public void Insert_MultipleKeys_RandomOrder_AllFound()
    {
        using var index = CreateSkipListIndex(_vol);
        var keys = Enumerable.Range(0, 50).OrderBy(_ => Random.Shared.Next()).ToArray();

        foreach (var key in keys)
            index.Insert(key, MakeAddr(key * 10), LogicalAddress.Empty);

        foreach (var key in keys)
        {
            var found = index.Find(key);
            found.Should().Be(MakeAddr(key * 10));
        }
    }

    [Fact]
    public void ScanCursor_Forward_AscendingOrder()
    {
        using var index = CreateSkipListIndex(_vol);
        for (int i = 1; i <= 100; i++)
            index.Insert(i, MakeAddr(i), LogicalAddress.Empty);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        long prev = long.MinValue;
        int count = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().BeGreaterThan(prev);
            cursor.CurrentValue.Should().Be(MakeAddr(cursor.CurrentKey));
            prev = cursor.CurrentKey;
            count++;
        }
        count.Should().Be(100);
    }

    [Fact]
    public void Find_NonExistent_ReturnsEmpty()
    {
        using var index = CreateSkipListIndex(_vol);
        index.Insert(1, MakeAddr(1), LogicalAddress.Empty);

        var found = index.Find(999);
        found.Should().Be(LogicalAddress.Empty);
    }

    [Fact]
    public void Delete_Existing_RemovesEntry()
    {
        using var index = CreateSkipListIndex(_vol);
        index.Insert(50, MakeAddr(500), LogicalAddress.Empty);

        var deleted = index.Delete(50);
        deleted.Should().BeTrue();
        index.Find(50).Should().Be(LogicalAddress.Empty);
    }

    [Fact]
    public void Delete_NonExistent_ReturnsFalse()
    {
        using var index = CreateSkipListIndex(_vol);
        index.Insert(1, MakeAddr(1), LogicalAddress.Empty);

        var deleted = index.Delete(999);
        deleted.Should().BeFalse();
    }

    [Fact]
    public void LargeBatch_1000Keys_RandomOrder_AllReadable()
    {
        using var index = CreateSkipListIndex(_vol);
        var keys = Enumerable.Range(0, 1000).OrderBy(_ => Random.Shared.Next()).ToArray();

        foreach (var key in keys)
            index.Insert(key, MakeAddr(key), LogicalAddress.Empty);

        foreach (var key in keys)
        {
            var found = index.Find(key);
            found.Should().Be(MakeAddr(key));
        }
    }

    [Fact]
    public void SequentialKeys_Insertion_OrderedScan()
    {
        using var index = CreateSkipListIndex(_vol);
        for (int i = 0; i < 200; i++)
            index.Insert(i, MakeAddr(i * 2), LogicalAddress.Empty);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        int expected = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().Be(expected);
            cursor.CurrentValue.Should().Be(MakeAddr(expected * 2));
            expected++;
        }
        expected.Should().Be(200);
    }

    [Fact]
    public void LevelDistribution_NoCrash()
    {
        using var index = CreateSkipListIndex(_vol, maxLevel: 16);
        for (int i = 0; i < 5000; i++)
            index.Insert(i, MakeAddr(i), LogicalAddress.Empty);

        for (int i = 0; i < 5000; i += 500)
            index.Find(i).Should().Be(MakeAddr(i));
    }

    [Fact]
    public void Insert_DuplicateKey_Overwrites()
    {
        // ★ 同 key 重复 Insert 原位覆写（旧码无判重——双节点/EntryCount 虚高/遍历吐重复 key）
        using var index = CreateSkipListIndex(_vol);
        var first = MakeAddr(100);
        var second = MakeAddr(200);

        index.Insert(1, first, LogicalAddress.Empty);
        index.Insert(1, second, LogicalAddress.Empty);

        index.Find(1).Should().Be(second, "同 key 覆盖后应取最新值");
        index.EntryCount.Should().Be(1, "同 key 覆盖不建新节点、不推计数");

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        int scanned = 0;
        while (cursor.MoveNext()) scanned++;
        scanned.Should().Be(1, "遍历不应吐重复 key");
    }

    [Fact]
    public void EntryCount_Accuracy()
    {
        using var index = CreateSkipListIndex(_vol);
        index.EntryCount.Should().Be(0);

        for (int i = 0; i < 10; i++)
            index.Insert(i, MakeAddr(i), LogicalAddress.Empty);
        index.EntryCount.Should().Be(10);

        index.Delete(3);
        index.EntryCount.Should().Be(9);

        index.Delete(7);
        index.EntryCount.Should().Be(8);
    }

    // ══════════════════════════════════════════════════════════
    // TruncatePrefix（键序前缀批量删——retention trim 契约，与 BTree 同矩阵）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void TruncatePrefix_EmptyTable_ReturnsZero()
    {
        using var index = CreateSkipListIndex(_vol);
        index.TruncatePrefix(100).Should().Be(0);
    }

    [Fact]
    public void TruncatePrefix_BelowMin_ReturnsZero()
    {
        using var index = CreateSkipListIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncatePrefix(5).Should().Be(0);
        index.EntryCount.Should().Be(3);
    }

    [Fact]
    public void TruncatePrefix_PartialRemoval_KeepsBoundaryAndRest()
    {
        using var index = CreateSkipListIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30, 40, 50 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncatePrefix(30).Should().Be(2, "删除 key < 30 的 {10, 20}");
        index.EntryCount.Should().Be(3);
        index.Find(10).Should().Be(LogicalAddress.Empty);
        index.Find(30).Should().Be(MakeAddr(30), "边界键本身不删（严格 <）");
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(50);
    }

    [Fact]
    public void TruncatePrefix_MultiNode_DeletesPrefixRegionOnly()
    {
        using var index = CreateSkipListIndex(_vol);
        const long count = 200;
        var rng = new Random(42);
        var keys = Enumerable.Range(0, (int)count).Select(k => (long)k).OrderBy(_ => rng.Next()).ToList();
        foreach (var k in keys)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncatePrefix(120).Should().Be(120);
        index.EntryCount.Should().Be(80);

        for (long k = 0; k < 120; k++)
            index.Find(k).Should().Be(LogicalAddress.Empty, $"key {k} 应已删");
        for (long k = 120; k < count; k++)
            index.Find(k).Should().Be(MakeAddr(k), $"key {k} 应存活");

        // 查询面全量一致：扫描 / Max / Floor
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Should().Equal(Enumerable.Range(120, 80).Select(k => (long)k));
        index.TryGetFloor(119, out _, out _).Should().BeFalse("前驱区全删");
        index.TryGetFloor(120, out var floorKey, out _).Should().BeTrue();
        floorKey.Should().Be(120);
    }

    [Fact]
    public void TruncatePrefix_EntireRange_EmptiesTable()
    {
        using var index = CreateSkipListIndex(_vol);
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
        using var index = CreateSkipListIndex(_vol);
        for (long k = 0; k < 100; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncatePrefix(20).Should().Be(20);
        index.TruncatePrefix(20).Should().Be(0, "重复同界幂等");
        index.TruncatePrefix(60).Should().Be(40);
        index.EntryCount.Should().Be(40);

        // 截断后再插入（跨塔层混合——新节点可能高于既有塔）
        index.Insert(150, MakeAddr(150), LogicalAddress.Empty);
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(150);
        index.EntryCount.Should().Be(41);
    }

    // ══════════════════════════════════════════════════════════
    // TruncateRange（双侧区间删——共享键空间逐前缀域 retention trim 契约，与 BTree 同矩阵）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void TruncateRange_EmptyTable_ReturnsZero()
    {
        using var index = CreateSkipListIndex(_vol);
        index.TruncateRange(10, 20).Should().Be(0);
    }

    [Fact]
    public void TruncateRange_InvertedBounds_ReturnsZero()
    {
        using var index = CreateSkipListIndex(_vol);
        for (long k = 0; k < 10; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);
        index.TruncateRange(20, 10).Should().Be(0, "下界 ≥ 上界 = 空区间 no-op");
        index.EntryCount.Should().Be(10);
    }

    [Fact]
    public void TruncateRange_SingleRegion_MiddleRemoval()
    {
        using var index = CreateSkipListIndex(_vol);
        for (long k = 0; k < 10; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncateRange(3, 7).Should().Be(4, "删除 [3, 7) = {3, 4, 5, 6}");
        index.EntryCount.Should().Be(6);
        for (long k = 0; k < 10; k++)
        {
            var expect = k is >= 3 and < 7 ? LogicalAddress.Empty : MakeAddr(k);
            index.Find(k).Should().Be(expect, $"key {k} 存活性");
        }
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(9, "区间右侧存活——Max 不受影响");
    }

    [Fact]
    public void TruncateRange_MultiNode_KeepsPrefixDomainIntact()
    {
        // dense 逐域 trim 的结构层契约：共享键空间内低前缀域（"更早域"）存活条目不受高前缀域截断波及
        using var index = CreateSkipListIndex(_vol);
        const long count = 300;
        var rng = new Random(7);
        var keys = Enumerable.Range(0, (int)count).Select(k => (long)k).OrderBy(_ => rng.Next()).ToList();
        foreach (var k in keys)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncateRange(100, 250).Should().Be(150);
        index.EntryCount.Should().Be(150);
        for (long k = 0; k < count; k++)
        {
            var expect = k is >= 100 and < 250 ? LogicalAddress.Empty : MakeAddr(k);
            index.Find(k).Should().Be(expect, $"key {k} 存活性");
        }

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Should().Equal(Enumerable.Range(0, 100).Concat(Enumerable.Range(250, 50)).Select(k => (long)k),
            "扫描跨旁路段交付两侧存活区");
        index.TryGetFloor(99, out var floorKey, out _).Should().BeTrue();
        floorKey.Should().Be(99, "前缀域下界以上前驱可查");
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(299);
    }

    [Fact]
    public void TruncateRange_EntireDomain_LeavesEmptyTable()
    {
        using var index = CreateSkipListIndex(_vol);
        for (long k = 0; k < 50; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncateRange(0, 50).Should().Be(50);
        index.EntryCount.Should().Be(0);
        index.TryGetMax(out _, out _).Should().BeFalse("全域清空后 Max 无命中");
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        cursor.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void TruncateRange_TailAndHeadAnchors()
    {
        using var index = CreateSkipListIndex(_vol);
        for (long k = 0; k < 40; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        // 头部段删除（pred = head 各层）
        index.TruncateRange(0, 10).Should().Be(10);
        index.EntryCount.Should().Be(30);
        index.Find(0).Should().Be(LogicalAddress.Empty);
        index.Find(10).Should().Be(MakeAddr(10), "上界键存活（严格 <）");

        // 尾部段删除（succ = Empty 各层）
        index.TruncateRange(30, 40).Should().Be(10);
        index.EntryCount.Should().Be(20);
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(29);

        // 全域 = 头尾锚同删（覆盖剩余全部）
        index.TruncateRange(long.MinValue, long.MaxValue).Should().Be(20);
        index.EntryCount.Should().Be(0);
    }

    [Fact]
    public void TruncateRange_RepeatedRetentionLoop_Converges()
    {
        using var index = CreateSkipListIndex(_vol);
        for (long k = 0; k < 100; k++)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TruncateRange(20, 40).Should().Be(20);
        index.TruncateRange(20, 40).Should().Be(0, "重复同界幂等");
        // 截断后再插入（旁路段回填——新节点塔链接回）
        index.Insert(25, MakeAddr(25), LogicalAddress.Empty);
        index.EntryCount.Should().Be(81);
        index.Find(25).Should().Be(MakeAddr(25));
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Should().Equal(
            Enumerable.Range(0, 20).Select(k => (long)k).Concat(new long[] { 25 })
                .Concat(Enumerable.Range(40, 60).Select(k => (long)k)),
            "回填段并入扫描序");
    }

    [Fact]
    public void TruncateRange_Randomized_MatchesOracle()
    {
        using var index = CreateSkipListIndex(_vol);
        var rng = new Random(13);
        var keys = new HashSet<long>();
        while (keys.Count < 300)
            keys.Add(rng.NextInt64(0, 100_000));
        foreach (var k in keys)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        for (int round = 0; round < 6; round++)
        {
            long lo = rng.NextInt64(0, 100_000);
            long hi = rng.NextInt64(0, 100_000);
            var expected = keys.Count(k => k >= lo && k < hi);
            index.TruncateRange(lo, hi).Should().Be(expected, $"round={round} [{lo}, {hi})");
            keys.RemoveWhere(k => k >= lo && k < hi);

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

    // ══ TryGetPrev（严格前驱——反向步进迭代原语）══

    [Fact]
    public void TryGetPrev_StrictExclusion_AndStepwiseReverseScan()
    {
        using var index = CreateSkipListIndex(_vol);
        const long count = 200;
        for (long k = 0; k < count; k++)
            index.Insert(k * 10, MakeAddr(k * 10), LogicalAddress.Empty);

        // 严格排除自身：等值键不算前驱
        index.TryGetPrev(150, out var prevKey, out var prevVal).Should().BeTrue();
        prevKey.Should().Be(140, "< 语义——等值不算");
        prevVal.Should().Be(MakeAddr(140));

        // 空隙键 = 语义 floor 相同（无等值可排）
        index.TryGetPrev(155, out prevKey, out _).Should().BeTrue();
        prevKey.Should().Be(150);

        // 反向步进迭代（ZRevRange 模式）：TryGetMax 起步逐步 TryGetPrev——与 Forward 反转对照
        index.TryGetMax(out var curKey, out _).Should().BeTrue();
        var reverse = new List<long> { curKey };
        while (index.TryGetPrev(curKey, out curKey, out _))
            reverse.Add(curKey);
        var forward = new List<long>();
        using (var cursor = index.CreateScanCursor(ReadDirection.Forward))
        {
            while (cursor.MoveNext())
                forward.Add(cursor.CurrentKey);
        }
        forward.Reverse();
        reverse.Should().Equal(forward, "反向步进 = Forward 全量反转");

        // 下界之下 miss
        index.TryGetPrev(0, out _, out _).Should().BeFalse("无 < 最小键的条目");
        index.TryGetPrev(-5, out _, out _).Should().BeFalse();

        // TruncateRange 截断后：反向步进全量 = Forward 全量反转（旁路段穿越一致性）
        index.TruncateRange(500, 1500).Should().Be(100);
        index.TryGetMax(out curKey, out _).Should().BeTrue();
        curKey.Should().Be(1990);
        var reverse2 = new List<long> { curKey };
        while (index.TryGetPrev(curKey, out curKey, out _))
            reverse2.Add(curKey);
        reverse2.Should().HaveCount(100, "200 键删 [500, 1500) 段 100 键");
        reverse2[^1].Should().Be(0, "步进到底 = 最小键");
        var forward2 = new List<long>();
        using (var cursor = index.CreateScanCursor(ReadDirection.Forward))
        {
            while (cursor.MoveNext())
                forward2.Add(cursor.CurrentKey);
        }
        forward2.Reverse();
        reverse2.Should().Equal(forward2, "截断后反向步进 = Forward 全量反转");
    }
}
