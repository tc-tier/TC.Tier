using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Epochs;

namespace TC.Tier.Core.Collections;

/// <summary>
/// 异步优先队列——基于 lock-free 跳表（Fomitchev–Ruppert marker 删除协议）。
/// <para>★ 高并发入队/出队 O(log n) 平均复杂度；出队时队列为空则异步等待或取消。</para>
/// <para>★ <b>Route A 正确性基线（2026-08-14 根因档案决策）</b>：回归论文前提世界——
///   边存<b>直接对象引用</b> + <b>marker 节点</b>表达逻辑删除（单引用 64 位 CAS，无 128 位打包）、
///   节点 fresh 分配交 GC 回收（不池化、不 epoch、不 Id 寻址）。论文不变式 I1~I5 由构造消除，
///   不再自证回收协议。性能代价：每次入队一次 Gen0 分配（LOH 无压力）。详见
///   <see cref="SkipListPriorityQueue{T}"/>（锁基变体）与 docs/async-priority-queue-root-cause.md。</para>
/// <para><b>marker 协议要点</b>：victim 的 <c>Forward[L]</c> 被 CAS 成 <see cref="Marker"/>
///   （marker.Next 持真实后继）即"已逻辑删除"；物理摘除 = 前驱边 CAS 绕过 victim 直连
///   marker.Next。marker 永不被改写，victim 摘除后整体交给 GC——并发读者永不会读到回收后的节点。</para>
/// <para>★ 并发语义：多生产者安全；多消费者不丢不重，但并发出队时"最小元素"判定是
///   竞争性的（各出队以各自 mark CAS 为线性化点），严格最小序需要单消费者（或
///   <see cref="SkipListPriorityQueue{T}"/> 的持锁 DeleteMin）。</para>
/// <para>★ DEBUG 构建内置链校验器（key 严格递增 + 成环护栏），每 64 次操作自动巡检，
///   测试可显式调 <see cref="ValidateInvariants"/> 定点取证。</para>
/// </summary>
/// <typeparam name="T">队列中存储的元素类型。</typeparam>
[SuppressMessage("Naming", "CA1711:标识符应采用正确的后缀")]
public sealed class AsyncPriorityQueue<T> : IDisposable
{
    // ════════════════════════════════════════════════════════════
    //  链边载荷：Node（数据节点） | Marker（逻辑删除标记）
    // ════════════════════════════════════════════════════════════

    private class Link { }

    /// <summary>跳表数据节点。Key/Priority/Sequence/Item 发布后不可变（I1 由构造保证）。</summary>
    private sealed class Node : Link
    {
        internal readonly long Key;         // (priority << 48) | sequence——排序键
        internal readonly int Priority;
        internal readonly long Sequence;
        internal readonly T Item;
        internal readonly Link?[] Forward;  // Forward[L] = Node | Marker | null
        internal Node(long key, int priority, long sequence, T item, int level)
        {
            Key = key; Priority = priority; Sequence = sequence; Item = item;
            Forward = new Link[level + 1];
        }
        internal int TopLevel => Forward.Length - 1;
    }

    /// <summary>逻辑删除标记。victim.Forward[L] = Marker(succ) ⇔ victim 已在 L 层被逻辑删除。
    /// Next 不可变——splice 永远能读到死节点的后继（I3 由构造保证）。</summary>
    private sealed class Marker : Link
    {
        internal readonly Node? Next;
        internal Marker(Node? next) => Next = next;
    }

    // ════════════════════════════════════════════════════════════
    //  实例字段
    // ════════════════════════════════════════════════════════════

    private readonly int _maxLevel;
    private readonly Node _head;             // 哨兵（key = long.MinValue，永不标记/摘除）
    private readonly AsyncManualResetEvent _signal = new();
    private long _sequenceCounter;
    private long _count;
    private int _disposed;
    private long _opCount;                   // DEBUG 校验器节流计数

    private const int DebugValidateStride = 64;

    /// <summary>获取队列中当前元素的近似数量。并发下可能不精确，仅用于诊断和监控。</summary>
    public int Count { get { var c = Interlocked.Read(ref _count); return c < 0 ? 0 : (int)c; } }

