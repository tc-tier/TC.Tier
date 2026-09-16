#pragma warning disable CA1711
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Collections;

/// <summary>
/// 异步生产-消费队列。基于 <see cref="ConcurrentQueue{T}"/> + 池化等待节点，
/// 常规路径（队列非空时出队、入队唤醒）零堆分配。
/// 出队发现队列为空时，租用独立完成源挂入等待链；入队时唤醒链首一个等待者。
/// <para><b>注意</b>：本类不是通用 Channel，不支持单消费者背压 / 完成（Complete）等高级语义。
/// 适合内核 IO 响应队列等"持续运行、永不关闭"的场景。</para>
/// </summary>
public sealed class AsyncQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new();

    // 等待者链表（LIFO 栈式）。每个等待者一个独立的 PooledValueTaskSource。
    private WaitNode? _waitHead;
    private WaitNode? _nodePoolHead;
    private int _pooledNodeCount;
    private int _waitNodeAllocations;
    private readonly object _waitLock = new();
    private const int MaxPooledWaitNodes = 256;

    private sealed class WaitNode
    {
        public PooledValueTaskSource Source = null!;
        public WaitNode? Next;
        public int Owners; // waiter + 已摘链但尚未完成信号的 Enqueue
    }

    /// <summary>队列中当前元素数量。</summary>
    public int Count => _queue.Count;

    /// <summary>
    /// 异步入队一个元素。若有等待者，唤醒链首一个等待者。
    /// </summary>
    /// <param name="item">要入队的元素。</param>
    public void Enqueue(T item)
    {
        _queue.Enqueue(item);

        // 唤醒一个等待者（FIFO 公平性不保证，LIFO 栈式取链首）
        WaitNode? toWake = null;
        lock (_waitLock)
        {
            if (_waitHead is not null)
            {
                toWake = _waitHead;
                _waitHead = toWake.Next;
                toWake.Next = null;
            }
        }

        if (toWake is not null)
        {
            try
            {
                toWake.Source.MarkOrComplete();
            }
            finally
            {
                ReleaseWaitNode(toWake);
            }
        }
    }

    /// <summary>
    /// 异步出队一个元素。若队列为空，等待直到有元素入队或取消。
    /// </summary>
    /// <param name="cancellationToken">用于取消等待的 <see cref="CancellationToken"/>。</param>
    /// <returns>一个表示异步操作的 <see cref="ValueTask{TResult}"/>，结果为出队的元素。</returns>
    public async ValueTask<T> DequeueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 快速路径：队列非空，直接出队（零分配）
        if (_queue.TryDequeue(out var item))
            return item;

        // 慢速路径：队列空，租用等待节点入链
        return await DequeueSlowAsync(cancellationToken).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async ValueTask<T> DequeueSlowAsync(CancellationToken cancellationToken)
    {
        for (; ; )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = RentWaitNode();
            if (cancellationToken.CanBeCanceled)
                node.Source.AttachCancellation(cancellationToken);

            lock (_waitLock)
            {
                // double-check：拿到锁后可能已有 Enqueue
                if (_queue.TryDequeue(out var item))
                {
                    ReleaseWaitNode(node);
                    return item;
                }
                // 入等待链（LIFO）
                node.Owners = 2;
                node.Next = _waitHead;
                _waitHead = node;
            }

            try
            {
                await new ValueTask(node.Source, node.Source.Version).ConfigureAwait(false);
            }
            finally
            {
                // 被取消且仍在链中时，本方同时释放链所有权；Enqueue 已摘链时由完成方释放。
                if (RemoveWaitNode(node))
                    ReleaseWaitNode(node);
                ReleaseWaitNode(node);
            }

            // 被唤醒后重试出队（可能被 spurious 唤醒或被其他消费者抢先）
            if (_queue.TryDequeue(out var result))
                return result;
        }
    }

    /// <summary>
    /// 同步等待队列有至少一个元素（不取出）。
    /// </summary>
    public void WaitForEntry()
    {
        if (!_queue.IsEmpty) return;
        var spin = new SpinWait();
        while (_queue.IsEmpty)
            spin.SpinOnce();
    }

    /// <summary>
    /// 异步等待队列有至少一个元素（不取出）。若队列非空，立即返回；否则挂起直到有元素入队或取消。
    /// </summary>
    /// <param name="cancellationToken">用于取消等待的 <see cref="CancellationToken"/>。</param>
    /// <returns>任务在队列有至少一个元素时完成（不取出元素）；<paramref name="cancellationToken"/> 取消时以
    /// <see cref="OperationCanceledException"/> 完成。</returns>
    public async ValueTask WaitForEntryAsync(CancellationToken cancellationToken = default)
    {
        if (!_queue.IsEmpty) return;

        // 复用 DequeueAsync 的等待机制，但不实际取走元素
        var node = RentWaitNode();
        if (cancellationToken.CanBeCanceled)
            node.Source.AttachCancellation(cancellationToken);

        lock (_waitLock)
        {
            if (!_queue.IsEmpty)
            {
                ReleaseWaitNode(node);
                return;
            }
            node.Owners = 2;
            node.Next = _waitHead;
            _waitHead = node;
        }

        try
        {
            await new ValueTask(node.Source, node.Source.Version).ConfigureAwait(false);
        }
        finally
        {
            if (RemoveWaitNode(node))
                ReleaseWaitNode(node);
            ReleaseWaitNode(node);
        }
    }

    /// <summary>
    /// 尝试同步出队一个元素。若队列为空，返回 false。
    /// </summary>
    /// <param name="item">出队的元素。</param>
    /// <returns>如果成功出队，返回 true；否则返回 false。</returns>
    public bool TryDequeue(out T item) => _queue.TryDequeue(out item!);

    /// <summary>从等待链中摘除指定节点（被唤醒或取消后调用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RemoveWaitNode(WaitNode target)
    {
        lock (_waitLock)
        {
            if (_waitHead is null) return false;
            if (ReferenceEquals(_waitHead, target))
            {
                _waitHead = target.Next;
                target.Next = null;
                return true;
            }
            var prev = _waitHead;
            while (prev.Next is not null)
            {
                if (ReferenceEquals(prev.Next, target))
                {
                    prev.Next = target.Next;
                    target.Next = null;
                    return true;
                }
                prev = prev.Next;
            }
            return false;
        }
    }

    private WaitNode RentWaitNode()
    {
        WaitNode node;
        lock (_waitLock)
        {
            if (_nodePoolHead is { } pooled)
            {
                _nodePoolHead = pooled.Next;
                _pooledNodeCount--;
                node = pooled;
            }
            else
            {
                node = new WaitNode();
                _waitNodeAllocations++;
            }
        }

        node.Next = null;
        node.Owners = 1;
        node.Source = PooledValueTaskSource.Rent();
        return node;
    }

    private void ReleaseWaitNode(WaitNode node)
    {
        if (Interlocked.Decrement(ref node.Owners) != 0)
            return;

        PooledValueTaskSource.Return(node.Source);
        lock (_waitLock)
            ReturnWaitNodeLocked(node);
    }

    private void ReturnWaitNodeLocked(WaitNode node)
    {
        node.Source = null!;
        node.Owners = 0;
        if (_pooledNodeCount >= MaxPooledWaitNodes)
        {
            node.Next = null;
            return;
        }

        node.Next = _nodePoolHead;
        _nodePoolHead = node;
        _pooledNodeCount++;
    }

    internal int WaitNodeAllocationCountForTest => Volatile.Read(ref _waitNodeAllocations);
}
