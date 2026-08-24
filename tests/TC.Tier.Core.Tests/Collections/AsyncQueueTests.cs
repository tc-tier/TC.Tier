namespace TC.Tier.Core.Tests.Collections;

public class AsyncQueueTests
{
    [Fact]
    public async Task EnqueueThenDequeue_ReturnsItem()
    {
        var q = new AsyncQueue<int>();
        q.Enqueue(42);
        var item = await q.DequeueAsync();
        item.Should().Be(42);
    }

    [Fact]
    public async Task DequeueAsync_EmptyQueue_BlocksUntilEnqueue()
    {
        var q = new AsyncQueue<string>();
        var dequeueTask = q.DequeueAsync().AsTask();

        await Task.Delay(50);
        dequeueTask.IsCompleted.Should().BeFalse();

        q.Enqueue("hello");
        var result = await dequeueTask;
        result.Should().Be("hello");
    }

    [Fact]
    public async Task Count_ReflectsQueueSize()
    {
        var q = new AsyncQueue<int>();
        q.Count.Should().Be(0);

        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);
        q.Count.Should().Be(3);

        await q.DequeueAsync();
        q.Count.Should().Be(2);
    }

    [Fact]
    public async Task Fifo_Ordering()
    {
        var q = new AsyncQueue<int>();
        for (int i = 0; i < 100; i++)
            q.Enqueue(i);

        for (int i = 0; i < 100; i++)
            (await q.DequeueAsync()).Should().Be(i);
    }

    [Fact]
    public void TryDequeue_ReturnsItemAndTrue()
    {
        var q = new AsyncQueue<int>();
        q.Enqueue(7);

        q.TryDequeue(out var item).Should().BeTrue();
        item.Should().Be(7);
        q.Count.Should().Be(0);
    }

    [Fact]
    public void TryDequeue_Empty_ReturnsFalse()
    {
        var q = new AsyncQueue<int>();
        q.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public async Task DequeueAsync_AlreadyCanceled_ThrowsOCE()
    {
        var q = new AsyncQueue<int>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await q.DequeueAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DequeueAsync_CancelWhileWaiting_ThrowsOCE()
    {
        var q = new AsyncQueue<int>();
        using var cts = new CancellationTokenSource();

        var dequeueTask = q.DequeueAsync(cts.Token).AsTask();
        await Task.Delay(50);
        dequeueTask.IsCompleted.Should().BeFalse();

        cts.Cancel();

        var act = async () => await dequeueTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CancelledDequeue_DoesNotLoseEnqueuedItem()
    {
        var q = new AsyncQueue<int>();
        using var cts = new CancellationTokenSource();

        // 发起一个会被取消的 Dequeue
        var cancelTask = Task.Run(async () =>
        {
            try { await q.DequeueAsync(cts.Token); }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(50);
        cts.Cancel();
        await cancelTask;

        // Enqueue 的 item 应仍在队列中
        q.Enqueue(99);
        var item = await q.DequeueAsync();
        item.Should().Be(99);
    }

    [Fact]
    public async Task MultipleConsumers_AllReceiveItems()
    {
        var q = new AsyncQueue<int>();
        int received = 0;
        const int total = 100;

        // 启动 4 个消费者
        var consumers = new Task[4];
        for (int c = 0; c < 4; c++)
        {
            consumers[c] = Task.Run(async () =>
            {
                while (Interlocked.Add(ref received, 0) < total)
                {
                    try
                    {
                        await q.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
                        Interlocked.Increment(ref received);
                    }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        // 生产者入队
        for (int i = 0; i < total; i++)
            q.Enqueue(i);

        // 等待消费完成（带超时防死锁）
        await Task.WhenAny(Task.WhenAll(consumers), Task.Delay(5000));
        Volatile.Read(ref received).Should().Be(total);
    }

    [Fact]
    public void WaitForEntry_ReturnsWhenItemAvailable()
    {
        var q = new AsyncQueue<int>();
        _ = Task.Run(() =>
        {
            Thread.Sleep(50);
            q.Enqueue(1);
        });

        q.WaitForEntry();
        q.Count.Should().Be(1);
    }

    [Fact]
    public async Task WaitForEntryAsync_BlocksUntilEnqueue()
    {
        var q = new AsyncQueue<int>();
        var waitTask = q.WaitForEntryAsync().AsTask();

        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeFalse();

        q.Enqueue(1);
        await waitTask;
    }

    [Fact]
    public async Task ProducerConsumer_HighVolume_NoLoss()
    {
        var q = new AsyncQueue<int>();
        const int total = 10000;
        int consumed = 0;
        long sum = 0;

        var consumer = Task.Run(async () =>
        {
            for (int i = 0; i < total; i++)
            {
                var v = await q.DequeueAsync();
                Interlocked.Increment(ref consumed);
                Interlocked.Add(ref sum, v);
            }
        });

        long expectedSum = 0;
        for (int i = 0; i < total; i++)
        {
            q.Enqueue(i);
            expectedSum += i;
        }

        await consumer;

        Volatile.Read(ref consumed).Should().Be(total);
        Interlocked.Read(ref sum).Should().Be(expectedSum);
    }

    [Fact]
    public async Task Concurrent_EnqueueDequeue_Stress()
    {
        var q = new AsyncQueue<int>();
        const int perProducer = 2000;
        const int producerCount = 4;
        const int total = perProducer * producerCount;

        int consumed = 0;
        var consumers = new Task[producerCount];
        var producers = new Task[producerCount];

        for (int p = 0; p < producerCount; p++)
        {
            int pid = p;
            producers[p] = Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++)
                    q.Enqueue(pid * perProducer + i);
            });
        }

        for (int c = 0; c < producerCount; c++)
        {
            consumers[c] = Task.Run(async () =>
            {
                while (Volatile.Read(ref consumed) < total)
                {
                    try
                    {
                        await q.DequeueAsync();
                        Interlocked.Increment(ref consumed);
                    }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        await Task.WhenAll(producers);

        // 等消费完成
        await Task.WhenAny(Task.WhenAll(consumers), Task.Delay(10000));
        Volatile.Read(ref consumed).Should().Be(total);
    }
}
