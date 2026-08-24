using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// AsyncQueue 性能基准：新实现（PooledValueTaskSource）vs SemaphoreSlim 基线。
/// 覆盖：入队-出队往返吞吐、分配量、多消费者竞争。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class AsyncQueueBench
{
    // 快速路径（队列非空）的 Enq+Deq 往返，全部异步
    [Benchmark(Description = "New AsyncQueue Enq+Deq (item ready)", Baseline = true)]
    public async Task<int> NewQueue_EnqDeq()
    {
        var q = new AsyncQueue<int>();
        q.Enqueue(1);
        return await q.DequeueAsync();
    }

    [Benchmark(Description = "SemaphoreSlim AsyncQueue Enq+Deq (item ready)")]
    public async Task<int> SemQueue_EnqDeq()
    {
        var q = new SemaphoreAsyncQueue();
        q.Enqueue(1);
        return await q.DequeueAsync();
    }

    // ===== 2. 高吞吐串行生产-消费（无竞争）=====

    [Params(100)]
    public int ItemCount { get; set; }

    [Benchmark(Description = "New AsyncQueue serial producer-consumer")]
    public async Task NewQueue_SerialThroughput()
    {
        var q = new AsyncQueue<int>();
        var consumer = Task.Run(async () =>
        {
            for (int i = 0; i < ItemCount; i++)
                await q.DequeueAsync();
        });

        for (int i = 0; i < ItemCount; i++)
            q.Enqueue(i);

        await consumer;
    }

    [Benchmark(Description = "SemaphoreSlim AsyncQueue serial producer-consumer")]
    public async Task SemQueue_SerialThroughput()
    {
        var q = new SemaphoreAsyncQueue();
        var consumer = Task.Run(async () =>
        {
            for (int i = 0; i < ItemCount; i++)
                await q.DequeueAsync();
        });

        for (int i = 0; i < ItemCount; i++)
            q.Enqueue(i);

        await consumer;
    }

    // ===== 3. 出队阻塞再唤醒（竞争路径分配量）=====

    [Benchmark(Description = "New AsyncQueue dequeue-wait wakeup (1:1)")]
    public async Task NewQueue_WaitWakeup()
    {
        var q = new AsyncQueue<int>();
        var dequeueTask = q.DequeueAsync().AsTask();

        // 短暂让出确保 waiter 已挂起
        await Task.Yield();

        q.Enqueue(1);
        await dequeueTask;
    }

    [Benchmark(Description = "SemaphoreSlim AsyncQueue dequeue-wait wakeup (1:1)")]
    public async Task SemQueue_WaitWakeup()
    {
        var q = new SemaphoreAsyncQueue();
        var dequeueTask = q.DequeueAsync();

        await Task.Yield();

        q.Enqueue(1);
        await dequeueTask;
    }

    /// <summary>SemaphoreSlim + ConcurrentQueue 基线（旧 AsyncQueue 实现）。</summary>
    private sealed class SemaphoreAsyncQueue : IDisposable
    {
        private readonly SemaphoreSlim _sem = new(0);
        private readonly ConcurrentQueue<int> _q = new();

        public void Enqueue(int item)
        {
            _q.Enqueue(item);
            _sem.Release();
        }

        public async Task<int> DequeueAsync()
        {
            for (; ; )
            {
                await _sem.WaitAsync();
                if (_q.TryDequeue(out var item))
                    return item;
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _sem.Dispose();
        }
    }
}
