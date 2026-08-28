using System.Collections.Concurrent;

namespace TC.Tier.Core.Collections;

/// <summary>
/// 固定大小溢出对象池。并发安全（基于 <see cref="ConcurrentQueue{T}"/>）。
/// <para>★ 指标：hits/misses/overflows 计数 + <see cref="GetStats"/>（对齐 PinnedBufferPool 覆盖度）。</para>
/// <para>容量上限为软约束（Soft cap）：高并发 TryAdd 下可能瞬时超出 <c>size</c> 几个，
/// 因 ConcurrentQueue 的 Count 快照与 Enqueue 非原子。这对当前调用方（disposer 为 no-op，
/// 超出项被丢弃）无正确性影响；如需硬上限须改用自旋锁或 CAS 容量跟踪。</para>
/// </summary>
public sealed class OverflowPool<T> : IDisposable
{
    private readonly int _size;
    private readonly ConcurrentQueue<T> _itemQueue;
    private readonly Action<T> _disposer;

    // ★ 指标（Interlocked，跨线程聚合）
    private long _hits;       // TryGet 成功命中
    private long _misses;     // TryGet 未命中（队列空）
    private long _overflows;  // TryAdd 被拒（池满/disposed，调 disposer）

    private int _disposed;    // 0=存活, 1=已释放（Volatile/Interlocked，修旧版非 volatile TOCTOU）

    /// <summary>当前池中对象数（快照，并发下近似值）。</summary>
    public int Count => _itemQueue.Count;

    /// <summary>TryGet 命中次数（池成功供出对象）。</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>TryGet 未命中次数（池空，调用方需自行分配）。</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>TryAdd 被拒次数（池满或已释放，对象被 disposer 回收）。</summary>
    public long Overflows => Interlocked.Read(ref _overflows);

    /// <summary>构造溢出池（有界并发队列——池空 miss、池满 overflow，都无阻塞）。</summary>
    /// <param name="size">池容量上限（软约束）。</param>
    /// <param name="disposer">被拒/释放时的对象回收回调（null = no-op）。</param>
    public OverflowPool(int size, Action<T>? disposer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        _size = size;
        _itemQueue = new ConcurrentQueue<T>();
        _disposer = disposer ?? (_ => { });
    }

    /// <summary>尝试从池中取出对象。成功（命中）返回 true；池空（未命中）返回 false。</summary>
    /// <param name="item">输出对象（池空时为 default(T)）。</param>
    /// <returns>是否成功命中池对象。</returns>
    /// <remarks>★ 高并发下 TryGet/TryAdd 可能瞬时超出 size 几个（软上限），但对调用方无正确性影响。</remarks>
    public bool TryGet(out T? item)
    {
        if (_itemQueue.TryDequeue(out item))
        {
            Interlocked.Increment(ref _hits);
            return true;
        }
        Interlocked.Increment(ref _misses);
        return false;
    }

    /// <summary>尝试归还对象到池。池未满且未释放返回 true（入池）；否则调 disposer 回收，返回 false（overflow）。</summary>
    /// <param name="item">要归还的对象。</param>
    /// <returns>是否成功入池。</returns>
    /// <remarks>★ 高并发下 TryGet/TryAdd 可能瞬时超出 size 几个（软上限），但对调用方无正确性影响。</remarks>
    public bool TryAdd(T item)
    {
        // 软上限：Count 快照与 Enqueue 非原子，高并发下可能瞬时超出 size 几个（无正确性影响，见类注释）
        if (Volatile.Read(ref _disposed) == 0 && _itemQueue.Count < _size)
        {
            _itemQueue.Enqueue(item);
            return true;
        }
        Interlocked.Increment(ref _overflows);
        _disposer(item);
        return false;
    }

    /// <summary>聚合指标快照。</summary>
    /// <remarks>★ 高并发下 Count 快照为近似值（ConcurrentQueue 非原子）。</remarks>
    public (long hits, long misses, int count, int size, long overflows) GetStats()
        => (Hits, Misses, Count, _size, Overflows);

    /// <summary>释放池：标记已释放，排空并回收所有剩余对象。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        while (_itemQueue.TryDequeue(out var item))
            _disposer(item);
    }
}
