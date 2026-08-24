using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 写偏向自旋读写锁——单 64 位字 CAS，读共享 / 写排他，等待中的写者挡住新读者（写不饿死）。
/// <para>★ 64-bit 位布局：bit63=写持有，bit62=写等待（pending），bits61..32=读计数，bits31..0 保留。</para>
/// <para>★ <b>写偏向协议</b>：写者进门即置 pending，读者 fast path 检查 Writer|Pending 任一置位即退避——
///   写者最多等「在途读者」退出，不被持续读者流饿死。读者 fast path 仍是一次 CAS。</para>
/// <para>★ 临界区约束：排他临界区必须短（纯内存、禁 await——Debug 释放线程校验会抓）；共享可长持/跨 await
///   （计数语义无线程亲和，读计划锁跨 IO 持有是合法用法）。跨 await 持有越久，写者与被挡读者等待越久。</para>
/// <para>★ 演进自 LockWord（终态替代，读优先→写偏向；Monitor Wait/PulseAll 职责删除——生产零调用）。</para>
/// </summary>
public sealed class SpinRWLock
{
    /// <summary>
    /// 锁存储——128B 显式布局：热锁字独占首缓存行，Debug 仪器隔离到第二缓存行（不污染热路径）。
    /// <para>★ WriterThreadId 放 offset 8 与 Value 同行是有意的：只在持写锁期间读写，该行本已脏，无额外乒乓。</para>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct LockStorage
    {
        /// <summary>热锁状态，独占 64B 缓存行（尽力隔离——托管对象 8B 对齐不保证行边界，胜于紧邻布局）。</summary>
        [FieldOffset(0)]
        public long Value;

        /// <summary>排他持有者线程 ID（Debug 释放线程校验；0=未持有）。与 Value 同行——持写锁时该行已脏。</summary>
        [FieldOffset(8)]
        public int WriterThreadId;

#if DEBUG
        /// <summary>Debug 仪器放到第二缓存行，不污染热路径。</summary>
        [FieldOffset(64)]
        public int OpsIdx;

        [FieldOffset(72)]
        public (string op, int tid, long before, long after)[] Ops;
#endif
    }

    private LockStorage _storage;

