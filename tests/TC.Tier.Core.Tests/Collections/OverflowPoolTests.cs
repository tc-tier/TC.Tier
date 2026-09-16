namespace TC.Tier.Core.Tests.Collections;
public class OverflowPoolTests
{
    [Fact]
    public void TryGet_Empty_ReturnsFalse_AndIncrementsMiss()
    {
        var pool = new OverflowPool<int>(4);
        pool.TryGet(out var item).Should().BeFalse();
        item.Should().Be(0);
        pool.Misses.Should().Be(1);
        pool.Hits.Should().Be(0);
    }

    [Fact]
    public void TryAdd_ThenTryGet_ReturnsItem_AndIncrementsHit()
    {
        var pool = new OverflowPool<int>(4);
        pool.TryAdd(42).Should().BeTrue();
        pool.TryGet(out var item).Should().BeTrue();
        item.Should().Be(42);
        pool.Hits.Should().Be(1);
        pool.Misses.Should().Be(0);
        pool.Count.Should().Be(0);
    }

    [Fact]
    public void TryAdd_WithinSize_ReturnsTrue()
    {
        var pool = new OverflowPool<string>(3);
        pool.TryAdd("a").Should().BeTrue();
        pool.TryAdd("b").Should().BeTrue();
        pool.TryAdd("c").Should().BeTrue();
        pool.Count.Should().Be(3);
        pool.Overflows.Should().Be(0);
    }

    [Fact]
    public void TryAdd_OverSize_InvokesDisposer_AndReturnsFalse()
    {
        var disposed = new List<string>();
        var pool = new OverflowPool<string>(2, s => disposed.Add(s));
        pool.TryAdd("a").Should().BeTrue();
        pool.TryAdd("b").Should().BeTrue();
        pool.TryAdd("c").Should().BeFalse();  // 超容量，被拒
        disposed.Should().ContainSingle().Which.Should().Be("c");
        pool.Overflows.Should().Be(1);
        pool.Count.Should().Be(2);
    }

    [Fact]
    public void TryAdd_AfterDispose_InvokesDisposer_AndReturnsFalse()
    {
        var disposed = new List<int>();
        var pool = new OverflowPool<int>(4, i => disposed.Add(i));
        pool.Dispose();
        pool.TryAdd(1).Should().BeFalse();
        disposed.Should().ContainSingle().Which.Should().Be(1);
        pool.Overflows.Should().Be(1);
    }

    [Fact]
    public void Dispose_DrainsAndDisposesAllItems()
    {
        var disposed = new List<int>();
        var pool = new OverflowPool<int>(4, i => disposed.Add(i));
        pool.TryAdd(1);
        pool.TryAdd(2);
        pool.TryAdd(3);
        pool.Dispose();
        disposed.Should().BeEquivalentTo(s_disposedAllExpected);
        pool.Count.Should().Be(0);
    }

    private static readonly int[] s_disposedAllExpected = { 1, 2, 3 };

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var pool = new OverflowPool<int>(4);
        pool.TryAdd(1);
        pool.Dispose();
        var act = () => pool.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void GetStats_ReturnsAccurateSnapshot()
    {
        var pool = new OverflowPool<int>(4);
        pool.TryAdd(1);
        pool.TryAdd(2);
        pool.TryGet(out _);   // hit
        pool.TryGet(out _);   // hit
        pool.TryGet(out _);   // miss
        var (hits, misses, count, size, overflows) = pool.GetStats();
        hits.Should().Be(2);
        misses.Should().Be(1);
        count.Should().Be(0);
        size.Should().Be(4);
        overflows.Should().Be(0);
    }

    [Fact]
    public void Constructor_NonPositiveSize_Throws()
    {
        var act1 = () => new OverflowPool<int>(0);
        var act2 = () => new OverflowPool<int>(-1);
        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Fifo_Ordering_PreservedWithinConcurrency()
    {
        // 单线程下严格 FIFO（ConcurrentQueue 保证）
        var pool = new OverflowPool<int>(8);
        for (int i = 0; i < 8; i++) pool.TryAdd(i);
        for (int i = 0; i < 8; i++)
        {
            pool.TryGet(out var item).Should().BeTrue();
            item.Should().Be(i);
        }
    }

    [Fact]
    public async Task Concurrent_NoCorruption_UnderDisposeRace()
    {
        const int producerCount = 4;
        const int perProducer = 1_000;
        const int total = producerCount * perProducer;

        for (int iter = 0; iter < 50; iter++)
        {
            var disposeCounts = new int[total];
            var pool = new OverflowPool<int>(64, item => Interlocked.Increment(ref disposeCounts[item]));
            using var start = new ManualResetEventSlim();

            var producers = Enumerable.Range(0, producerCount).Select(producer => Task.Run(() =>
            {
                start.Wait();
                int begin = producer * perProducer;
                for (int item = begin; item < begin + perProducer; item++)
                    pool.TryAdd(item);
            })).ToArray();
            var disposer = Task.Run(() =>
            {
                start.Wait();
                pool.Dispose();
            });

            start.Set();
            await Task.WhenAll(producers.Append(disposer));

            pool.Count.Should().Be(0, "Dispose 返回后不得残留竞态晚入队对象");
            disposeCounts.Should().OnlyContain(
                count => count == 1,
                "每个归还对象必须由拒绝路径或 Dispose 排水恰好回收一次");
        }
    }
}
