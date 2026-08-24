using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Collections;

/// <summary>
/// 通用并发优先队列——基于细粒度锁 skip-list（Herlihy 2006 lazy 版本）。
/// <para>★ 任意可比优先级：支持任意 <see cref="long"/> 优先级值（值小者先出）。</para>
/// <para>★ 严格优先级 + 同优先级 FIFO：key = (priority, sequence)，sequence 单调递增保证同优先级 FIFO。</para>
/// <para>★ 细粒度锁：每节点一把 <see cref="SpinLock"/>，hand-over-hand 加锁，不同区间操作并行。</para>
/// <para>★ lazy validation：Peek/Find 不加锁（读 marked 字段）；Insert/DeleteMin 加锁后 validate（全层级——
///   标记即完整摘除，marked 节点永为瞬态）。</para>
/// <para>★ 内存回收简单：unlink + 解锁后节点安全可回收（无需 epoch/hazard pointer）。</para>
/// <para>★ 定位：细分锁实现——任意 long 优先级 + 消费者较少的场景。严格优先级+FIFO 语义使同优先级尾插与
///   min 头摘是两个串行热点，吞吐上限由热点决定；无锁高吞吐场景用 <see cref="AsyncPriorityQueue{T}"/>
///   （Route A 生产基线），离散枚举优先级用 <see cref="BucketPriorityQueue{TPriority,T}"/>。</para>
/// <para>算法依据：Herlihy, Lev, Luchangco, Shavit. "A Simple Optimistic Skip-List Algorithm" (DISC 2006)。</para>
/// </summary>
/// <typeparam name="T">元素类型。</typeparam>
[SuppressMessage("Naming", "CA1711:标识符应采用正确的后缀")]
public sealed class SkipListPriorityQueue<T> : IDisposable
{
    // ════════════════════════════════════════════════════════════
    //  常量
    // ════════════════════════════════════════════════════════════

    private const double Probability = 0.5;   // skip-list 层级概率（p=0.5 几何分布）
    private const int DefaultMaxLevel = 31;

    // ════════════════════════════════════════════════════════════
    //  Node——skip-list 节点（每节点一把锁 + marked 字段）
    // ════════════════════════════════════════════════════════════

    private sealed class Node
    {
        internal readonly long Key;          // (priority << 48) | sequence——排序键
        internal readonly int Priority;
        internal readonly long Sequence;
        internal readonly T Item;

        // ★ 结构优化（#PERF-005）：level-0 边内联（~50% 节点只有这一条边——省一次数组分配）；
        //   level ≥1 边存高位数组（长度 = topLevel，索引 level-1）。Forward[i] 访问统一走 Get/SetForward。
        internal Node? Forward0;
        internal Node?[]? ForwardHigh;

        // ★ lazy 删除标志：volatile bool。逻辑删除置 true，物理删除 unlink。
        //   forward 和 marked 不需要原子打包——加锁期间保证一致性。
        internal volatile bool Marked;

        // ★ 每节点一把锁（细粒度）。SpinLock 不阻塞 ThreadPool（自旋），避免 testhost 优雅退出卡死。
        //   不可重入——加锁前必须去重（同节点不重锁）。
        internal SpinLock Lock = new(enableThreadOwnerTracking: false);

        internal Node(long key, int priority, long sequence, T item, int topLevel)
        {
            Key = key;
            Priority = priority;
            Sequence = sequence;
            Item = item;
            if (topLevel > 0) ForwardHigh = new Node?[topLevel];
        }

