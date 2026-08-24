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
    public async Task Concurrent_TryAddTryGet_Stress()
    {
        // 多生产者 + 多消费者并发压测（对齐 AsyncQueueTests 的并发范式）
        var pool = new OverflowPool<int>(64);
        const int perProducer = 2000;
        const int producerCount = 4;
        const int total = perProducer * producerCount;
        int consumed = 0;
        var producers = new Task[producerCount];
        var consumers = new Task[producerCount];

        // 生产者：尝试入池（部分会被拒 overflow，因容量 64 << total）
        for (int p = 0; p < producerCount; p++)
        {
            int pid = p;
            producers[p] = Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++)
                    pool.TryAdd(pid * perProducer + i);
            });
        }
        // 消费者：持续取出直到吃满 total（含被拒的不计入）
        for (int c = 0; c < producerCount; c++)
        {
            consumers[c] = Task.Run(() =>
            {
                int local = 0;
                while (local < perProducer && Volatile.Read(ref consumed) < total)
                {
                    if (pool.TryGet(out _))
                    {
                        local++;
                        Interlocked.Increment(ref consumed);
                    }
                }
            });
        }
        await Task.WhenAll(producers);
        await Task.WhenAny(Task.WhenAll(consumers), Task.Delay(10000));

        // 容量 64，生产 total=8000，大部分被拒 overflow。消费掉的 = hits，吃掉的应 <= total。
        pool.Hits.Should().BePositive("消费者应至少取到部分对象");
        pool.Overflows.Should().BePositive("容量远小于生产量，必有 overflow");
        (pool.Hits + pool.Count).Should().BeLessThanOrEqualTo(total);
    }

    [Fact]
    public async Task Concurrent_NoCorruption_UnderDisposeRace()
    {
        // 并发 TryAdd 与 Dispose 不应崩溃/死锁（旧版 _disposed 非 volatile 有 TOCTOU，新版 Volatile 修复）
        for (int iter = 0; iter < 20; iter++)
        {
            var pool = new OverflowPool<int>(4, _ => { });
            var w1 = Task.Run(() => { for (int i = 0; i < 1000; i++) pool.TryAdd(i); });
            var w2 = Task.Run(() => pool.Dispose());
            await Task.WhenAll(w1, w2);
            // 不抛异常即通过（dispose 后 TryAdd 走 overflow 分支回收）
        }
    }
}