    /// <summary>
    /// 创建异步优先队列实例。
    /// </summary>
    /// <param name="epoch">保留参数以兼容既有调用方。<b>Route A 后节点回收交给 GC，
    /// 不再依赖 epoch</b>——传入共享 <see cref="LightEpoch"/> 或 null 均可（不再校验）。</param>
    /// <param name="maxLevel">跳表最大层数。</param>
    public AsyncPriorityQueue(LightEpoch? epoch = null, int maxLevel = 31)
    {
        _ = epoch;
        _maxLevel = maxLevel;
        _head = new Node(long.MinValue, int.MinValue, long.MinValue, default!, maxLevel);
    }

    // ════════════════════════════════════════════════════════════
    //  随机层级（thread-local xorshift，无锁无分配）
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
    //  FIND——marker 感知遍历 + 顺带物理摘除（helping 协议）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 查找 key 的各层前驱链 preds[] 与后继链 succs[]。
    /// <para>★ 遍历中顺带清理：curr 已标记（<c>curr.Forward[L] is Marker</c>）→ CAS
    ///   <c>pred.Forward[L] = marker.Next</c> 物理摘除（尽力而为，失败即继续——正确性不依赖）。</para>
    /// <para>★ 遇 <c>pred.Forward[L] is Marker</c>（pred 已删）只跟随不 splice：splice 需要
    ///   pred 的前驱，且直接改写会把 mark 擦掉、令已删节点"复活"——这是旧实现断链/自环的
    ///   根源之一，marker 协议下禁止。</para>
    /// <para>★ 返回的 preds[L] 保证在该层未标记（pred 只在 curr 未标记时推进）；
    ///   succs[L] 为 Node 或 null，绝不返回 Marker。</para>
    /// </summary>
    private void Find(long key, Node[] preds, Node?[] succs)
    {
        var pred = _head;
        for (var level = _maxLevel; level >= 0; level--)
        {
            var curr = Volatile.Read(ref pred.Forward[level]);
            Node? curNode;
            while (true)
            {
                // pred 已删：跟随 marker 走到真实后继（不 splice，理由见上）
                while (curr is Marker m)
                    curr = m.Next;

                if (curr is null) { curNode = null; break; }
                curNode = (Node)curr;

                var next = Volatile.Read(ref curNode.Forward[level]);
                if (next is Marker m2)
                {
                    // curr 已标记 → 物理摘除（helping）：pred 绕过 curr 直连真实后继
                    Interlocked.CompareExchange(ref pred.Forward[level], m2.Next, curNode);
                    curr = m2.Next;
                    continue;
                }
                if (curNode.Key < key) { pred = curNode; curr = next; continue; }
                break;
            }
            preds[level] = pred;
            succs[level] = curNode;
        }
    }

    /// <summary>level-L 链接 CAS：pred.Forward[L] 从 succ 改为 node。succ 为 null 时即尾插。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryLink(Node pred, int level, Node? succ, Node node)
        => Interlocked.CompareExchange(ref pred.Forward[level], node, succ) == succ;

    // ════════════════════════════════════════════════════════════
    //  ENQUEUE——level-0 严格发布（唯一关键层），高层 best-effort 加速
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 入队元素。
    /// </summary>
    /// <param name="item">要入队的元素。</param>
    /// <param name="priority">元素的优先级（值小者先出）。</param>
    /// <exception cref="ObjectDisposedException">当队列已被释放时抛出。</exception>
    public void Enqueue(T item, int priority)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var seq = Interlocked.Increment(ref _sequenceCounter);
        var key = ((long)priority << 48) | (seq & 0xFFFFFFFFFFFF);
        var topLevel = RandomLevel();
        var node = new Node(key, priority, seq, item, topLevel);

        var preds = new Node[_maxLevel + 1];
        var succs = new Node?[_maxLevel + 1];
        var spin = new SpinWait();