        internal int TopLevel => ForwardHigh?.Length ?? 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Node? GetForward(int level) => level == 0 ? Forward0 : ForwardHigh![level - 1];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetForward(int level, Node? next)
        {
            if (level == 0) Forward0 = next;
            else ForwardHigh![level - 1] = next;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  实例字段
    // ════════════════════════════════════════════════════════════

    private readonly int _maxLevel;
    private readonly Node _head;            // 哨兵头节点（key = long.MinValue）
    private readonly AsyncManualResetEvent _signal = new();
    private long _sequenceCounter;
    private long _count;
    private int _disposed;

    // ════════════════════════════════════════════════════════════
    //  构造
    // ════════════════════════════════════════════════════════════

    /// <summary>创建 skip-list 优先队列。</summary>
    /// <param name="maxLevel">
    /// 最大层数（express lane 密度）——⚠️ 2^maxLevel 应 ≥ 预期条目数：层级不足时查找从 O(log n) 退化为
    /// O(n)（实测 maxLevel=5 @ 20 万条 = 54µs/op vs 31 层 = 1.1µs/op）。默认 31 覆盖至 2^31 条。
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">maxLevel 不在 [1, 31]。</exception>
    public SkipListPriorityQueue(int maxLevel = DefaultMaxLevel)
    {
        if (maxLevel is < 1 or > DefaultMaxLevel)
            throw new ArgumentOutOfRangeException(nameof(maxLevel), maxLevel,
                $"maxLevel 须在 [1, {DefaultMaxLevel}]；层级决定 express lane 密度，2^maxLevel 应 ≥ 预期条目数，否则查找退化为 O(n)");
        _maxLevel = maxLevel;
        _head = new Node(long.MinValue, int.MinValue, long.MinValue, default!, maxLevel);
    }

    // ════════════════════════════════════════════════════════════
    //  ★ ThreadStatic 工作缓冲（#PERF-003）——preds/succs/lockBuf/lockTaken 原每 op 分配 4 个数组
    //  （Enqueue 每 op 共 6 次堆分配 ≈1.5KB，单线程吞吐被 GC 压到 0.88M/s）；改为线程内复用后
    //  每 op 仅剩 Node + Forward 两笔载荷分配。
    //  安全前提：本队列操作无同线程重入（_signal 唤醒走线程池，无内联续体；无回调）。
    // ════════════════════════════════════════════════════════════

    [ThreadStatic] private static Node[]? t_preds;
    [ThreadStatic] private static Node?[]? t_succs;   // 后继链层级尾可为 null（语义如此——非空断言是说谎）
    [ThreadStatic] private static Node[]? t_lockBuf;
    [ThreadStatic] private static bool[]? t_lockTaken;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node[] RentPreds()
        => t_preds is { } p && p.Length >= _maxLevel + 1 ? p : t_preds = new Node[_maxLevel + 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node?[] RentSuccs()
        => t_succs is { } p && p.Length >= _maxLevel + 1 ? p : t_succs = new Node?[_maxLevel + 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node[] RentLockBuf()
        => t_lockBuf is { } p && p.Length >= _maxLevel + 2 ? p : t_lockBuf = new Node[_maxLevel + 2];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool[] RentLockTaken()
        => t_lockTaken is { } p && p.Length >= _maxLevel + 2 ? p : t_lockTaken = new bool[_maxLevel + 2];

    // ════════════════════════════════════════════════════════════
    //  随机层级（thread-local xorshift，无 lock 无分配）
    // ════════════════════════════════════════════════════════════

    [ThreadStatic] private static uint _tRandState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RandomLevel()
    {
        var s = _tRandState;
        if (s == 0) s = (uint)Environment.CurrentManagedThreadId * 2654435761u | 1;
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        _tRandState = s;

        var level = 0;
        while ((s & 1) == 0 && level < _maxLevel)
        {
            level++;
            s >>= 1;
        }
        return level;
    }

    // ════════════════════════════════════════════════════════════
    //  FIND——不加锁遍历（lazy 版本核心）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 查找 key 的前驱链 preds[] 和后继链 succs[]。<b>不加锁</b>。
    /// <para>★ 遇 marked 节点：直接跳过（<c>curr = curr.Forward[level]</c>，pred 不更新）。marked
    ///   节点的 Forward 在 mark 后恒定、指向有效后继，跟着走安全。pred 保持在最后一个 unmarked 节点，
    ///   使 preds[] 不含 marked 节点——加锁时不会去锁一个正在被摘除的节点。</para>
    /// <para>★ marked 节点的物理摘除**不在 Find 做**——Find 是纯读（Peek/Enqueue/Dequeue 共用）。
    ///   level-0 链头的 marked 残留由 <see cref="TryDequeue"/> 持锁后统一清理（见 victim.Marked 分支）。</para>
    /// <para>★ validate 在加锁后做（pred.Forward==curr 且 unmarked）——Find 返回的 preds/succs 可能过期，
    ///   加锁后 validate 失败则 retry。</para>
    /// </summary>
    private void Find(long key, Node[] preds, Node?[] succs)
    {
        var pred = _head;
        for (var level = _maxLevel; level >= 0; level--)
        {
            var curr = pred.GetForward(level);
            while (curr is not null)
            {
                // ★ 遇 marked：跟着 curr.Forward 走（victim.Forward 在 mark 后不变，指向有效后继）
                if (curr.Marked)
                {
                    curr = curr.GetForward(level);
                    continue;
                }
                if (curr.Key >= key) break;
                pred = curr;
                curr = curr.GetForward(level);
            }
            preds[level] = pred;
            succs[level] = curr;
        }
    }

    /// <summary>
    /// 返回 level-0 链上的第一个节点（head 的直接后继），<b>不论是否 marked</b>。不加锁。
    /// <para>★ 不在此跳过 marked——物理摘除由 <see cref="TryDequeue"/> 持锁后统一处理（见 victim.Marked
    ///   分支的 UnlinkVictim）。在此跳过反而有害：FindFirst 返回链中非首位的 unmarked victim，
    ///   但 Find 跳过 marked 后 preds[0]=head，而 head.Forward[0] 仍指 marked 节点（≠victim），
    ///   validate 恒失败 → 无进展活锁。</para>
    /// </summary>
    private Node? FindFirst() => _head.Forward0;

    // ════════════════════════════════════════════════════════════
    //  ENQUEUE（Insert）
    // ════════════════════════════════════════════════════════════

    public void Enqueue(T item, long priority)
    {
        var sequence = Interlocked.Increment(ref _sequenceCounter);
        var key = MakeKey(priority, sequence);

        var preds = RentPreds();
        var succs = RentSuccs();

        while (true)
        {
            var topLevel = RandomLevel();
            Find(key, preds, succs);

            var newNode = new Node(key, (int)priority, sequence, item, topLevel);

            // ★ 收集 preds[0..topLevel] 去重（引用比较）。
            //   preds 按 skip-list 遍历方向天然 key 升序——无需 Sort。
            var toLockBuf = RentLockBuf();
            var lockCount = 0;
            for (var i = 0; i <= topLevel; i++)
            {
                var p = preds[i];
                var dup = false;
                for (var j = 0; j < lockCount; j++)
                    if (toLockBuf[j] == p) { dup = true; break; }
                if (!dup) toLockBuf[lockCount++] = p;
            }

            // 批量加锁
            var locksTaken = RentLockTaken();
            Array.Clear(locksTaken, 0, lockCount);
            var linked = false;
            try
            {
                for (var i = 0; i < lockCount; i++)
                    Enter(toLockBuf[i], ref locksTaken[i]);

                // ★ lazy validate
                var valid = true;
                for (var i = 0; i <= topLevel; i++)
                {
                    if (preds[i].Marked || preds[i].GetForward(i) != succs[i])
                    { valid = false; break; }
                }
                if (valid)
                {
                    for (var i = 0; i <= topLevel; i++)
                        newNode.SetForward(i, succs[i]);
                    for (var i = topLevel; i >= 0; i--)
                        preds[i].SetForward(i, newNode);
                    linked = true;
                }
            }
            finally
            {
                for (var i = lockCount - 1; i >= 0; i--)
                    if (locksTaken[i]) Exit(toLockBuf[i]);
            }

            if (!linked) continue;   // validate 失败——重试

            // ★ 锁外收尾（#PERF-005）：计数与唤醒不占 pred 自旋锁——缩短锁持有期，
            //   生产者尾插热点上的锁竞争区间只含"校验+链接"。
            Interlocked.Increment(ref _count);
            _signal.Set();
            return;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  DEQUEUE / DeleteMin
    // ════════════════════════════════════════════════════════════

    public bool TryDequeue(out T item)
    {
        var preds = RentPreds();
        var succs = RentSuccs();

        retry:
        var victim = FindFirst();
        if (victim is null)
        {
            item = default!;
            return false;
        }

        // find victim 的各层前驱（key = victim.Key 精确定位）
        Find(victim.Key, preds, succs);

        // ★ 收集要锁的节点：victim + preds[0..victim.TopLevel]，去重（引用比较）。
        var victimTopLevel = victim.TopLevel;
        var toLock = RentLockBuf();
        var lockCount = 0;
        toLock[lockCount++] = victim;
        for (var i = 0; i <= victimTopLevel; i++)
        {
            var p = preds[i];
            var dup = false;
            for (var j = 0; j < lockCount; j++)
                if (toLock[j] == p) { dup = true; break; }
            if (!dup) toLock[lockCount++] = p;
        }

        var locksTaken = RentLockTaken();
        Array.Clear(locksTaken, 0, lockCount);
        try
        {
            for (var i = 0; i < lockCount; i++)
                Enter(toLock[i], ref locksTaken[i]);

            // ★ validate：victim 已被并发逻辑删除？
            //   是 → 这不是"重试就完事"——lazy 协议要求每个见到 marked victim 的操作接力物理清理，
            //   否则 marked 节点永久滞留链上，FindFirst 反复命中同一 victim（Bug：活锁零进展）。
            //   victim 与 preds 都在锁内，helping 清理无新竞态；两个 DEQ 同时 help 同一 victim 写入
            //   相同的 victim.Forward[i]，幂等。preds[i].Forward[i]==victim 守卫防止误改已变更的链。
            if (victim.Marked)
            {
                UnlinkVictim(victim, victimTopLevel, preds);
                goto retry;
            }

            // ★ 全层级 validate（#PERF-004，对齐 Herlihy 论文）：仅校验 level-0 会在高层 preds 陈旧时
            //   unlink 守卫失手——被删除节点永久滞留高层链，Find 的 marked-skip 又不在 key 处 break，
            //   每次查找扫完整条 marked 前缀（实测 2P+2C 时前缀增至 2 万+、Find 退化 O(n)）。
            //   全层级校验保证"标记即完整摘除"，marked 节点永远瞬态。
            var valid = true;
            for (var i = 0; i <= victimTopLevel; i++)
            {
                if (preds[i].Marked || preds[i].GetForward(i) != victim)
                { valid = false; break; }
            }
            if (!valid) goto retry;

            // ★ 逻辑删除（声明所有权）+ 物理删除（持锁期间原子完成）
            victim.Marked = true;
            UnlinkVictim(victim, victimTopLevel, preds);

            item = victim.Item!;
            Interlocked.Decrement(ref _count);
            return true;
        }
        finally
        {
            for (var i = lockCount - 1; i >= 0; i--)
                if (locksTaken[i]) Exit(toLock[i]);
        }
    }

    /// <summary>
    /// 物理摘除 victim——把各层 preds[i] 指向 victim 的边改指向 victim.Forward[i]。
    /// <para>★ 守卫 <c>preds[i].Forward[i] == victim</c>：preds 可能是过期的 Find 结果
    ///   （并发改链后 preds[i] 已不指 victim），跳过这类已失效的层；只摘仍指 victim 的边。
    ///   幂等：victim.Forward 在 mark 后恒定，多个 helper 写入相同值。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnlinkVictim(Node victim, int victimTopLevel, Node[] preds)
    {
        for (var i = 0; i <= victimTopLevel; i++)
        {
            if (preds[i].GetForward(i) == victim)
                preds[i].SetForward(i, victim.GetForward(i));
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Peek（不加锁——lazy 版本特性）
    // ════════════════════════════════════════════════════════════

    public bool TryPeek(out T item)
    {
        // 跳过已 marked 的首节点（它们会被后续 DeleteMin 清理）
        var curr = _head.Forward0;
        while (curr is not null && curr.Marked)
            curr = curr.Forward0;

        if (curr is null)
        {
            item = default!;
            return false;
        }
        item = curr.Item!;
        return true;
    }

    // ════════════════════════════════════════════════════════════
    //  异步出队
    // ════════════════════════════════════════════════════════════

    public async ValueTask<T> DequeueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryDequeue(out var fast)) return fast;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _signal.Reset();
            if (TryDequeue(out var item)) return item;
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  辅助
    // ════════════════════════════════════════════════════════════

    public int Count => (int)Interlocked.Read(ref _count);

    private static long MakeKey(long priority, long sequence)
    {
        // priority 占高 16 位，sequence 占低 48 位
        // 同 priority 按 sequence 升序（FIFO）
        return (priority << 48) | (sequence & 0xFFFFFFFFFFFF);
    }

    /// <summary>批量加锁的第 i 把。调用方负责去重（同节点不重锁，防 SpinLock 不可重入自死锁）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Enter(Node node, ref bool taken) => node.Lock.Enter(ref taken);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Exit(Node node) => node.Lock.Exit(false);

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        _signal.Set();
    }
}
