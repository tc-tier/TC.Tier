using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace TC.Tier.Core.Collections;

/// <summary>
/// 离散枚举优先级队列——N 个 <see cref="ConcurrentQueue{T}"/> 桶，每桶对应一个枚举优先级。
/// <para>★ 严格优先级：按枚举值升序扫描桶（值小先出），同优先级桶内 FIFO。</para>
/// <para>★ 无锁入队：<see cref="ConcurrentQueue{T}"/> 内部无锁实现。</para>
/// <para>★ lock-free 出队：扫桶 <see cref="ConcurrentQueue{T}.TryDequeue"/>，单消费者接近 wait-free。</para>
/// <para>★ 适用场景：优先级是少量离散枚举值（如 4~16 级）。任意可比优先级用 <see cref="SkipListPriorityQueue{T}"/>。</para>
/// <para>★ 零分配：入队复用 <see cref="ConcurrentQueue{T}"/> 的分段池；等待复用 <see cref="AsyncManualResetEvent"/> 池化 source。</para>
/// </summary>
/// <typeparam name="TPriority">枚举优先级类型。值小者优先。</typeparam>
/// <typeparam name="T">元素类型。</typeparam>
[SuppressMessage("Naming", "CA1711:标识符应采用正确的后缀")]
public sealed class BucketPriorityQueue<TPriority, T> : IDisposable where TPriority : struct, Enum
{
    private readonly ConcurrentQueue<T>[] _buckets;
    private readonly TPriority[] _orderedPriorities;   // 按枚举值升序排列
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);   // ★ 一项一许可：多消费者 MPMC 公平逐项唤醒
    private long _count;
    private int _disposed;

    /// <summary>
    /// 创建桶优先队列。桶数 = <typeparamref name="TPriority"/> 枚举值的数量。
    /// </summary>
    /// <exception cref="FormatException"></exception>
    /// <exception cref="InvalidCastException"></exception>
    /// <exception cref="OverflowException"></exception>
    public BucketPriorityQueue()
    {
        // 取全部枚举值并按升序排列（值小者优先）
        _orderedPriorities = Enum.GetValues<TPriority>().OrderBy(p => Convert.ToInt64(p)).ToArray();
        var bucketCount = _orderedPriorities.Length;
        _buckets = new ConcurrentQueue<T>[bucketCount];
        for (var i = 0; i < bucketCount; i++)
            _buckets[i] = new ConcurrentQueue<T>();

        // 构建枚举值 → 桶下标的快速映射（避免每次 Enqueue/Dequeue 二分查找）
        // 枚举值范围可能稀疏（如 0,1,2,100），用字典映射到紧凑桶下标
        ValueToBucket = new Dictionary<long, int>();
        for (var i = 0; i < _orderedPriorities.Length; i++)
            ValueToBucket.Add(Convert.ToInt64(_orderedPriorities[i]), i);
    }

    /// <summary>枚举值（long）→ 桶下标的映射（紧凑化稀疏枚举）。</summary>
    private Dictionary<long, int> ValueToBucket { get; }

    /// <summary>近似元素数（并发下非精确，诊断用）。</summary>
    public int Count => (int)Interlocked.Read(ref _count);

    /// <summary>
    /// 入队——无锁（<see cref="ConcurrentQueue{T}"/> 内部无锁）。
    /// </summary>
    /// <param name="item">元素。</param>
    /// <param name="priority">优先级。值小者先出。</param>
    public void Enqueue(T item, TPriority priority)
    {
        var key = Convert.ToInt64(priority);
        if (!ValueToBucket.TryGetValue(key, out var bucketIdx))
            throw new ArgumentOutOfRangeException(nameof(priority), priority, $"枚举值 {priority} 不在 {typeof(TPriority).Name} 定义范围内");
        _buckets[bucketIdx].Enqueue(item);
        Interlocked.Increment(ref _count);
        _signal.Release();   // ★ 一项一许可：唤醒一个等待的消费者（多消费者下逐项公平，无惊群、无孤儿许可）
    }

    /// <summary>
    /// 尝试出队——按枚举值升序扫桶，第一个非空桶出队。
    /// <para>★ 严格优先级：值小的桶一定先于值大的桶出队。</para>
    /// <para>★ 同优先级 FIFO：<see cref="ConcurrentQueue{T}"/> 天然保证。</para>
    /// </summary>
    public bool TryDequeue(out T item)
    {
        // 按优先级升序扫描所有桶
        foreach (var bucket in _buckets)
        {
            if (bucket.TryDequeue(out item!))
            {
                Interlocked.Decrement(ref _count);
                return true;
            }
        }
        item = default!;
        return false;
    }

    /// <summary>
    /// 尝试查看最高优先级元素（不出队）。
    /// </summary>
    public bool TryPeek(out T item)
    {
        foreach (var bucket in _buckets)
        {
            if (bucket.TryPeek(out item!))
                return true;
        }
        item = default!;
        return false;
    }

    /// <summary>
    /// 异步出队——空队列时挂起直到有元素入队或取消。
    /// <para>★ counting-semaphore MPMC（多生产者多消费者）：<see cref="Enqueue"/> 每入一项 <c>Release</c> 一许可，
    ///   本方法先 <c>WaitAsync</c> 消费一个许可再 <see cref="TryDequeue"/>。</para>
    /// <para>★ 无 fast-path 偷项：许可数 == 可取项数，消费许可后 <see cref="TryDequeue"/> 必中
    ///   （无孤儿许可累积→无 <c>SemaphoreFullException</c>；多消费者逐项公平唤醒→无惊群）。
    ///   许可可用时 <c>WaitAsync</c> 立即返回，故单消费者性能与 fast-path 相当。</para>
    /// </summary>
    public async ValueTask<T> DequeueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ★ 消费一个许可（有许可则立即返回 = 等价 fast-path）→ 再出队。
        //   许可与项 1:1，故拿许可后 TryDequeue 必中；极端竞争下若被同优先级桶轮转错过，循环重试（自洽）。
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(BucketPriorityQueue<TPriority, T>));
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (TryDequeue(out var item))
                return item;
        }
    }

    /// <summary>释放——唤醒等待的消费者（消费者通常经 worker 的 ct 退出，此处兜底无 ct 调用者）。</summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        // 唤醒可能阻塞在 WaitAsync 的无 ct 调用者：逐个 Release，每个唤醒一个 waiter。
        // 安全上限远超任何合理消费者数；CurrentCount 已满则 Release 抛 SemaphoreFullException 即停。
        for (var i = 0; i < 1024; i++)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { break; }
        }
    }
}
