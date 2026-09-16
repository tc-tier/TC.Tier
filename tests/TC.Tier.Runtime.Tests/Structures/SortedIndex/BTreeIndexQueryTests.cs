namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

/// <summary>
/// BTreeIndex 位置化查询契约测试（SeekLowerBound / TryGetMax / TryGetFloor——BTreeIndex.Query.cs +
/// BTreeIndex.Cursor.cs 定位化）。
/// <para>★ 契约：Seek 后 Current 即首个 ≥ key 的条目、MoveNext 产出后续；Max/Floor 为键序
///   Latest/采样语义（含等值）；空叶（删除不重平衡的产物）不得破坏任何查询路径。</para>
/// </summary>
public class BTreeIndexQueryTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public BTreeIndexQueryTests()
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

    static BTreeIndex<long> CreateBTreeIndex(TestVolume vol, string name = "bt")
    {
        var settings = TestSortedIndexSettingsFactory.BTreeOn(vol, name);
        return TestSortedIndexSettingsFactory.NewBTree<long>(vol, settings);
    }

    static LogicalAddress MakeAddr(long offset) => new(0, offset);

    [Fact]
    public void ScanCursor_Direction_HonestContract()
    {
        using var index = CreateBTreeIndex(_vol);
        index.Invoking(i => i.CreateScanCursor(ReadDirection.Backward))
            .Should().Throw<NotSupportedException>(
                "叶链无 Prev 指针——Backward 显式抛优于谎报（旧形接受参数恒前向产出）");
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        cursor.Direction.Should().Be(ReadDirection.Forward);
    }

    // ══════════════════════════════════════════════════════════
    // SeekLowerBound（定位化游标）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Seek_EmptyTree_ReturnsFalse()
    {
        using var index = CreateBTreeIndex(_vol);
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);

        cursor.SeekLowerBound(42).Should().BeFalse();
        cursor.MoveNext().Should().BeFalse("空树 seek 后游标置于末尾");
    }

    [Fact]
    public void Seek_BeforeFirst_PositionsAtFirstEntry()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);

        cursor.SeekLowerBound(5).Should().BeTrue();
        cursor.CurrentKey.Should().Be(10);
        cursor.MoveNext().Should().BeTrue();
        cursor.CurrentKey.Should().Be(20, "MoveNext 交付定位条目的后续");
    }

    [Fact]
    public void Seek_ExactKey_PositionsAtKey()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);

        cursor.SeekLowerBound(20).Should().BeTrue();
        cursor.CurrentKey.Should().Be(20, "lower_bound 含等值");
        cursor.CurrentValue.Should().Be(MakeAddr(20));
    }

    [Fact]
    public void Seek_BetweenKeys_PositionsAtNextGreater()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);

        cursor.SeekLowerBound(21).Should().BeTrue();
        cursor.CurrentKey.Should().Be(30);
    }

    [Fact]
    public void Seek_AfterLast_ReturnsFalse()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);

        cursor.SeekLowerBound(31).Should().BeFalse();
        cursor.MoveNext().Should().BeFalse("末尾定位后 MoveNext 恒 false");
    }

    [Fact]
    public void Seek_MultiLeaf_DeliversSortedSuffix()
    {
        using var index = CreateBTreeIndex(_vol);
        // 200 键（叶容量 9——强制多叶多分裂），乱序插入
        var keys = Enumerable.Range(0, 200).Select(k => (long)k).ToArray();
        var rng = new Random(1234);
        foreach (var k in keys.OrderBy(_ => rng.Next()))
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        cursor.SeekLowerBound(137).Should().BeTrue();
        var delivered = new List<long>();
        delivered.Add(cursor.CurrentKey);
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);

        delivered.Should().Equal(Enumerable.Range(137, 200 - 137).Select(k => (long)k),
            "seek 定位后交付有序后缀 [137, 200)");
    }

    [Fact]
    public void Seek_MidIteration_RepositionsForward()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in Enumerable.Range(0, 50).Select(k => (long)k))
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);

        // 先迭代几条，再前跳重定位
        cursor.SeekLowerBound(10).Should().BeTrue();
        cursor.MoveNext();
        cursor.MoveNext();
        cursor.CurrentKey.Should().Be(12);

        cursor.SeekLowerBound(40).Should().BeTrue("迭代中可重新定位");
        cursor.CurrentKey.Should().Be(40);
        var count = 1;
        while (cursor.MoveNext()) count++;
        count.Should().Be(10, "重定位后交付 [40, 50)");
    }

    [Fact]
    public void Scan_AfterEmptyingLeaves_SkipsEmptyLeaves()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in Enumerable.Range(0, 50).Select(k => (long)k))
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        // 清空大片键（删除不重平衡 → 必然留下 Count=0 的叶）
        foreach (var k in Enumerable.Range(1, 47).Select(k => (long)k))
            index.Delete(k);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Should().Equal(new long[] { 0, 48, 49 },
            "空叶不是扫描终点——前向扫描必须跳过 Count=0 的叶");
    }

    [Fact]
    public void Seek_AfterEmptyingLeaves_StillPositions()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in Enumerable.Range(0, 50).Select(k => (long)k))
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);
        foreach (var k in Enumerable.Range(1, 47).Select(k => (long)k))
            index.Delete(k);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        cursor.SeekLowerBound(20).Should().BeTrue("seek 跨越空叶区域定位到 48");
        cursor.CurrentKey.Should().Be(48);
        cursor.MoveNext().Should().BeTrue();
        cursor.CurrentKey.Should().Be(49);
    }

    // ══════════════════════════════════════════════════════════
    // TryGetMax（Latest 语义）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Max_EmptyTree_ReturnsFalse()
    {
        using var index = CreateBTreeIndex(_vol);
        index.TryGetMax(out var key, out var value).Should().BeFalse();
        key.Should().Be(0);
        value.Should().Be(LogicalAddress.Empty);
    }

    [Fact]
    public void Max_ReverseInserts_ReturnsLargest()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 30, 10, 20 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TryGetMax(out var key, out var value).Should().BeTrue();
        key.Should().Be(30);
        value.Should().Be(MakeAddr(30));
    }

    [Fact]
    public void Max_AfterDeletingMax_ReturnsNewMax()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in Enumerable.Range(0, 30).Select(k => (long)k))
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.Delete(29).Should().BeTrue();
        index.TryGetMax(out var key, out _).Should().BeTrue();
        key.Should().Be(28);
    }

    [Fact]
    public void Max_AfterEmptyingTailLeaves_FallsBackAcrossEmptyLeaf()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in Enumerable.Range(0, 60).Select(k => (long)k))
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        // 清空尾部大片（末叶必空——右下降落空叶走分离键 floor 兜底路径）
        foreach (var k in Enumerable.Range(40, 20).Select(k => (long)k))
            index.Delete(k);

        index.TryGetMax(out var key, out var value).Should().BeTrue();
        key.Should().Be(39);
        value.Should().Be(MakeAddr(39));
    }

    // ══════════════════════════════════════════════════════════
    // TryGetFloor（采样语义——含等值）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Floor_EmptyTree_ReturnsFalse()
    {
        using var index = CreateBTreeIndex(_vol);
        index.TryGetFloor(42, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Floor_BelowAllKeys_ReturnsFalse()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TryGetFloor(9, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Floor_ExactKey_ReturnsKeyItself()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TryGetFloor(20, out var key, out var value).Should().BeTrue();
        key.Should().Be(20, "floor 含等值");
        value.Should().Be(MakeAddr(20));
    }

    [Fact]
    public void Floor_BetweenKeys_ReturnsPrevious()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TryGetFloor(25, out var key, out _).Should().BeTrue();
        key.Should().Be(20);
    }

    [Fact]
    public void Floor_AboveAllKeys_ReturnsMax()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in new long[] { 10, 20, 30 })
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        index.TryGetFloor(long.MaxValue, out var key, out _).Should().BeTrue();
        key.Should().Be(30);
    }

    [Fact]
    public void Floor_AfterEmptyingMidLeaves_FallsBackToLeftNeighbor()
    {
        using var index = CreateBTreeIndex(_vol);
        foreach (var k in Enumerable.Range(0, 60).Select(k => (long)k))
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        // 清空中段（下降落空叶时走左邻子树回退）
        foreach (var k in Enumerable.Range(25, 15).Select(k => (long)k))
            index.Delete(k);

        // 28 已删：floor(28) 必须回退到 24（跨空叶区域）
        index.TryGetFloor(28, out var key, out var value).Should().BeTrue();
        key.Should().Be(24);
        value.Should().Be(MakeAddr(24));

        // 尾段不受影响
        index.TryGetFloor(50, out key, out _).Should().BeTrue();
        key.Should().Be(50);
    }

    // ══════════════════════════════════════════════════════════
    // 随机属性测试（对照有序表 oracle——多分裂形态下的全路径覆盖）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Randomized_SeekFloorMax_MatchSortedOracle()
    {
        using var index = CreateBTreeIndex(_vol);
        var rng = new Random(2026);
        var keys = new HashSet<long>();
        while (keys.Count < 300)
            keys.Add(rng.NextInt64(0, 100_000));
        var sorted = keys.OrderBy(k => k).ToList();
        foreach (var k in sorted)
            index.Insert(k, MakeAddr(k), LogicalAddress.Empty);

        // TryGetMax = oracle 末位
        index.TryGetMax(out var maxKey, out var maxValue).Should().BeTrue();
        maxKey.Should().Be(sorted[^1]);
        maxValue.Should().Be(MakeAddr(sorted[^1]));

        // 探针集：全部已插入键 ± 1 + 若干随机
        var probes = new List<long>();
        foreach (var k in sorted.Take(50))
        {
            probes.Add(k);
            probes.Add(k + 1);
        }
        for (int i = 0; i < 100; i++)
            probes.Add(rng.NextInt64(0, 100_000));

        foreach (var probe in probes)
        {
            // Floor 契约
            var expectedFloor = sorted.LastOrDefault(k => k <= probe, defaultValue: -1);
            var gotFloor = index.TryGetFloor(probe, out var floorKey, out _);
            if (expectedFloor == -1)
            {
                gotFloor.Should().BeFalse($"probe={probe} 无 ≤ 它的键");
            }
            else
            {
                gotFloor.Should().BeTrue($"probe={probe}");
                floorKey.Should().Be(expectedFloor, $"probe={probe} floor 契约");
            }

            // Seek 契约：交付有序后缀起点 = oracle 首个 ≥ probe
            var expectedFirst = sorted.FirstOrDefault(k => k >= probe, defaultValue: -1);
            using var cursor = index.CreateScanCursor(ReadDirection.Forward);
            var seeked = cursor.SeekLowerBound(probe);
            if (expectedFirst == -1)
            {
                seeked.Should().BeFalse($"probe={probe} 无 ≥ 它的键");
            }
            else
            {
                seeked.Should().BeTrue($"probe={probe}");
                cursor.CurrentKey.Should().Be(expectedFirst, $"probe={probe} seek 契约");
            }
        }
    }
}