    // ═══ 锁模式常量 ═══
    private const long WriterMask = 1L << 63;       // bit63：写持有（独占）
    private const long PendingMask = 1L << 62;      // bit62：写等待（挡新读者——写偏向核心）
    private const long ReaderInc = 1L << 32;        // 读计数递增量
    private const long ReaderMask = 0x3FFF_FFFF_0000_0000;   // bits61..32：读计数（30 位）
    private const long ReaderMaxCount = (1L << 30) - 1;

#if DEBUG
    // ★ 常设 Debug 仪器（Release 零开销）：最近 24 次锁字原子操作环形记录，绊线异常自动携带。
    //   教训（）：LockWord AcquireShared OR置位 bug 靠临时造的值示波器破案（CAS-R before==after
    //   铁证）——原语级 Debug 全套跟踪必须是常设设施，否则每次锁异常都要现场考古一下午。
    public SpinRWLock()
    {
        _storage.Ops = new (string, int, long, long)[24];
        _storage.OpsIdx = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TraceOp(string op, long before, long after)
    {
        var i = Interlocked.Increment(ref _storage.OpsIdx) - 1;
        _storage.Ops[i % _storage.Ops.Length] = (op, Environment.CurrentManagedThreadId, before, after);
    }

    private string OpsDump()
    {
        var sb = new System.Text.StringBuilder();
        var end = Volatile.Read(ref _storage.OpsIdx);
        var start = Math.Max(0, end - _storage.Ops.Length);
        for (var i = start; i < end; i++)
        {
            var e = _storage.Ops[i % _storage.Ops.Length];
            sb.AppendLine($"  [{i}] {e.op} T{e.tid}: 0x{e.before:X16} → 0x{e.after:X16}");
        }
        return sb.ToString();
    }
#else
    // Release：结构体字段有默认零初始化，无需构造器（省一个 ctor 调用）
#endif

    /// <summary>获取读锁（共享）——一次 CAS 递增读计数；写持有或写等待（pending）期间退避自旋。
    /// <para>★ 获取必须用 <c>s + ReaderInc</c>（递增），绝不能 <c>s | ReaderInc</c>（置位）——ReaderInc 是
    ///   固定单 bit，OR 语义下第 2..N 个并发读者"获取成功"却不加计数，而每个读者的释放都 −1 → 第二个
    ///   释放即读计数下溢 → 借位到高位出假"写者位" → 全部等待方永久自旋楔死（事故根因）。</para></summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AcquireShared()
    {
        var s = Volatile.Read(ref _storage.Value);
        if ((s & (WriterMask | PendingMask)) == 0)
        {
            var readerCnt = (ulong)(s & ReaderMask) >> 32;
            if (readerCnt < ReaderMaxCount)
            {
                var next = s + ReaderInc;
                if (Interlocked.CompareExchange(ref _storage.Value, next, s) == s)
                {
#if DEBUG
                    TraceOp("CAS-R", s, next);
#endif
                    return;
                }
            }
        }
        AcquireSharedSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AcquireSharedSlow()
    {
        var spinner = new SpinWait();
        while (true)
        {
            var s = Volatile.Read(ref _storage.Value);
            if ((s & (WriterMask | PendingMask)) == 0)
            {
                var readerCnt = (ulong)(s & ReaderMask) >> 32;
                if (readerCnt >= ReaderMaxCount)
                {
                    throw new InvalidOperationException("SpinRWLock reader count overflow, too many shared holders.");
                }

                var next = s + ReaderInc;
                if (Interlocked.CompareExchange(ref _storage.Value, next, s) == s)
                {
#if DEBUG
                    TraceOp("CAS-R-Slow", s, next);
#endif
                    return;
                }
            }

            spinner.SpinOnce();
            if (spinner.NextSpinWillYield)
                Thread.Yield();
        }
    }

    /// <summary>尝试获取读锁，不自旋，立刻返回（投机路径——拿不到不排队不阻碍）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAcquireShared()
    {
        var s = Volatile.Read(ref _storage.Value);
        if ((s & (WriterMask | PendingMask)) != 0)
            return false;

        var readerCnt = (ulong)(s & ReaderMask) >> 32;
        if (readerCnt >= ReaderMaxCount)
            return false;

        var next = s + ReaderInc;
        var ok = Interlocked.CompareExchange(ref _storage.Value, next, s) == s;
#if DEBUG
        if (ok) TraceOp("Try-R", s, next);
#endif
        return ok;
    }

    /// <summary>获取读锁——返回 using scope（自动释放）。简化 try/finally 模式。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SharedScope EnterShared() => new(this);

    /// <summary>释放读锁（共享）——CAS 循环实现，消除破坏性先修改再回滚窗口。
    /// <para>★ 下溢绊线（Debug+Release 永久保留，演进自 LockWord ）：读计数已为 0 = 存在无配对的
    ///   ReleaseShared（bug）——旧实现 Interlocked.Add 先减再检测再回滚，减完到回滚之间锁字短暂呈现
    ///   "假写者位"（0 - ReaderInc 借位到 bit63）阻塞全部等待方；现改为<b>修改前检测</b>（count==0 直接抛，
    ///   锁字不动）——无损伤窗口，即使异常被吞也不残留破坏态。</para>
    /// <para>★ 竞态语义：若误释放恰逢他方读者在途（count ≥ 1），本次会"偷"掉对方的计数——对方的释放
    ///   将在 count==0 处被绊线拦下（异常栈指到无配对释放链的下游）——检测可能延迟一步，不会漏。</para></summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseShared()
    {
        while (true)
        {
            var s = Volatile.Read(ref _storage.Value);
            if ((s & ReaderMask) == 0)
            {
#if DEBUG
                var history = $"\n最近操作历史:\n{OpsDump()}";
#else
                var history = string.Empty;
#endif
                var stack = Environment.StackTrace;
                Console.Error.WriteLine($"[SpinRWLock] 读计数下溢预判（value=0x{s:X16}）\n{stack}");
                throw new InvalidOperationException($"SpinRWLock 读计数下溢——无配对 ReleaseShared {history}\n{stack}");
            }

            var next = s - ReaderInc;
            if (Interlocked.CompareExchange(ref _storage.Value, next, s) == s)
            {
#if DEBUG
                TraceOp("Rel-R", s, next);
#endif
                return;
            }
        }
    }

    /// <summary>获取写锁（独占）——进门先置 pending 挡新读者，再等在途读者退出后 CAS 置写持有位。
    /// <para>★ 多写者：pending 位幂等；前一写者释放会连 pending 一并清除，仍在等的写者检测到丢失即重登记
    ///   （条件重登记——自旋循环内不再无条件 Interlocked.Or，减少共享行乒乓）。</para></summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AcquireExclusive()
    {
        // 先挂 pending 闸门——此后新读者退避，本写者最多等「当前在途读者」的临界区
        Interlocked.Or(ref _storage.Value, PendingMask);
#if DEBUG
        TraceOp("OR-Pend", 0, PendingMask);   // before 记 0：Or 前值不重要，after 语义为置位
#endif
        AcquireExclusiveSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AcquireExclusiveSlow()
    {
        var spinner = new SpinWait();
        while (true)
        {
            var s = Volatile.Read(ref _storage.Value);
            if ((s & (WriterMask | ReaderMask)) == 0)
            {
                var next = s | WriterMask;
                if (Interlocked.CompareExchange(ref _storage.Value, next, s) == s)
                {
#if DEBUG
                    TraceOp("CAS-W", s, next);
#endif
                    _storage.WriterThreadId = Environment.CurrentManagedThreadId;
                    return;
                }
            }

            // 仅 pending 丢失时重登记闸门，消除循环内无条件 Interlocked.Or
            if ((Volatile.Read(ref _storage.Value) & PendingMask) == 0)
            {
                Interlocked.Or(ref _storage.Value, PendingMask);
#if DEBUG
                TraceOp("OR-Pend-Re-arm", 0, PendingMask);
#endif
            }

            spinner.SpinOnce();
            if (spinner.NextSpinWillYield)
                Thread.Yield();
        }
    }

    /// <summary>尝试获取写锁，不自旋、不设置 pending 排队闸门，拿到即返回 true。
    /// <para>★ 投机语义：失败不登记 pending（不给读者上闸）——适合"拿到就赚、拿不到走别路"的路径；
    ///   需要写偏向保证的排他转换必须用 <see cref="AcquireExclusive"/>。</para></summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAcquireExclusive()
    {
        var s = Volatile.Read(ref _storage.Value);
        if ((s & (WriterMask | ReaderMask)) != 0)
            return false;

        var next = s | WriterMask;
        var ok = Interlocked.CompareExchange(ref _storage.Value, next, s) == s;
        if (ok)
        {
            _storage.WriterThreadId = Environment.CurrentManagedThreadId;
#if DEBUG
            TraceOp("Try-W", s, next);
#endif
        }
        return ok;
    }

    /// <summary>获取写锁——返回 using scope（自动释放）。简化 try/finally 模式。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExclusiveScope EnterExclusive() => new(this);

    /// <summary>释放写锁（独占）——原子清除写持有 + pending（多写者下由仍在等的写者自行重登记）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseExclusive()
    {
        // Debug 检测：释放线程必须 == 获取线程——排他临界区内 await 会导致线程切换，
        // 错线程释放 = 锁泄漏（"排他临界区内 await"反模式的运行时捕获）。
        Debug.Assert(_storage.WriterThreadId == Environment.CurrentManagedThreadId,
            "SpinRWLock 排他锁释放线程 != 获取线程——临界区内可能发生了线程切换（await?）");

        _storage.WriterThreadId = 0;
#if DEBUG
        var before = Volatile.Read(ref _storage.Value);
#endif
        Interlocked.And(ref _storage.Value, ~(WriterMask | PendingMask));
#if DEBUG
        TraceOp("Rel-W", before, Volatile.Read(ref _storage.Value));
#endif
    }

    // ═══ 诊断属性（仅调试/指标，业务逻辑禁止依赖——瞬时值，读出即可能过期）═══

    /// <summary>是否被排他持有（诊断用）。</summary>
    public bool IsHeldExclusive => (Volatile.Read(ref _storage.Value) & WriterMask) != 0;

    /// <summary>当前共享持有计数（诊断用）。</summary>
    public int ReaderCount
    {
        get
        {
            var v = Volatile.Read(ref _storage.Value);
            return (int)((v & ReaderMask) >> 32);
        }
    }

    // ═══ using scope——ref struct，构造时加锁，Dispose 时释放 ═══

    /// <summary>排他锁 scope——using 自动释放。构造时 AcquireExclusive，Dispose 时 ReleaseExclusive。</summary>
    public ref struct ExclusiveScope
    {
        private readonly SpinRWLock _lock;
        private bool _disposed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ExclusiveScope(SpinRWLock spinRwLock)
        {
            _lock = spinRwLock;
            _lock.AcquireExclusive();
            _disposed = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (_disposed) return;
            _lock.ReleaseExclusive();
            _disposed = true;
        }
    }

    /// <summary>共享锁 scope——using 自动释放。构造时 AcquireShared，Dispose 时 ReleaseShared。</summary>
    public ref struct SharedScope
    {
        private readonly SpinRWLock _lock;
        private bool _disposed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SharedScope(SpinRWLock spinRwLock)
        {
            _lock = spinRwLock;
            _lock.AcquireShared();
            _disposed = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (_disposed) return;
            _lock.ReleaseShared();
            _disposed = true;
        }
    }
}
