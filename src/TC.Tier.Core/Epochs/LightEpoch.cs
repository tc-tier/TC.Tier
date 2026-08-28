using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace TC.Tier.Core.Epochs;

/// <summary>
/// 轻量级 epoch 回收机制，用于内存管理。
/// </summary>
public sealed unsafe class LightEpoch : IDisposable
{
    /// <summary>
    /// 单独存放线程静态元数据，避免每个实例一个 ThreadStatic 字段的开销。
    /// </summary>
    private static class Metadata
    {
        /// <summary>
        /// 当前线程的托管线程 ID。
        /// </summary>
        [ThreadStatic]
        internal static int ThreadId;

        /// <summary>
        /// 在 epoch 表中预留条目的起始偏移。
        /// </summary>
        [ThreadStatic]
        internal static ushort StartOffset1;

        /// <summary>
        /// 在 epoch 表中预留条目的备用起始偏移（当 <see cref="StartOffset1"/> 槽位已被占用时，用此偏移减少探测）。
        /// </summary>
        [ThreadStatic]
        internal static ushort StartOffset2;

        /// <summary>
        /// 当前线程在 epoch 表中的条目索引。
        /// </summary>
        [ThreadStatic]
        internal static int ThreadEntryIndex;

        /// <summary>
        /// 使用该条目的实例数。
        /// </summary>
        [ThreadStatic]
        internal static int ThreadEntryIndexCount;

#if DEBUG
        /// <summary>
        /// Acquire 时的托管线程 ID——跨线程 Suspend 绊线基准（await 后换线程释放 = 协议违反）。
        /// </summary>
        [ThreadStatic]
        internal static int ResumeThreadId;
#endif
    }

    /// <summary>
    /// 缓存行大小（字节）。
    /// </summary>
    private const int kCacheLineBytes = 64;

#if DEBUG
    // ═══ 常设 Debug 仪器（Release 零开销）═══
    // ★ 协议违反绊线 + 值示波器。教训（AsyncPriorityQueue 挂死事故）：
    //   队列节点重复回收 → 生产者卡死在 Search 持 epoch 不退出 → 消费者 BumpCurrentEpoch 槽满自旋。
    //   挂死期间 Release 构建零检测，只能靠 hang dump 逐线程考古。此后 LightEpoch 协议违反
    //   （未配对 Resume/Suspend、跨线程 Suspend、重入、嵌套 bump、Dispose 持保护）在 Debug 构建
    //   一律立即抛异常，异常消息自动携带最近协议操作历史（示波器），栈信息由异常自身携带。
    private readonly (string op, int tid, int entry, long entryEpoch, long curEpoch, int drainCount, int depth)[]
        _ops = new (string, int, int, long, long, int, int)[32];

    private int _opsIdx;

    private void TraceOp(string op)
    {
        var i = Interlocked.Increment(ref _opsIdx) - 1;
        var entry = Metadata.ThreadEntryIndex;
        var entryEpoch = entry == kInvalidIndex ? 0 : (*(_tableAligned + entry)).localCurrentEpoch;
        _ops[i % _ops.Length] = (op, Environment.CurrentManagedThreadId, entry,
            entryEpoch, Volatile.Read(ref _currentEpoch), Volatile.Read(ref _drainCount), _tBumpDepth);
    }

