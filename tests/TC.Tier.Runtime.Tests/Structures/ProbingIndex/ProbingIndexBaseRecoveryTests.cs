namespace TC.Tier.Runtime.Tests.Structures.ProbingIndex;

/// <summary>
/// 探测族自建恢复测试（设计稿 §4 两段式）——假 resolver 吐假流即可测重建，不需真 Ring。
/// <para>★ 恢复核心 = 建<b>空结构</b>后拉 <see cref="MockKeyResolver{TKey}"/>.ScanAsync 窗口流逐条 Insert 自填桶；
///   判等闭环/tag 冲突/同 key 覆盖语义在重放中原样生效（流序折叠 = 最新写胜出）。</para>
/// </summary>
public class ProbingIndexBaseRecoveryTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public ProbingIndexBaseRecoveryTests()
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

    [Fact]
    public void Replay_FullWindow_RebuildsAllEntries()
    {
        const int count = 100;
        var resolver = new MockKeyResolver<long>();
        for (long i = 0; i < count; i++)
            resolver.Put(MakeAddr(i * 10), i);

        var settings = TestProbingIndexSettingsFactory.On(_vol, "hash", hashTableCapacity: 1 << 10);
        using var index = TestProbingIndexSettingsFactory.NewHash<long>(_vol, settings, resolver,
            hints: new ProbingIndexRecoveryHints(LogicalAddress.Empty, MakeAddr(count * 10)));

        index.EntryCount.Should().Be(count, "全量窗口重放应重建全部条目");
        for (long i = 0; i < count; i++)
            index.Find(i).Should().Be(MakeAddr(i * 10), $"key {i} 重放后应命中");
        index.Find(-1).Should().Be(LogicalAddress.Empty, "未插入的 key 不应命中");
    }

    [Fact]
    public void Replay_LastWriteWins_SameKeyOverwritten()
    {
        // 同 key 两条 record（旧地址 + 新地址）——流序折叠后只剩最新写
        var resolver = new MockKeyResolver<long>();
        resolver.Put(MakeAddr(10), 42);
        resolver.Put(MakeAddr(99), 42);

        var settings = TestProbingIndexSettingsFactory.On(_vol, "hash");
        using var index = TestProbingIndexSettingsFactory.NewHash<long>(_vol, settings, resolver,
            hints: new ProbingIndexRecoveryHints(LogicalAddress.Empty, MakeAddr(100)));

        index.Find(42).Should().Be(MakeAddr(99), "重放后同 key 应取最新写");
        index.EntryCount.Should().Be(1, "同 key 覆盖后只有一条条目");
    }

    [Fact]
    public void Replay_Window_ExcludesOutOfRangeRecords()
    {
        // 五条 record 落 (0,10)..(0,50)；窗口 [ (0,25), (0,50) ) 只含 30/40（半开区间：50 排除）
        var resolver = new MockKeyResolver<long>();
        for (long i = 1; i <= 5; i++)
            resolver.Put(MakeAddr(i * 10), i);

        var settings = TestProbingIndexSettingsFactory.On(_vol, "hash");
        using var index = TestProbingIndexSettingsFactory.NewHash<long>(_vol, settings, resolver,
            hints: new ProbingIndexRecoveryHints(MakeAddr(25), MakeAddr(50)));

        index.EntryCount.Should().Be(2, "窗口外 record（10/20/50）不应重放");
        index.Find(3).Should().Be(MakeAddr(30));
        index.Find(4).Should().Be(MakeAddr(40));
        index.Find(1).Should().Be(LogicalAddress.Empty, "窗口前的 record 不应重放");
        index.Find(5).Should().Be(LogicalAddress.Empty, "窗口末端（半开）不应重放");
    }

    [Fact]
    public void NoWindow_DefaultHints_BuildsEmptyStructure()
    {
        var resolver = new MockKeyResolver<long>();
        resolver.Put(MakeAddr(10), 1);   // 流里有数据，但无窗口=空结构首开，不重放

        var settings = TestProbingIndexSettingsFactory.On(_vol, "hash");
        using var index = TestProbingIndexSettingsFactory.NewHash<long>(_vol, settings, resolver);

        index.EntryCount.Should().Be(0, "默认 hints（无窗口）= 空结构首开，不拉流");
        index.Find(1).Should().Be(LogicalAddress.Empty);
    }

    [Fact]
    public void Replay_EmptyWindow_BeginEqualsEnd_NoReplay()
    {
        var resolver = new MockKeyResolver<long>();
        resolver.Put(MakeAddr(10), 1);

        var settings = TestProbingIndexSettingsFactory.On(_vol, "hash");
        using var index = TestProbingIndexSettingsFactory.NewHash<long>(_vol, settings, resolver,
            hints: new ProbingIndexRecoveryHints(MakeAddr(10), MakeAddr(10)));

        index.EntryCount.Should().Be(0, "空窗口（Begin==End）无效，不重放");
    }

    [Fact]
    public void Replay_TagCollisionEntries_JudgmentLoopHoldsDuringReplay()
    {
        // 同 bucket 同 tag 的两个 key 同窗重放——重放路径的判等闭环必须各建各的条目、Find 不串值
        var (k1, k2, capacity) = FindTagCollision();
        var resolver = new MockKeyResolver<long>();
        resolver.Put(MakeAddr(111), k1);
        resolver.Put(MakeAddr(222), k2);

        var settings = TestProbingIndexSettingsFactory.On(_vol, "hash",
            hashTableCapacity: capacity, overflowPoolCapacity: 1 << 18);
        using var index = TestProbingIndexSettingsFactory.NewHash<long>(_vol, settings, resolver,
            hints: new ProbingIndexRecoveryHints(LogicalAddress.Empty, MakeAddr(1000)));

        index.EntryCount.Should().Be(2, "同 tag 异 key 应是两个独立条目");
        index.Find(k1).Should().Be(MakeAddr(111), $"key {k1} 不应被 {k2} 串值");
        index.Find(k2).Should().Be(MakeAddr(222), $"key {k2} 不应被 {k1} 串值");
    }

    // ★ 复刻 ProbingIndexBase 路由数学（KeyComparer XxHash64 → tag/bucket），构造真 tag 冲突对
    private static readonly KeyComparer<long> KeyCmp = new();

    static ushort ComputeTag(long key) => (ushort)(KeyCmp.GetHashCode64(key) >> 50);
    static long BucketOf(long key, long mask) => (long)(KeyCmp.GetHashCode64(key) & (ulong)mask);

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
}