        // ★ 发布纪律：node 的全部 Forward 字段在 level-0 发布（唯一使节点可达的动作）**之前**写完，
        //   发布之后绝不再写 node 自身的任何字段。违反的后果（2026-08-17 压测 dump 取证）：
        //   发布后删除者可立即标记其高层（读到未写完的 null → Marker），随后本线程的普通写
        //   会把 Marker 覆盖掉——已删节点"复活"成未标节点并被 TryLink 链入高层，其 level-0
        //   已被摘除 → Find 从此僵尸 pred 出发永远走不到真链头 → 队头 victim 已标无人摘 →
        //   全体消费者自旋冻结（活性死锁，结构不断裂故 DEBUG 校验器不报警）。
        while (true)
        {
            Find(key, preds, succs);
            for (var i = 0; i <= topLevel; i++)
                node.Forward[i] = succs[i];
            if (TryLink(preds[0], 0, succs[0], node)) break;
            spin.SpinOnce();
        }

        // 高层加速层：尽力链接，失败忽略——level-0 已发布，节点完全可达（不丢元素、不成环）。
        // ★ 尾插（succs==null）也必须 CAS：跳过会让"插到层尾"的节点永不进该层，持续尾插负载
        //   （如单一最大优先级段）下高层索引永不建立，Find 退化 level-0 线性扫描（256K 积压
        //   尾插实测 1.9ms/op——docs/perf/priority-queues-performance.md）。
        //   本循环只 CAS 前驱字段、不写 node 自身——node 的字段此后只有两类写者：删除者的
        //   Marker CAS 与 Find 的 helping splice（CAS 普通后继）——均为协议内演进。
        for (var i = 1; i <= topLevel; i++)
            TryLink(preds[i], i, succs[i], node);

