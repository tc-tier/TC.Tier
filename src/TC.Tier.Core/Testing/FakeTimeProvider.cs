namespace TC.Tier.Core.Testing;

/// <summary>
/// 假时钟（故障注入面补全设计 件一——时钟缝测试替身，零外部依赖；不用 Microsoft.Extensions.TimeProvider.Testing 包）。
/// <para>★ 双钟模型：<b>单调钟</b>（<see cref="GetTimestamp"/>——快进推进、停走/倒流免疫、墙钟跳变不污染）
///   + <b>墙钟</b>（<see cref="GetUtcNow"/>——跳变/漂移可注入，建模真实 NTP 校时与走速偏差）。</para>
/// <para>★ 单调戳 ms 域（<see cref="TimestampFrequency"/> = 1000，与 <c>Environment.TickCount64</c> 同域）——
///   落点 deadline 算术零变更。</para>
/// <para>★ 快进：<see cref="Advance"/> 同步推进双钟并触发到期定时器（回调在 Advance 调用线程按到期序
///   同步执行——零真实睡等、确定性重现）；<c>Advance(TimeSpan.Zero)</c> = 冲刷已到期定时器。</para>
/// <para>★ 定时器：<see cref="CreateTimer"/> 返回的 <see cref="ITimer"/> 由假钟驱动——周期定时器在单次
///   大跨度 Advance 内按周期连发（对齐真实周期语义）。定时器回调抛出的异常原样冒泡到 Advance 调用方
///   （测试可见性优先，不静默吞）。</para>
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    /// <summary>缺省起始墙钟（固定——测试确定性；时间起点无关断言用相对量）。</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // 单调钟（100ns ticks 域内部计量；对外 ms 域）
    private long _monoTicks;
    // 墙钟（DateTime.Ticks 域，100ns）
    private long _wallTicks;
    // 墙钟走速（1 = 正常；>1 快走 <1 慢走——Advance 按走速缩放墙钟增量；单调钟恒定不受污染）
    private double _drift = 1;
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = new();

    /// <summary>构造（起始墙钟默认 <see cref="DefaultStart"/>，单调钟 0）。</summary>
    /// <param name="startWallClock">起始墙钟（可选）。</param>
    public FakeTimeProvider(DateTimeOffset? startWallClock = null)
    {
        _wallTicks = (startWallClock ?? DefaultStart).UtcTicks;
    }

    /// <summary>当前墙钟（跳变/漂移可见——TTL/租约消费点）。</summary>
    public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _wallTicks), TimeSpan.Zero);

    /// <summary>单调钟频率（ms 域——与 <c>Environment.TickCount64</c> 同域，deadline 算术零变更）。</summary>
    public override long TimestampFrequency => 1000;

    /// <summary>当前单调戳（ms 域——快进推进；墙钟跳变/漂移不影响）。</summary>
    public override long GetTimestamp() => Volatile.Read(ref _monoTicks) / 10_000;

    /// <summary>同步快进——单调钟 + 墙钟（按当前走速）推进 <paramref name="delta"/>，并按到期序触发
    /// 到期定时器（周期定时器按周期连发）。回调在调用线程同步执行。</summary>
    /// <param name="delta">推进量（可为零——冲刷已到期定时器；须非负）。</param>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        List<FakeTimer> toFire;
        lock (_gate)
        {
            _monoTicks += delta.Ticks;
            _wallTicks += (long)(delta.Ticks * _drift);
            toFire = CollectDueTimersLocked();
        }
        foreach (var timer in toFire) timer.Fire(); // 锁外回调——回调可重入假钟读数
    }

    /// <summary>墙钟跳变（建模 NTP 校时）——墙钟立即置为 <paramref name="utcNow"/>；单调钟不受污染。</summary>
    /// <param name="utcNow">新墙钟（UTC）。</param>
    public void SetWallClock(DateTimeOffset utcNow)
    {
        lock (_gate) _wallTicks = utcNow.UtcTicks;
    }

    /// <summary>墙钟走速（漂移注入）——后续 <see cref="Advance"/> 的墙钟增量按 <paramref name="rate"/>
    /// 缩放（1 = 正常；单调钟恒定不受影响）。</summary>
    /// <param name="rate">走速比（须 &gt; 0）。</param>
    public void SetDrift(double rate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rate, 0);
        lock (_gate) _drift = rate;
    }

    /// <inheritdoc/>
    /// <remarks>假钟驱动：<paramref name="dueTime"/> ≤ 0 的定时器即刻到期（下一次 Advance——含零跨度冲刷——触发）；
    /// <see cref="Timeout.InfiniteTimeSpan"/> = 永不触发。</remarks>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Rearm(dueTime, period);
        }
        return timer;
    }

    /// <summary>收集并推进全部到期定时器（按到期序，周期定时器连发；锁内调用）。</summary>
    private List<FakeTimer> CollectDueTimersLocked()
    {
        var fired = new List<FakeTimer>();
        while (true)
        {
            FakeTimer? best = null;
            long bestDue = long.MaxValue;
            foreach (var timer in _timers)
            {
                if (!timer.Enabled || timer.DueTicks > _monoTicks || timer.DueTicks >= bestDue) continue;
                best = timer;
                bestDue = timer.DueTicks;
            }
            if (best is null) break;
            fired.Add(best);
            if (best.PeriodTicks > 0) best.DueTicks += best.PeriodTicks; // 周期——重挂下一拍（仍到期则本轮连发）
            else best.Disarm();                                          // 单次——消耗
        }
        return fired;
    }

    /// <summary>移除已释放定时器（锁内调用）。</summary>
    private void RemoveTimer(FakeTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    /// <summary>假钟定时器——<see cref="ITimer"/> 的时钟缝实现（触发完全由 <see cref="FakeTimeProvider.Advance"/> 驱动）。</summary>
    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;
        private TimeSpan _due = Timeout.InfiniteTimeSpan;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        /// <summary>到期时刻（单调钟 100ns 域绝对值；停用 = long.MaxValue）。</summary>
        internal long DueTicks { get; set; } = long.MaxValue;

        /// <summary>周期（100ns；0 = 单次）。</summary>
        internal long PeriodTicks { get; private set; }

        internal bool Enabled => DueTicks != long.MaxValue;

        /// <summary>重挂（due/period 相对当前单调钟；锁内调用）。</summary>
        internal void Rearm(TimeSpan due, TimeSpan period)
        {
            _due = due;
            _period = period;
            PeriodTicks = period == Timeout.InfiniteTimeSpan ? 0 : Math.Max(0, period.Ticks);
            DueTicks = due == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : owner._monoTicks + Math.Max(0, due.Ticks);
        }

        internal void Disarm() => DueTicks = long.MaxValue;

        /// <summary>触发回调（Advance 锁外调用——异常原样冒泡，不静默吞）。</summary>
        internal void Fire() => callback(state);

        /// <inheritdoc/>
        /// <remarks>重设到期（相对当前时刻，对齐 ITimer 契约）——假钟下下次 Advance 生效。</remarks>
        public TimeSpan DueTime
        {
            get => _due;
            set
            {
                lock (owner._gate)
                    Rearm(value, _period);
            }
        }

        /// <inheritdoc/>
        /// <remarks>重设周期（到期点保持不变；未激活定时器只记值）。</remarks>
        public TimeSpan Period
        {
            get => _period;
            set
            {
                lock (owner._gate)
                {
                    if (Enabled)
                        Rearm(TimeSpan.FromTicks(Math.Max(0, DueTicks - owner._monoTicks)), value);
                    else
                        _period = value;
                }
            }
        }

        /// <inheritdoc/>
        /// <remarks>重设到期与周期（对齐 ITimer.Change 契约）——假钟下下次 Advance 生效。恒返回 true（重挂成功）。</remarks>
        /// <param name="dueTime">新到期（相对当前时刻；Timeout.InfiniteTimeSpan = 停用）。</param>
        /// <param name="period">新周期（Timeout.InfiniteTimeSpan = 单次）。</param>
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
                Rearm(dueTime, period);
            return true;
        }

        /// <summary>释放（解除挂载——幂等）。</summary>
        /// <returns>释放后完成。</returns>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>释放（解除挂载——幂等）。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Disarm();
            owner.RemoveTimer(this);
        }
    }
}
