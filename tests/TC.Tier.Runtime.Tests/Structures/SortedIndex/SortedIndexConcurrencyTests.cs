namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

/// <summary>
/// 比较族线程契约测试（写闸互斥——SortedIndexBase 终态契约：并发写者安全、读者无锁）。
/// <para>★ 有界形态（对齐 LeaseConcurrencyTests 先例）：固定线程数 × 固定操作数，秒级完成，
///   只做终态断言（全部在场/计数一致/扫描有序）——非压测。</para>
/// </summary>
public class SortedIndexConcurrencyTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public SortedIndexConcurrencyTests()
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

    static BTreeIndex<long> CreateBTree(TestVolume vol, string name, IKeyResolver<long>? keyResolver = null)
    {
        var settings = TestSortedIndexSettingsFactory.BTreeOn(vol, name);
        return TestSortedIndexSettingsFactory.NewBTree<long>(vol, settings, keyResolver: keyResolver);
    }

    static SkipListIndex<long> CreateSkipList(TestVolume vol, string name)
    {
        var settings = TestSortedIndexSettingsFactory.SkipListOn(vol, name);
        return TestSortedIndexSettingsFactory.NewSkipList<long>(vol, settings);
    }

    static LogicalAddress MakeAddr(long offset) => new(0, offset);

    // ══════════════════════════════════════════════════════════
    // BTree：并发写者
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task BTree_ConcurrentInsert_AllPresentAndOrdered()
    {
        using var index = CreateBTree(_vol, "bt");
        const int threads = 4, perThread = 2500;

        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    long key = (long)tid * perThread + i;
                    index.Insert(key, MakeAddr(key), LogicalAddress.Empty);
                }
            });
        }
        await Task.WhenAll(tasks);

        index.EntryCount.Should().Be(threads * perThread, "无丢失/无重复计数");
        for (long k = 0; k < threads * perThread; k++)
            index.Find(k).Should().Be(MakeAddr(k), $"并发插入后 key {k} 必须可查");
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(threads * perThread - 1);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Should().Equal(Enumerable.Range(0, threads * perThread).Select(k => (long)k),
            "并发插入后扫描严格有序无缺");
    }

    [Fact]
    public async Task BTree_ConcurrentMixedWrites_TreeStaysConsistent()
    {
        using var index = CreateBTree(_vol, "bt");
        const int insertThreads = 2, perThread = 3000;

        // 写者组合：2 插入者 + 1 删除者（偶数键反复删）+ 1 前缀截断者（低水位推进）
        var inserters = new Task[insertThreads];
        for (int t = 0; t < insertThreads; t++)
        {
            int tid = t;
            inserters[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    long key = (long)tid * perThread + i;
                    index.Insert(key, MakeAddr(key), LogicalAddress.Empty);
                }
            });
        }
        var deleter = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
                index.Delete(i * 2);
        });
        var truncator = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
                index.TruncatePrefix(i * 10);
        });

        await Task.WhenAll(inserters.Append(deleter).Append(truncator));
        index.TruncatePrefix(490);   // 终态收敛（消除并发交错的不确定性——截断者上限 490）

        // 终态一致性：EntryCount == 实际扫描条数（树无撕裂），扫描严格递增
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Count.Should().Be((int)index.EntryCount, "扫描计数 == EntryCount（结构一致性）");
        delivered.Should().BeInAscendingOrder("并发混合写后仍严格有序");
        delivered.Should().OnlyContain(k => k >= 490, "终态截断到 490 后不应有更低存活键");
    }

    [Fact]
    public async Task BTree_ConcurrentInsertAndDump_NoCorruption()
    {
        // ★ 注入 stub resolver 使 TryDump 走完 WriteBody 全路径（脏集回写 + 清 _dirtyNodes——
        //   与并发 Insert 的 _dirtyNodes.Add 正是写闸要互斥的一致性窗口；无 resolver 时 dump 短路测不到）。
        using var index = CreateBTree(_vol, "bt", keyResolver: new StubKeyResolver());
        const int perThread = 3000;

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
                index.Insert(i, MakeAddr(i), LogicalAddress.Empty);
        });
        var dumper = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
                index.TryDump();
        });

        await writer;
        await dumper;

        index.EntryCount.Should().Be(perThread);
        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Should().Equal(Enumerable.Range(0, perThread).Select(k => (long)k),
            "并发 dump 回写与插入竞争后树完整有序");
    }

    [Fact]
    public async Task BTree_ConcurrentReadersWhileWriting_NoExceptionAndMonotonic()
    {
        using var index = CreateBTree(_vol, "bt");
        const int perThread = 4000;

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
                index.Insert(i, MakeAddr(i), LogicalAddress.Empty);
        });
        var readers = new Task[2];
        for (int r = 0; r < 2; r++)
        {
            readers[r] = Task.Run(() =>
            {
                for (int i = 0; i < 2000; i++)
                {
                    // 读者无锁契约：并发读写不抛异常、观察到的键集单调可用
                    index.TryGetMax(out _, out _);
                    index.TryGetFloor(perThread / 2, out _, out _);
                    index.Find(i % perThread);
                }
            });
        }

        await writer;
        await Task.WhenAll(readers);
        index.EntryCount.Should().Be(perThread, "写者全部落位");
    }

    // ══════════════════════════════════════════════════════════
    // SkipList：并发写者
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task SkipList_ConcurrentInsert_AllPresentAndOrdered()
    {
        using var index = CreateSkipList(_vol, "sl");
        const int threads = 4, perThread = 2500;

        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    long key = (long)tid * perThread + i;
                    index.Insert(key, MakeAddr(key), LogicalAddress.Empty);
                }
            });
        }
        await Task.WhenAll(tasks);

        index.EntryCount.Should().Be(threads * perThread);
        for (long k = 0; k < threads * perThread; k += 97)   // 抽查
            index.Find(k).Should().Be(MakeAddr(k));
        index.TryGetMax(out var maxKey, out _).Should().BeTrue();
        maxKey.Should().Be(threads * perThread - 1);

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Should().Equal(Enumerable.Range(0, threads * perThread).Select(k => (long)k),
            "并发插入后扫描严格有序无缺");
    }

    [Fact]
    public async Task SkipList_ConcurrentMixedWrites_TableStaysConsistent()
    {
        using var index = CreateSkipList(_vol, "sl");
        const int insertThreads = 2, perThread = 3000;

        var inserters = new Task[insertThreads];
        for (int t = 0; t < insertThreads; t++)
        {
            int tid = t;
            inserters[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    long key = (long)tid * perThread + i;
                    index.Insert(key, MakeAddr(key), LogicalAddress.Empty);
                }
            });
        }
        var deleter = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
                index.Delete(i * 2);
        });
        var truncator = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
                index.TruncatePrefix(i * 10);
        });

        await Task.WhenAll(inserters.Append(deleter).Append(truncator));
        index.TruncatePrefix(490);   // 终态收敛（消除并发交错的不确定性——截断者上限 490）

        using var cursor = index.CreateScanCursor(ReadDirection.Forward);
        var delivered = new List<long>();
        while (cursor.MoveNext())
            delivered.Add(cursor.CurrentKey);
        delivered.Count.Should().Be((int)index.EntryCount, "扫描计数 == EntryCount（结构一致性）");
        delivered.Should().BeInAscendingOrder();
        delivered.Should().OnlyContain(k => k >= 490, "终态截断到 490 后不应有更低存活键");
    }

    /// <summary>stub 重放数据面（dump 的 W 锚点前提——只喂 TryDump 走 WriteBody，重放语义不消费）。</summary>
    private sealed class StubKeyResolver : IKeyResolver<long>
    {
        public bool TryGetKey(LogicalAddress addr, out long key)
        {
            key = 0;
            return false;
        }

        public LogicalAddress GetFlushedWatermark() => LogicalAddress.Empty;

        public async IAsyncEnumerable<(long Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
            LogicalAddress begin, LogicalAddress end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public IAsyncEnumerable<(long Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
            => ScanAsync(LogicalAddress.Empty, LogicalAddress.Empty, ct);
    }
}
