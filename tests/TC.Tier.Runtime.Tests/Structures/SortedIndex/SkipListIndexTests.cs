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
}