        Interlocked.Increment(ref _count);
        _signal.Set();
        DebugValidateThrottled();
    }

    // ════════════════════════════════════════════════════════════
    //  DEQUEUE——mark level-0 决胜（线性化点）→ mark 高层 → helping 摘除
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 尝试出队最小元素。
    /// </summary>
    /// <param name="item">出队的元素。</param>
    /// <returns>如果成功出队元素，则返回 true；否则返回 false。</returns>
    public bool TryDequeue(out T item)
    {
        item = default!;
        if (_disposed != 0) return false;

        var preds = new Node[_maxLevel + 1];
        var succs = new Node?[_maxLevel + 1];
        var spin = new SpinWait();

        while (true)
        {
            // 取 level-0 首节点；顺带摘除 head 边上的残留 marker（head 永不标记，splice 安全）
            var victim = Volatile.Read(ref _head.Forward[0]);
            while (victim is Marker m)
            {
                Interlocked.CompareExchange(ref _head.Forward[0], m.Next, victim);
                victim = m.Next;
            }
            if (victim is null) return false;
            var victimNode = (Node)victim;

            // ★ mark level-0 决胜（线性化点）：唯一把 victim.Forward[0] CAS 成 Marker 的线程
            //   是胜者。已标记（并发删除）或 CAS 失败 → 重试下一个 victim。
            var next0 = Volatile.Read(ref victimNode.Forward[0]);
            if (next0 is Marker) { spin.SpinOnce(); continue; }
            if (Interlocked.CompareExchange(ref victimNode.Forward[0], new Marker(next0 as Node), next0) != next0)
            { spin.SpinOnce(); continue; }

            // 胜者：其余层标记到落地（marker.Next 持该层真实后继，null 合法=层尾）。
            // ★ 必须重试到 Marker 落地：标记 CAS 会与 Find 的 helping splice 竞争同一字段
            //   （splice 也 CAS victim 的边、写普通后继引用）——被打败即留下"已删未标"字段：
            //   节点已出队（F0 已标）但高层边仍是普通引用，Find 无法识别为已删 → 永不摘除 →
            //   悬挂僵尸（key 小于层 0 队头）→ 后续 Enqueue 的 preds 落在僵尸旧世界上（发布
            //   CAS 恒失败）+ 队头已标无人摘 → 全体自旋的活性死锁（2026-08-17 压测取证：
            //   示波器抓到发布后 F0 被 splice 改写 + dump 抓到 F2 未标僵尸 + 双侧自旋栈）。
            //   收敛性：splice 使 victim.F[i] 沿链前进有限步后停止（后继未删时无 splice），
            //   此后标记 CAS 是唯一写者——必然成功。
            for (var i = 1; i < victimNode.Forward.Length; i++)
            {
                while (true)
                {
                    var nextI = Volatile.Read(ref victimNode.Forward[i]);
                    if (nextI is Marker) break;
                    if (Interlocked.CompareExchange(ref victimNode.Forward[i], new Marker(nextI as Node), nextI) == nextI)
                        break;
                }
            }

            item = victimNode.Item;

            // helping：走一遍 Find 把 victim 从各层物理摘除（尽力而为，摘不干净后续操作会接力）
            Find(victimNode.Key, preds, succs);

            Interlocked.Decrement(ref _count);
            DebugValidateThrottled();
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  PEEK
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 查看队首元素而不移除。
    /// </summary>
    /// <param name="item">队首元素。</param>
    /// <returns>如果队列非空，则返回 true；否则返回 false。</returns>
    public bool TryPeek(out T item)
    {
        item = default!;
        if (_disposed != 0) return false;

        var curr = Volatile.Read(ref _head.Forward[0]);
        while (curr is not null)
        {
            if (curr is Marker m) { curr = m.Next; continue; }
            var n = (Node)curr;
            if (Volatile.Read(ref n.Forward[0]) is Marker m2) { curr = m2.Next; continue; }
            item = n.Item;
            return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════
    //  异步出队
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 异步出队一个元素。如果队列为空，将异步等待直到有元素入队或取消。
    /// </summary>
    /// <param name="ct">用于取消操作的取消令牌。</param>
    /// <returns>一个表示异步操作的 ValueTask，当操作完成时返回出队的元素。</returns>
    public ValueTask<T> DequeueAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return TryDequeue(out var item) ? new ValueTask<T>(item) : new ValueTask<T>(DequeueSlowAsync(ct));
    }

    private async Task<T> DequeueSlowAsync(CancellationToken ct)
    {
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            _signal.Reset();
            if (TryDequeue(out var item)) return item;
            await _signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  DEBUG 链校验器——自环/后向边/断链当场抓获
    // ════════════════════════════════════════════════════════════

    /// <summary>每 <see cref="DebugValidateStride"/> 次操作自动巡检一次（DEBUG 有效，Release 无开销）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DebugValidateThrottled()
    {
        if ((Interlocked.Increment(ref _opCount) & (DebugValidateStride - 1)) == 0)
            ValidateInvariants();
    }

    /// <summary>
    /// 走 level-0 主链校验结构不变式：key 严格递增（防后向边/自环）、marker 链长 ≤1、
    /// 步数护栏（防成环）。<b>DEBUG 构建有效</b>（Release 为空操作），供测试与 Debug 巡检调用。
    /// </summary>
    internal void ValidateInvariants()
    {
#if DEBUG
        long last = long.MinValue;
        Link? curr = _head.Forward[0];
        var steps = 0;
        const int maxSteps = 1 << 24;
        while (curr is not null)
        {
            if (++steps > maxSteps)
            {
                Debug.Fail($"AsyncPriorityQueue level-0 链步数超护栏（疑似成环）：{steps}");
                return;
            }
            if (curr is Marker m)
            {
                // marker 链必须长为 1：marker.Next 不允许再是 marker
                Debug.Assert(m.Next is not Marker, "marker.Next 是 marker——marker 链非法");
                curr = m.Next;
                continue;
            }
            var n = (Node)curr;
            Debug.Assert(n.Key > last, $"level-0 链 key 非严格递增：prev={last} cur={n.Key}");
            last = n.Key;
            curr = Volatile.Read(ref n.Forward[0]);
        }
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  DISPOSE
    // ════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _signal.Set();
        // 节点/标记全部交 GC——Route A 无池、无 epoch、无待回收队列
    }
}
