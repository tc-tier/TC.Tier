using TC.Tier.Runtime.Tests.Structures.ProbingIndex;

namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

/// <summary>
/// 比较族自建恢复测试（设计稿 §4 两段式）——假 resolver 吐假流即可测重建，不需真 Ring。
/// <para>★ 恢复核心 = 建<b>空结构</b>后拉 ScanAsync 窗口流逐条 Insert 增量插节点；
///   比较路由/同 key 覆盖语义在重放中原样生效（流序折叠 = 最新写胜出），重建后有序遍历可用。</para>
/// <para>★ KeyResolver 可选注入：判等不需要（key 物化条目内），重放需要——有窗无 resolver 恢复期 fail-fast。</para>
/// </summary>
public class SortedIndexBaseRecoveryTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public SortedIndexBaseRecoveryTests()
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

    static LogicalAddress MakeAddr(long offset) => new(0, offset);

    // ════ BTreeIndex ════

    [Fact]
    public void BTree_Replay_FullWindow_RebuildsAllEntries_OrderedScanHolds()
    {
        const int count = 60;   // 跨叶分裂规模
        var resolver = new MockKeyResolver<long>();
        var keys = Enumerable.Range(0, count).Select(i => (long)i).OrderBy(_ => Random.Shared.Next()).ToArray();
        foreach (var k in keys)
            resolver.Put(MakeAddr(k * 10), k);

        var settings = TestSortedIndexSettingsFactory.BTreeOn(_vol, "bt");
        using var index = TestSortedIndexSettingsFactory.NewBTree<long>(_vol, settings,
            keyResolver: resolver,
            hints: new SortedIndexRecoveryHints(LogicalAddress.Empty, MakeAddr(count * 10)));

        index.EntryCount.Should().Be(count);
        for (long i = 0; i < count; i++)
            index.Find(i).Should().Be(MakeAddr(i * 10), $"key {i} 重放后应命中");

        // 比较族重建后有序遍历必须成立（乱序写入 → 有序读出）
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        long expected = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().Be(expected);
            cursor.CurrentValue.Should().Be(MakeAddr(expected * 10));
            expected++;
        }
        expected.Should().Be(count);
    }

    [Fact]
    public void BTree_Replay_LastWriteWins_SameKeyOverwritten()
    {
        var resolver = new MockKeyResolver<long>();
        resolver.Put(MakeAddr(10), 42);
        resolver.Put(MakeAddr(99), 42);

        var settings = TestSortedIndexSettingsFactory.BTreeOn(_vol, "bt");
        using var index = TestSortedIndexSettingsFactory.NewBTree<long>(_vol, settings,
            keyResolver: resolver,
            hints: new SortedIndexRecoveryHints(LogicalAddress.Empty, MakeAddr(100)));

        index.Find(42).Should().Be(MakeAddr(99), "重放后同 key 应取最新写");
        index.EntryCount.Should().Be(1);
    }

    [Fact]
    public void BTree_Replay_Window_ExcludesOutOfRangeRecords()
    {
        var resolver = new MockKeyResolver<long>();
        for (long i = 1; i <= 5; i++)
            resolver.Put(MakeAddr(i * 10), i);

        var settings = TestSortedIndexSettingsFactory.BTreeOn(_vol, "bt");
        using var index = TestSortedIndexSettingsFactory.NewBTree<long>(_vol, settings,
            keyResolver: resolver,
            hints: new SortedIndexRecoveryHints(MakeAddr(25), MakeAddr(50)));

        index.EntryCount.Should().Be(2, "窗口外 record（10/20/50）不应重放");
        index.Find(3).Should().Be(MakeAddr(30));
        index.Find(4).Should().Be(MakeAddr(40));
        index.Find(1).Should().Be(LogicalAddress.Empty);
        index.Find(5).Should().Be(LogicalAddress.Empty, "窗口末端（半开）不应重放");
    }

    [Fact]
    public void BTree_Replay_WithoutResolver_FailsRecovery()
    {
        // 有重放窗口但未注入 IKeyResolver——恢复核心 fail-fast（实例不可修复，须 Dispose 重建）
        var resolver = new MockKeyResolver<long>();
        resolver.Put(MakeAddr(10), 1);

        var settings = TestSortedIndexSettingsFactory.BTreeOn(_vol, "bt");
        var index = new BTreeIndex<long>(_vol.Fs, settings);   // 无 keyResolver
        try
        {
            index.Initialize(new SortedIndexRecoveryHints(LogicalAddress.Empty, MakeAddr(100)));

            // 消除时序：等恢复失败终态登记后，WaitForReady 的守卫路径才确定性重抛
            SpinWait.SpinUntil(() => index.RecoveryState.Phase == RecoveryPhase.Failed, 5000)
                .Should().BeTrue("fail-fast 应使恢复进入 Failed 终态");
            var act = () => index.WaitForReady();
            act.Should().Throw<InvalidOperationException>()
               .Which.InnerException.Should().BeOfType<InvalidOperationException>(
                   "Inner 应是恢复核心的原生 fail-fast 异常");
        }
        finally
        {
            index.Dispose();
        }
    }

    // ════ SkipListIndex ════

    [Fact]
    public void SkipList_Replay_FullWindow_RebuildsAllEntries_OrderedScanHolds()
    {
        const int count = 200;
        var resolver = new MockKeyResolver<long>();
        var keys = Enumerable.Range(0, count).Select(i => (long)i).OrderBy(_ => Random.Shared.Next()).ToArray();
        foreach (var k in keys)
            resolver.Put(MakeAddr(k * 10), k);

        var settings = TestSortedIndexSettingsFactory.SkipListOn(_vol, "sl");
        using var index = TestSortedIndexSettingsFactory.NewSkipList<long>(_vol, settings,
            keyResolver: resolver,
            hints: new SortedIndexRecoveryHints(LogicalAddress.Empty, MakeAddr(count * 10)));

        index.EntryCount.Should().Be(count);
        for (long i = 0; i < count; i++)
            index.Find(i).Should().Be(MakeAddr(i * 10), $"key {i} 重放后应命中");

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        long prev = long.MinValue;
        int scanned = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().BeGreaterThan(prev);
            prev = cursor.CurrentKey;
            scanned++;
        }
        scanned.Should().Be(count);
    }

    [Fact]
    public void SkipList_Replay_LastWriteWins_SameKeyOverwritten()
    {
        var resolver = new MockKeyResolver<long>();
        resolver.Put(MakeAddr(10), 42);
        resolver.Put(MakeAddr(99), 42);

        var settings = TestSortedIndexSettingsFactory.SkipListOn(_vol, "sl");
        using var index = TestSortedIndexSettingsFactory.NewSkipList<long>(_vol, settings,
            keyResolver: resolver,
            hints: new SortedIndexRecoveryHints(LogicalAddress.Empty, MakeAddr(100)));

        index.Find(42).Should().Be(MakeAddr(99), "重放后同 key 应取最新写");
        index.EntryCount.Should().Be(1);
    }

    [Fact]
    public void NoWindow_DefaultHints_BuildsEmptyStructure()
    {
        var resolver = new MockKeyResolver<long>();
        resolver.Put(MakeAddr(10), 1);   // 流里有数据，但无窗口=空结构首开，不重放

        var btSettings = TestSortedIndexSettingsFactory.BTreeOn(_vol, "bt-empty");
        using var bt = TestSortedIndexSettingsFactory.NewBTree<long>(_vol, btSettings, keyResolver: resolver);
        bt.EntryCount.Should().Be(0, "默认 hints（无窗口）= 空结构首开，不拉流");

        var slSettings = TestSortedIndexSettingsFactory.SkipListOn(_vol, "sl-empty");
        using var sl = TestSortedIndexSettingsFactory.NewSkipList<long>(_vol, slSettings, keyResolver: resolver);
        sl.EntryCount.Should().Be(0);
    }
}
