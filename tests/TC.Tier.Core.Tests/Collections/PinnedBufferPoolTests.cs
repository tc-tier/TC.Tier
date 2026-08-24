using System.Runtime.InteropServices;

namespace TC.Tier.Core.Tests.Collections;

/// <summary>
/// PinnedBufferPool 单元测试 — 池化语义、线程安全、size 分桶、DIO 对齐内存路径。
/// </summary>
public sealed class PinnedBufferPoolTests
{
    [Fact]
    public void Rent_Return_SameBuffer()
    {
        using var pool = new PinnedBufferPool();
        var buf = pool.Rent(1024);
        pool.Return(buf);
        var buf2 = pool.Rent(1024);
        Assert.Same(buf, buf2);
    }

    [Fact]
    public void Rent_NewBuffer_WhenPoolEmpty()
    {
        using var pool = new PinnedBufferPool();
        var buf = pool.Rent(1024);
        buf.Should().NotBeNull();
        buf.Length.Should().Be(1024);
    }

    [Fact]
    public void Rent_ProducesPinnedArray()
    {
        using var pool = new PinnedBufferPool();
        var buf = pool.Rent(256);
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            handle.AddrOfPinnedObject().Should().NotBe(IntPtr.Zero);
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void Return_WrongSize_Ignored()
    {
        using var pool = new PinnedBufferPool();
        var alien = new byte[512];
        pool.Return(alien);

        var buf = pool.Rent(1024);
        Assert.NotSame(alien, buf);
        buf.Length.Should().Be(1024);
    }

    [Fact]
    public void Different_Sizes_Separated()
    {
        using var pool = new PinnedBufferPool();
        var buf512 = pool.Rent(512);
        var buf1024 = pool.Rent(1024);
        pool.Return(buf512);
        pool.Return(buf1024);

        Assert.Same(buf512, pool.Rent(512));
        Assert.Same(buf1024, pool.Rent(1024));
    }

    [Fact]
    public void MaxPerBucket_Respected()
    {
        using var pool = new PinnedBufferPool(maxPerBucket: 2);
        var b1 = pool.Rent(256);
        var b2 = pool.Rent(256);
        var b3 = pool.Rent(256);
        pool.Return(b1);   // 栈：[b1]
        pool.Return(b2);   // 栈：[b1, b2]（count=2=max）
        pool.Return(b3);   // 超过 maxPerBucket，b3 被丢弃

        // ConcurrentStack LIFO：栈顶先出，取出顺序 b2, b1
        Assert.Same(b2, pool.Rent(256));
        Assert.Same(b1, pool.Rent(256));
        Assert.NotSame(b3, pool.Rent(256)); // b3 被丢弃，新分配
    }

    [Fact]
    public void Dispose_ClearsAllBuckets()
    {
        var pool = new PinnedBufferPool();
        pool.Return(pool.Rent(256));
        pool.Return(pool.Rent(512));
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent(256));
        // 上述 Assert.Throws 对同步 lambda 合法（非 async）
    }

    [Fact]
    public void Concurrent_ThreadSafe()
    {
        using var pool = new PinnedBufferPool(maxPerBucket: 100);
        var rng = new Random(42);
        Parallel.For(0, 100, i =>
        {
            int size = 256 * (1 + rng.Next(4));
            var buf = pool.Rent(size);
            Thread.SpinWait(10);
            pool.Return(buf);
        });
    }

    [Fact]
    public void RentAligned_ReturnAligned_SameBuffer()
    {
        // DIO 对齐内存路径（Blob 版新增）
        using var pool = new PinnedBufferPool();
        var buf = pool.RentAligned(4096, 4096);
        pool.ReturnAligned(buf);
        var buf2 = pool.RentAligned(4096, 4096);
        Assert.Same(buf, buf2);
    }

    [Fact]
    public void RentAligned_NewBuffer_WhenPoolEmpty()
    {
        using var pool = new PinnedBufferPool();
        var buf = pool.RentAligned(8192, 512);
        buf.Should().NotBeNull();
        buf.Size.Should().Be(8192);
    }

    [Fact]
    public void CacheHitsMisses_Increment()
    {
        using var pool = new PinnedBufferPool();
        pool.Rent(1024);              // miss
        pool.Rent(1024);              // miss
        pool.CacheMisses.Should().Be(2);
        pool.CacheHits.Should().Be(0);

        var b = pool.Rent(1024);
        pool.Return(b);
        pool.Rent(1024);              // hit
        pool.CacheHits.Should().Be(1);
    }
}
