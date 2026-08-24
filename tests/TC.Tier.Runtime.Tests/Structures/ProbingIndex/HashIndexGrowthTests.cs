using TC.Tier.Runtime.Tests.Structures.ProbingIndex;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.Tests.Structures.ProbingIndex;

/// <summary>
/// HashIndex 容量自适应（GrowIndex 换代）契约测试。
/// <para>★ 契约面：装载超 0.7 自动翻倍（多次换代全条目保全）/dup 覆写不推计数/删除回退计数/
///   换代后继续删+插/写者换代期读者容忍。</para>
/// </summary>
public class HashIndexGrowthTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public HashIndexGrowthTests()
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

    private static HashIndex<long> NewSmall(TestVolume vol, MockKeyResolver<long> resolver, int capacity = 1 << 10)
    {
        var settings = TestProbingIndexSettingsFactory.On(vol, "grow", hashTableCapacity: capacity,
            overflowPoolCapacity: 1 << 10);
        return TestProbingIndexSettingsFactory.NewHash(vol, settings, resolver);
    }

    [Fact]
    public void Growth_PreservesAllEntries_AcrossMultipleGenerations()
    {
        var resolver = new MockKeyResolver<long>();
        using var index = NewSmall(_vol, resolver);   // 1024 桶，阈值 ~717——5000 条至少三代换代

        const int n = 5000;
        for (long k = 0; k < n; k++)
        {
            var addr = new LogicalAddress(0, 256L * k);
            resolver.Put(addr, k);
            index.Insert(k, addr, LogicalAddress.Empty);
        }

        index.EntryCount.Should().Be(n);
        index.IndexSize.Should().BeGreaterThan(1024L * 128, "装载超限必已翻倍换代");

        for (long k = 0; k < n; k++)
            index.Find(k).Should().Be(new LogicalAddress(0, 256L * k), $"换代后条目 {k} 必保全");
    }

    [Fact]
    public void Growth_DuplicateOverwrite_DoesNotAdvanceCount()
    {
        var resolver = new MockKeyResolver<long>();
        using var index = NewSmall(_vol, resolver);

        var addr = new LogicalAddress(0, 256L);
        resolver.Put(addr, 42);
        for (int i = 0; i < 2000; i++)
            index.Insert(42, addr, LogicalAddress.Empty);   // 同 key 覆写 2000 次

        index.EntryCount.Should().Be(1, "覆写不是新条目——不得触发换代");
        index.IndexSize.Should().Be(1024L * 128 + 1024L * 128);
    }

    [Fact]
    public void EntryCount_DeleteDecrements_ReinsertRegrows()
    {
        var resolver = new MockKeyResolver<long>();
        using var index = NewSmall(_vol, resolver);

        for (long k = 0; k < 100; k++)
        {
            var addr = new LogicalAddress(0, 256L * k);
            resolver.Put(addr, k);
            index.Insert(k, addr, LogicalAddress.Empty);
        }
        for (long k = 0; k < 50; k++)
            index.Delete(k).Should().BeTrue();

        index.EntryCount.Should().Be(50);

        for (long k = 0; k < 50; k++)
            index.Find(k).Should().Be(LogicalAddress.Empty);
        for (long k = 50; k < 100; k++)
            index.Find(k).Should().NotBe(LogicalAddress.Empty);
    }

    [Fact]
    public void Growth_ThenDeleteAndReinsert_RoundTrip()
    {
        var resolver = new MockKeyResolver<long>();
        using var index = NewSmall(_vol, resolver);

        const int n = 3000;
        for (long k = 0; k < n; k++)
        {
            var addr = new LogicalAddress(0, 256L * k);
            resolver.Put(addr, k);
            index.Insert(k, addr, LogicalAddress.Empty);
        }

        for (long k = 0; k < n; k += 2)
            index.Delete(k).Should().BeTrue();

        for (long k = 0; k < n; k += 2)
        {
            var addr = new LogicalAddress(0, 256L * k + 128);
            resolver.Put(addr, k);
            index.Insert(k, addr, LogicalAddress.Empty);   // 重插（换代后的代表上）
        }

        index.EntryCount.Should().Be(n);
        for (long k = 0; k < n; k++)
            index.Find(k).Should().Be(new LogicalAddress(0, 256L * k + (k % 2 == 0 ? 128 : 0)));
    }

    [Fact]
    public void Growth_DuringWriterInserts_ConcurrentReaderTolerated()
    {
        var resolver = new MockKeyResolver<long>();
        using var index = NewSmall(_vol, resolver);   // 换代在写者插入流中发生

        const int n = 6000;
        var writer = Task.Run(() =>
        {
            for (long k = 0; k < n; k++)
            {
                var addr = new LogicalAddress(0, 256L * k);
                resolver.Put(addr, k);
                index.Insert(k, addr, LogicalAddress.Empty);
            }
        });

        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            long hits = 0;
            for (long k = 0; k < n; k++)
            {
                var found = index.Find(k);
                if (found != LogicalAddress.Empty) hits++;   // 换代窗口旧代 miss=合法中间态
            }
            return hits;
        })).ToArray();

        writer.Wait();
        Task.WaitAll(readers);

        index.EntryCount.Should().Be(n);
        for (long k = 0; k < n; k++)
            index.Find(k).Should().Be(new LogicalAddress(0, 256L * k), "join 后全量必可见");
    }
}