    private string OpsDump()
    {
        var sb = new System.Text.StringBuilder();
        var end = Volatile.Read(ref _opsIdx);
        var start = Math.Max(0, end - _ops.Length);
        for (var i = start; i < end; i++)
        {
            var e = _ops[i % _ops.Length];
            sb.Append($"\n  [{i}] {e.op} T{e.tid} entry={e.entry} lec={e.entryEpoch} cur={e.curEpoch} drain={e.drainCount} depth={e.depth}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Debug 构建强制失败：协议违反立即抛异常，携带线程/entry/epoch 状态 + 协议操作历史（示波器）。
    /// 栈信息由异常 StackTrace 携带——完全可重现、可定位。Release 构建本方法不存在（零开销）。
    /// </summary>
    [DoesNotReturn]
    private void ThrowProtocolViolation(string detail, string hint)
        => throw new InvalidOperationException(
            $"[LightEpoch] 协议违反：{detail}（线程={Environment.CurrentManagedThreadId}，" +
            $"entry={Metadata.ThreadEntryIndex}，cur={Volatile.Read(ref _currentEpoch)}，" +
            $"drain={Volatile.Read(ref _drainCount)}，depth={_tBumpDepth}）。{hint}" +
            $"\n── 最近协议操作历史（示波器）──{OpsDump()}");
#endif

    /// <summary>
    /// 默认的无效索引条目。
    /// </summary>
    private const int kInvalidIndex = 0;

    /// <summary>
    /// 条目表的默认条目数。
    /// </summary>
    private static readonly ushort KTableSize = Math.Max((ushort)128, (ushort)(Environment.ProcessorCount * 2));

    /// <summary>
    /// 默认 drain 列表大小。
    /// </summary>
    private const int kDrainListSize = 16;

    // ★ pinned 数组必须持有强引用——GC.AllocateArray(pinned:true) 只保证不移动（POH），
    //   不保证不回收。若仅存裸指针（_tableAligned/ThreadIndexAligned）而丢弃数组引用，
    //   GC 会回收数组 → 悬挂指针 → ReserveEntry AccessViolation（随 GC 时机随机触发）。
    private readonly Entry[] _tableRaw;

    private readonly Entry* _tableAligned;

    private static readonly Entry[] ThreadIndexRaw;

    private static readonly Entry* ThreadIndexAligned;

    /// <summary>
    /// （action, epoch）对列表——当某个 epoch 变为可安全回收时执行对应 action。
    /// 标 volatile，确保最后挂起的线程能看到最新值。
    /// </summary>
    private volatile int _drainCount;

    private readonly EpochActionPair[] _drainList = new EpochActionPair[kDrainListSize];

    /// <summary>
    /// 全局当前 epoch 值。
    /// </summary>
    private long _currentEpoch;

    /// <summary>
    /// 缓存的「最近一个可安全回收的 epoch」值。
    /// </summary>
    private long _safeToReclaimEpoch;

    /// <summary>
    /// 静态构造：设置共享的缓存行对齐空间，
    /// 用于存放每个条目被多少实例使用的计数。
    /// </summary>
    static LightEpoch()
    {
        // 多分配以做缓存行对齐
        ThreadIndexRaw = GC.AllocateArray<Entry>(KTableSize + 2, pinned: true);
        var p = (long)Unsafe.AsPointer(ref ThreadIndexRaw[0]);
        // 强制指针对齐到 64 字节边界
        var p2 = (p + (kCacheLineBytes - 1)) & ~(kCacheLineBytes - 1);
        ThreadIndexAligned = (Entry*)p2;
    }

    /// <summary>
    /// 实例化 epoch 表。
    /// </summary>
    public LightEpoch()
    {
        _tableRaw = GC.AllocateArray<Entry>(KTableSize + 2, pinned: true);
        var p = (long)Unsafe.AsPointer(ref _tableRaw[0]);
        // 强制指针对齐到 64 字节边界
        var p2 = (p + (kCacheLineBytes - 1)) & ~(kCacheLineBytes - 1);
        _tableAligned = (Entry*)p2;

        _currentEpoch = 1;
        _safeToReclaimEpoch = 0;

        // 将所有 epoch 表条目标记为「可用」
        for (var i = 0; i < kDrainListSize; i++)
            _drainList[i].Epoch = long.MaxValue;
        _drainCount = 0;
    }

    /// <summary>
    /// 清理 epoch 表。
    /// </summary>
    public void Dispose()
    {
#if DEBUG
        // ★ 忘记 Suspend 绊线：Dispose 时仍有线程持本实例保护 = 保护区泄漏——该线程的槽位永不归还，
        //   后续所有 BumpCurrentEpoch 永远等不到它退出（全局 drain 阻塞，AsyncPriorityQueue 挂死事故同型）。
        var held = new List<(int entry, long lec)>();
        for (var i = 1; i <= KTableSize; i++)
        {
            var lec = (*(_tableAligned + i)).localCurrentEpoch;
            if (lec != 0) held.Add((i, lec));
        }
        if (held.Count > 0)
            ThrowProtocolViolation(
                $"Dispose 时仍有 {held.Count} 个线程持保护未退出（{string.Join("，", held.Select(h => $"entry {h.entry} lec={h.lec}"))}）",
                "每个 Resume 必须配对同线程 Suspend；保护区严禁跨 await。");
#endif
        _currentEpoch = 1;
        _safeToReclaimEpoch = 0;
    }

    /// <summary>
    /// 检查当前 epoch 实例是否在本线程处于受保护状态。
    /// </summary>
    /// <returns>检查结果。</returns>
    public bool ThisInstanceProtected()
    {
        var entry = Metadata.ThreadEntryIndex;
        if (kInvalidIndex == entry) return false;
        return (*(_tableAligned + entry)).threadId == entry;
    }

    /// <summary>
    /// 当前全局 epoch 值（单调递增）。供回收调度方给 pending 对象打 epoch 标签：
    /// 对象在「标签 epoch」之前被物理摘除，其回收必须等标签 epoch 静默（见 <see cref="SafeToReclaimEpoch"/>）。
    /// </summary>
    public long CurrentEpoch => Volatile.Read(ref _currentEpoch);

    /// <summary>
    /// 最近一个可安全回收的 epoch——所有注册线程的保护区 epoch 均大于此值时成立。
    /// <b>仅在 drain action 回调栈内读取才有意义</b>：<see cref="Drain"/> 执行 action 前
    /// 已按当前线程表计算（<see cref="ComputeNewSafeToReclaimEpoch"/>），保护区外读取无同步保证。
    /// </summary>
    public long SafeToReclaimEpoch => Volatile.Read(ref _safeToReclaimEpoch);

    /// <summary>
    /// 将当前线程纳入受保护代码区。
    /// </summary>
    /// <returns>当前 epoch。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ProtectAndDrain()
    {
        var entry = Metadata.ThreadEntryIndex;

#if DEBUG
        // ★ 未 Acquire 绊线：ProtectAndDrain 前必须先 Resume（否则写 entry0 保留槽 = 静默腐败，
        //   文档反模式 3 的加固建议从 Debug.Assert 升级为立即抛异常）。
        if (entry == kInvalidIndex)
            ThrowProtocolViolation(
                "ProtectAndDrain 前未 Resume——ThreadEntryIndex 为 0（保留 entry0 将被写入，epoch 表腐败）",
                "调用线程必须先 Resume()（= Acquire + ProtectAndDrain），用完 Suspend()。");
#endif

        // 在非静态 epoch 表中登记 CurrentEpoch，从而保护它——这样 ComputeNewSafeToReclaimEpoch() 才能看到。
        (*(_tableAligned + entry)).threadId = Metadata.ThreadEntryIndex;
        (*(_tableAligned + entry)).localCurrentEpoch = _currentEpoch;

        if (_drainCount > 0)
        {
            // ★ 泄漏护栏：drain action 违约抛异常时（契约：纯内存、无异常），必须先 Release 本条目
            //   再重抛——否则调用方 Resume 抛异常、其 Suspend 永不执行，条目残留旧 epoch，
            //   safe 永久停摆 → 全局 drain 阻塞（AsyncPriorityQueueV2 压力楔死事故根因）。
            try { Drain((*(_tableAligned + entry)).localCurrentEpoch); }
            catch { Release(); throw; }
        }
#if DEBUG
        TraceOp("ProtectAndDrain");
#endif
    }

    /// <summary>
    /// 线程挂起其 epoch 条目。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Suspend()
    {
        Release();
        if (_drainCount > 0) SuspendDrain();
    }

    /// <summary>
    /// 线程恢复其 epoch 条目。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Resume()
    {
        Acquire();
        ProtectAndDrain();
    }

    /// <summary>
    /// 递增全局当前 epoch。
    /// </summary>
    /// <returns>递增后的新 epoch。</returns>
    private long BumpCurrentEpoch()
    {
#if DEBUG
        // ★ 未保护绊线：BumpCurrentEpoch 必须在保护区调用（未保护 → ThisInstanceProtected=false →
        //   本线程不被计入 safe epoch 计算，且自身也可能在 bump 期间被回收——协议破坏）。
        if (!ThisInstanceProtected())
            ThrowProtocolViolation(
                "BumpCurrentEpoch 未在保护区调用——ThisInstanceProtected=false",
                "BumpCurrentEpoch 的调用线程必须先 Resume()，用完 Suspend()。");
#endif
        var nextEpoch = Interlocked.Increment(ref _currentEpoch);

        if (_drainCount > 0)
            Drain(nextEpoch);

#if DEBUG
        TraceOp("Bump");
#endif
        return nextEpoch;
    }

    // ── 嵌套 bump 检测 ──
    // 嵌套 BumpCurrentEpoch（外层 bump 回调内又调 device.TruncateUntilSegmentAsync 二次 bump）有自死锁风险：
    // 外层 bump 已把当前线程计入「正在 drain 的旧 epoch 持有者」，内层 bump 又要等所有持有者退出（含自己）。
    // per-thread 深度计数不碰手搓指针表性能；正常路径一次 ThreadStatic 读 + 自增，可忽略。
    [ThreadStatic] private static int _tBumpDepth;

    /// <summary>当前线程正在 bump 的实例（嵌套判定按实例——跨实例嵌套（如引擎 epoch 的 drain action
    /// 内调 Core fs 内部另一 epoch 实例的 bump）无自死锁风险，放行；同实例嵌套才违规）。</summary>
    [ThreadStatic] private static LightEpoch? t_bumping;

    /// <summary>
    /// 当前线程是否正处于某个 BumpCurrentEpoch 的回调栈内。
    /// 供 drain action 判断自身是被 bump 内联触发（禁止再 bump/驱动嵌套过渡），
    /// 还是被 SuspendDrain/Resume 等非 bump 路径触发（可安全推进）。
    /// </summary>
    internal bool IsInsideBump() => _tBumpDepth > 0;

    /// <summary>
    /// 生产环境累计嵌套 bump 次数（可观测指标）。正常应为 0；非 0 提示潜在 epoch 死锁。
    /// </summary>
    public static long NestedBumpCount => Interlocked.CompareExchange(ref _nestedBumpCount, 0, 0);
    private static long _nestedBumpCount;

    /// <summary>
    /// 递增当前 epoch，并把触发 action 关联到前一个 epoch。
    /// </summary>
    /// <param name="onDrain">触发 action（前一个 epoch 安全回收时执行）。</param>
    public void BumpCurrentEpoch(Action onDrain)
    {
        // ★ 嵌套检测（按实例）：同实例嵌套（外层 bump 回调内二次 bump 同一表）有自死锁/重入风险；
        //   跨实例嵌套（drain action 触达另一独立 epoch 实例——引擎 drain 内做 Core fs 变异属常态）
        //   表相互独立，无死锁——放行并压栈，退出时恢复。
        var prevBumping = t_bumping;
        if (ReferenceEquals(t_bumping, this))
        {
            Interlocked.Increment(ref _nestedBumpCount);
#if DEBUG
            // ★ 立即回滚（Throw 路径不经过下方 finally）——防残留：线程池复用线程不重置
            //   ThreadStatic，残留会让后续无辜代码在入口被判"嵌套"（实测：绊线炸到
            //   其它测试的同线程调用）。同实例嵌套 Debug 构建强制失败。
            ThrowProtocolViolation(
                $"嵌套 BumpCurrentEpoch（同实例 depth={_tBumpDepth + 1}）——外层 bump 回调内二次 bump 同一表有自死锁风险",
                "drain action 内禁止再调本实例 BumpCurrentEpoch/驱动嵌套过渡（EPVS 自动链接已移出 bump 栈）。");
#else
            // Release：不中断，记录指标 + 警告，便于压测/线上排查（spec 16 §3.2.3）。
            Debug.WriteLine($"[LightEpoch] Nested BumpCurrentEpoch detected (depth={_tBumpDepth})");
#endif
        }
        t_bumping = this;
        ++_tBumpDepth;
        try
        {
            var priorEpoch = BumpCurrentEpoch() - 1;

            var i = 0;
            while (true)
            {
                if (_drainList[i].Epoch == long.MaxValue)
                {
                    // 这原本是空槽位。若仍是空，则把本 action/epoch 赋给该槽位。
                    if (Interlocked.CompareExchange(ref _drainList[i].Epoch, long.MaxValue - 1, long.MaxValue) == long.MaxValue)
                    {
                        _drainList[i].Action = onDrain;
                        _drainList[i].Epoch = priorEpoch;
                        Interlocked.Increment(ref _drainCount);
                        break;
                    }
                }
                else
                {
                    var triggerEpoch = _drainList[i].Epoch;

                    if (triggerEpoch <= _safeToReclaimEpoch)
                    {
                        // 这原本是「epoch 已安全回收」的槽位。若仍是，则执行其触发 action，再把本 action/epoch 赋给该槽位。
                        if (Interlocked.CompareExchange(ref _drainList[i].Epoch, long.MaxValue - 1, triggerEpoch) == triggerEpoch)
                        {
                            var triggerAction = _drainList[i].Action;
                            _drainList[i].Action = onDrain;
                            _drainList[i].Epoch = priorEpoch;
                            triggerAction!();
                            break;
                        }
                    }
                }

                if (++i != kDrainListSize) continue;
                // 已遍历到 drain 列表末尾仍未找到空槽或可回收槽。调 ProtectAndDrain，应能清出一个或多个槽位。
                ProtectAndDrain();
                i = 0;
                Thread.Yield();
            }

            // 现在 ProtectAndDrain 可能会执行我们刚加入的 action。
            ProtectAndDrain();
        }  // end try (嵌套检测)
        finally
        {
            --_tBumpDepth;
            t_bumping = prevBumping;
        }
    }

    /// <summary>
    /// 协作 drain 便捷封装（下沉 Core，语义 = StorageEngine 旧 IEngineEpoch.DrainThen）：
    /// <see cref="Resume"/>（本线程计入 epoch 表）→ <see cref="BumpCurrentEpoch(Action)"/> → 条件 <see cref="Suspend"/>。
    /// <para>★ 在调用方（mutator）线程上协作 drain——无并发 reader 时 <paramref name="safeAction"/> 在
    ///   <c>BumpCurrentEpoch</c> 内部同步触发；有 reader 时延迟到其退出 epoch（<c>Suspend→SuspendDrain</c>）
    ///   协作触发。<b>调用方不阻塞等待</b>——FASTER 原生模型，无专用 worker、无 drained.Wait、无死锁。</para>
    /// <para>⚠️ <paramref name="safeAction"/> 必须轻量、非阻塞（可能延迟到任意 reader 线程触发）；
    ///   重活应注册仅「投递」的轻量 action，再在专用 worker 上执行。</para>
    /// </summary>
    /// <param name="safeAction">reader 全部退出旧 epoch 后才执行的动作（必须轻量、非阻塞）。</param>
    public void DrainThen(Action safeAction)
    {
        ArgumentNullException.ThrowIfNull(safeAction);
        Resume();
        try
        {
            BumpCurrentEpoch(safeAction);
        }
        finally
        {
            // ★ action 失败时 BumpCurrentEpoch 的幂等清理可能已 Suspend 本线程——无条件二次
            //   Suspend 会以协议违反掩盖原始异常。仅在线程仍持本实例保护时收尾（真异常优先浮出）。
            if (ThisInstanceProtected())
                Suspend();
        }
    }

    /// <summary>
    /// 供线程标记某项活动已由本线程完成到某个版本的机制。
    /// </summary>
    /// <param name="markerIdx">活动 ID。</param>
    /// <param name="version">版本。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Mark(int markerIdx, long version)
    {
        Debug.Assert(markerIdx < 6);
        (*(_tableAligned + Metadata.ThreadEntryIndex)).markers[markerIdx] = version;
    }

    /// <summary>
    /// 检查所有活跃线程是否都已将某项活动完成到给定版本。
    /// </summary>
    /// <param name="markerIdx">活动 ID。</param>
    /// <param name="version">版本。</param>
    /// <returns>是否全部完成。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CheckIsComplete(int markerIdx, long version)
    {
        Debug.Assert(markerIdx < 6);

        // 检查是否所有线程都已上报完成
        for (var index = 1; index <= KTableSize; ++index)
        {
            var entryEpoch = (*(_tableAligned + index)).localCurrentEpoch;
            var fcVersion = (*(_tableAligned + index)).markers[markerIdx];
            if (0 == entryEpoch) continue;
            if ((fcVersion != version) && (entryEpoch < long.MaxValue))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 遍历所有线程，返回最近的安全 epoch。
    /// </summary>
    /// <param name="currentEpoch">当前 epoch。</param>
    /// <returns>安全 epoch。</returns>
    private long ComputeNewSafeToReclaimEpoch(long currentEpoch)
    {
        var oldestOngoingCall = currentEpoch;

        for (var index = 1; index <= KTableSize; ++index)
        {
            var entryEpoch = (*(_tableAligned + index)).localCurrentEpoch;
            if (0 == entryEpoch) continue;
            if (entryEpoch < oldestOngoingCall)
            {
                oldestOngoingCall = entryEpoch;
            }
        }

        // 最近的安全 epoch = 最早的不安全 epoch 的前一个。
        _safeToReclaimEpoch = oldestOngoingCall - 1;
        return _safeToReclaimEpoch;
    }

    /// <summary>
    /// epoch 挂起后处理待执行的 drain。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SuspendDrain()
    {
        while (_drainCount > 0)
        {
            // 内存屏障确保看到最新的 epoch 表条目，从而保证最后挂起的线程 drain 掉所有待执行 action。
            Thread.MemoryBarrier();
            for (var index = 1; index <= KTableSize; ++index)
            {
                var entryEpoch = (*(_tableAligned + index)).localCurrentEpoch;
                if (0 != entryEpoch)
                {
                    return;
                }
            }
            Resume();
            Release();
        }
    }

    /// <summary>
    /// 检查并执行已就绪的触发 action。
    /// </summary>
    /// <param name="nextEpoch">下一个 epoch。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Drain(long nextEpoch)
    {
        ComputeNewSafeToReclaimEpoch(nextEpoch);

        for (var i = 0; i < kDrainListSize; i++)
        {
            var triggerEpoch = _drainList[i].Epoch;

            if (triggerEpoch > _safeToReclaimEpoch) continue;
            if (Interlocked.CompareExchange(ref _drainList[i].Epoch, long.MaxValue - 1, triggerEpoch) !=
                triggerEpoch) continue;
            // 取出触发 action，然后把 epoch 置为 long.MaxValue 标记该槽位为「可用」。
            var triggerAction = _drainList[i].Action;
            _drainList[i].Action = null;
            _drainList[i].Epoch = long.MaxValue;
            Interlocked.Decrement(ref _drainCount);

            // 执行 action
#if DEBUG
            try
            {
                TraceOp("Drain-Action");
                triggerAction!();
            }
            catch (Exception ex)
            {
                // ★ drain action 由任意触发 drain 的协作者线程顺手执行——异常会破坏该线程。
                //   Debug 构建重抛并携带上下文（action 已从列表摘除，不会重复执行；剩余已就绪
                //   action 留待下次 Drain）。栈信息 + 示波器历史全在异常里。
                throw new InvalidOperationException(
                    $"[LightEpoch] drain action 抛异常（triggerEpoch={triggerEpoch}，" +
                    $"线程={Environment.CurrentManagedThreadId}）——drain action 必须纯内存、无异常。" +
                    $"\n── 最近协议操作历史（示波器）──{OpsDump()}", ex);
            }
#else
            triggerAction!();
#endif
            if (_drainCount == 0) break;
        }
    }

    /// <summary>
    /// 线程获取其 epoch 条目。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Acquire()
    {
        if (Metadata.ThreadEntryIndex == kInvalidIndex)
            Metadata.ThreadEntryIndex = ReserveEntryForThread();

#if DEBUG
        // ★ 重入绊线：同实例嵌套保护 = 协议违反（保护区未退出又 Resume；或 await 跨保护区后同线程重入）。
        if ((*(_tableAligned + Metadata.ThreadEntryIndex)).localCurrentEpoch != 0)
            ThrowProtocolViolation(
                $"Acquire 重入——线程已持本实例保护又 Resume（entry {Metadata.ThreadEntryIndex} 的 localCurrentEpoch 非 0）",
                "不要在保护区内重入；若用 Task 续延，须 RunContinuationsAsynchronously。");
        Metadata.ResumeThreadId = Environment.CurrentManagedThreadId;   // 跨线程 Suspend 检测基准
#endif

        // 此处对应 AnyInstanceProtected()。我们直到 ProtectAndDrain() 才标记 ThisInstanceProtected。
        Metadata.ThreadEntryIndexCount++;
#if DEBUG
        TraceOp("Acquire");
#endif
    }

    /// <summary>
    /// 线程释放其 epoch 条目。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Release()
    {
        var entry = Metadata.ThreadEntryIndex;

#if DEBUG
        // ★ 未配对绊线：未 Resume 就 Suspend（含跨线程 Suspend 的第一种表现——本线程根本没有 entry）。
        if (entry == kInvalidIndex || (*(_tableAligned + entry)).localCurrentEpoch == 0)
            ThrowProtocolViolation(
                $"Release 未配对——线程未持本实例保护就 Suspend（entry={entry}，localCurrentEpoch={(entry == kInvalidIndex ? 0 : (*(_tableAligned + entry)).localCurrentEpoch)}）",
                "Suspend 必须与同一实例的 Resume 配对；保护区严禁跨 await（thread-static 语义）。");
        // ★ 跨线程绊线：Resume 于线程 A、Suspend 于线程 B = 协议违反（await 后换线程释放 → 写错 entry 腐败）。
        if (Metadata.ResumeThreadId != Environment.CurrentManagedThreadId)
            ThrowProtocolViolation(
                $"跨线程 Suspend——Resume 于线程 {Metadata.ResumeThreadId}，Suspend 于线程 {Environment.CurrentManagedThreadId}",
                "保护区不可跨线程（thread-static）。若用 Task 续延，须 RunContinuationsAsynchronously 保证续延不与保护区重叠。");
#endif

        // 清除「ThisInstanceProtected()」（非静态 epoch 表）
        (*(_tableAligned + entry)).localCurrentEpoch = 0;
        (*(_tableAligned + entry)).threadId = 0;

        // 递减「AnyInstanceProtected()」（静态线程表）
        Metadata.ThreadEntryIndexCount--;
        if (Metadata.ThreadEntryIndexCount != 0)
        {
#if DEBUG
            TraceOp("Release(nested)");
#endif
            return;
        }
        (ThreadIndexAligned + Metadata.ThreadEntryIndex)->threadId = 0;
        Metadata.ThreadEntryIndex = kInvalidIndex;
#if DEBUG
        TraceOp("Release");
#endif
    }

    /// <summary>
    /// 为线程预留条目。本方法依赖「任何线程的 ID 都不会是 0」这一事实。
    /// </summary>
    /// <returns>预留到的条目。</returns>
    private static int ReserveEntry()
    {
        while (true)
        {
            // 尝试获取条目
            if (0 == (ThreadIndexAligned + Metadata.StartOffset1)->threadId)
            {
                if (0 == Interlocked.CompareExchange(
                        ref (ThreadIndexAligned + Metadata.StartOffset1)->threadId,
                        Metadata.ThreadId, 0))
                    return Metadata.StartOffset1;
            }

            if (Metadata.StartOffset2 > 0)
            {
                // 尝试备用条目
                Metadata.StartOffset1 = Metadata.StartOffset2;
                Metadata.StartOffset2 = 0;
            }
            else Metadata.StartOffset1++; // 顺序探测下一个条目

            if (Metadata.StartOffset1 <= KTableSize) continue;
            Metadata.StartOffset1 -= KTableSize;
            Thread.Yield();
        }
    }

    /// <summary>
    /// 32 位 murmur3 实现。
    /// </summary>
    /// <param name="h">输入整数。</param>
    /// <returns>哈希值。</returns>
    private static int Murmur3(int h)
    {
        var a = (uint)h;
        a ^= a >> 16;
        a *= 0x85ebca6b;
        a ^= a >> 13;
        a *= 0xc2b2ae35;
        a ^= a >> 16;
        return (int)a;
    }

    /// <summary>
    /// 在 epoch 表中分配一个新条目。每个线程只调用一次。
    /// </summary>
    /// <returns>预留到的条目。</returns>
    private static int ReserveEntryForThread()
    {
        if (Metadata.ThreadId != 0) return ReserveEntry(); // 每线程只执行一次（性能优化）
        Metadata.ThreadId = Environment.CurrentManagedThreadId;
        var code = (uint)Murmur3(Metadata.ThreadId);
        Metadata.StartOffset1 = (ushort)(1 + (code % KTableSize));
        Metadata.StartOffset2 = (ushort)(1 + ((code >> 16) % KTableSize));
        return ReserveEntry();
    }

    /// <summary>
    /// epoch 表条目（缓存行大小）。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = kCacheLineBytes)]
    private struct Entry
    {
        /// <summary>
        /// 线程本地的 epoch 值。
        /// </summary>
        [FieldOffset(0)]
        public long localCurrentEpoch;

        /// <summary>
        /// 与本条目关联的线程 ID。
        /// </summary>
        [FieldOffset(8)]
        public int threadId;

        [FieldOffset(12)]
        public int reentrant;

        [FieldOffset(16)]
        public fixed long markers[6];

        public override string ToString() => $"lce = {localCurrentEpoch}, tid = {threadId}, re-ent {reentrant}";
    }

    /// <summary>
    /// epoch 和 action 的配对，当 epoch 安全回收时执行 action。
    /// </summary>
    private struct EpochActionPair
    {
        /// <summary>
        /// epoch 值。
        /// </summary>
        public long Epoch;
        /// <summary>
        /// epoch 安全回收时执行的 action。
        /// </summary>
        public Action? Action;

        public override string ToString() => $"epoch = {Epoch}, action = {(Action is null ? "n/a" : Action.Method.ToString())}";
    }
}