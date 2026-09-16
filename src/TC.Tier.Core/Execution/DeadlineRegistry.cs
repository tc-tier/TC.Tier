using System.Collections.Concurrent;
using TC.Tier.Core.Logging;

namespace TC.Tier.Core.Execution;

/// <summary>
/// 时间驱动唤醒注册表（零 TimerQueue 依赖原语——拉取模型，设计稿
/// docs/design/tc-tier-net-timerqueue-free-tick-pacer-design.md）。
/// <para>★ 背景判例：.NET 8 TimerQueue 在高频触发窗口把 PeriodicTimer 从定时器链表弄丢
///   （堆转储实锤：TimerQueueTimer _next=0,_prev=0 孤儿）——tick 永不再来 → 共识循环冻结。
///   本原语用<b>专用线程 + WaitHandle.WaitOne 同步等待</b>（futex 实现）取代一切定时器依赖。</para>
/// <para>★ 拉取模型：条目不存 deadline 值——持 <c>Func&lt;long&gt;</c>（deadline 计算委托，
///   owner 侧读自己的 Interlocked 字段，TickCount64 单位）+ <c>Action</c>（唤醒回调）。节拍线程
///   每轮醒来重扫全部条目：到期者回调、未到期者取最早值睡眠。零更新竞态、无堆、无重排协议；
///   deadline 早移由封顶（<see cref="DefaultCapMilliseconds"/>=100ms）保证有界晚醒。</para>
/// <para>★ 分片：<see cref="DeadlineRegistry"/>——每 shard 独立条目表 + 独立
///   节拍线程（懒启：首个订阅才起、起后常驻）；订阅按 round-robin 稳定分配，条目终身不动
///   （句柄持 shard 引用——退订零查找）。扫描成本 = 条目数 × 唤醒率：单分片到 10⁴ 级无感，
///   万级 → shard=2、10⁶ 级 → 4（升级触发器见设计稿 §3.2）。</para>
/// <para>★ 生命周期：<see cref="IDisposable"/>/<see cref="IAsyncDisposable"/>——CAS 防双释放 +
///   有界等节拍线程退出（对齐 <see cref="BackgroundWorkerLoop"/> 样板）。<see cref="Shared"/>
///   进程级懒加载单例（IsolatedTaskScheduler.Shared 同款先例）。</para>
/// </summary>
public sealed class DeadlineRegistry : IDisposable, IAsyncDisposable
{
    /// <summary>唤醒封顶（ms——早移晚醒上界；空闲唤醒率 ≤10/s）。</summary>
    private const int DefaultCapMilliseconds = 100;

    /// <summary>进程级共享实例（懒加载单例——多数消费方共用一条节拍线程）。</summary>
    public static DeadlineRegistry Shared { get; } = new();

    private readonly Shard[] _shards;
    private long _nextShard;   // round-robin 分配游标（Interlocked）
    private int _disposed;
    private readonly TimeProvider _clock;

    /// <summary>构造。</summary>
    /// <param name="shardCount">分片数（默认 1——当前量级无需分片；每 shard 一个懒启节拍线程）。
    /// 万级条目 → 2、10⁶ 级 → 4（升级触发器见设计稿 §3.2）。</param>
    /// <param name="capMilliseconds">唤醒封顶（ms——早移晚醒上界）。须 &gt; 0。</param>
    /// <param name="logger">日志（可选）。</param>
    /// <param name="clock">时钟供给源（时钟缝 件一——缺省 <see cref="TimeProvider.System"/>；假钟注入时
    ///   与订阅方 deadline 同钟——<see cref="TimeProviderExtensions.GetMsTimestamp"/> 同域）。</param>
    /// <exception cref="ArgumentOutOfRangeException">shardCount &lt; 1 或封顶 ≤ 0。</exception>
    public DeadlineRegistry(int shardCount = 1, int capMilliseconds = DefaultCapMilliseconds, ILogger? logger = null,
        TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capMilliseconds, 1);
        _clock = clock ?? TimeProvider.System;
        _shards = new Shard[shardCount];
        for (var i = 0; i < shardCount; i++)
            _shards[i] = new Shard(i, capMilliseconds, logger, _clock);
    }

    /// <summary>条目总数（近似快照——诊断用）。</summary>
    public int EntryCount
    {
        get
        {
            var count = 0;
            foreach (var s in _shards) count += s.EntryCount;
            return count;
        }
    }

    /// <summary>注册表取证快照（每 shard：节拍线程是否存活 + 每条目 last-wake/due 状态——
    /// tick 链断类冻结的判别面：pacer 活但条目不 wake = 扫描/到期判定矛盾点）。</summary>
    /// <returns>各 shard 取证快照以 <c>" || "</c> 连接而成的诊断字符串。</returns>
    public string Describe()
    {
        var parts = new List<string>(_shards.Length);
        foreach (var s in _shards) parts.Add(s.Describe());
        return string.Join(" || ", parts);
    }

    /// <summary>
    /// 订阅唤醒（时间驱动——拉取模型）。
    /// <para>★ 契约：<paramref name="deadlineTicks"/> 返回本 owner 的下一次 deadline
    ///   （ms 单调域——注册表时钟的 <see cref="TimeProviderExtensions.GetMsTimestamp"/> 单位；读 owner
    ///   自己的 Interlocked 字段——本线程只读，零更新协议）；<paramref name="wake"/> = 快速非阻塞唤醒信号
    ///   （TryWrite 类——禁止重活，单条异常隔离不杀节拍线程）。到期判定 = <c>now ≥ deadlineTicks()</c>，
    ///   回调后 owner 自推进 deadline（固定周期形态）或由业务处理重置（raft 形态）。</para>
    /// </summary>
    /// <param name="deadlineTicks">deadline 计算委托（ms 单调域）。</param>
    /// <param name="wake">唤醒回调（快速非阻塞）。</param>
    /// <returns>订阅句柄（Dispose = 退订，幂等）。</returns>
    /// <exception cref="ObjectDisposedException">注册表已释放。</exception>
#pragma warning disable CA1859 // 公共 API 面刻意以 IDisposable 抽象（句柄实现细节不外泄——对齐 IDisposable 契约惯例）
    public IDisposable Subscribe(Func<long> deadlineTicks, Action wake)
#pragma warning restore CA1859
    {
        ArgumentNullException.ThrowIfNull(deadlineTicks);
        ArgumentNullException.ThrowIfNull(wake);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var shard = _shards[(int)((Interlocked.Increment(ref _nextShard) - 1) % _shards.Length)];
        return shard.Subscribe(deadlineTicks, wake);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var shard in _shards) shard.Dispose();
    }

    /// <inheritdoc/>
    /// <returns>任务在全部 shard 的节拍线程停止（或超时放弃等待）并释放取消源后完成。</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        var tasks = new Task[_shards.Length];
        for (var i = 0; i < _shards.Length; i++) tasks[i] = _shards[i].DisposeAsync().AsTask();
        return new ValueTask(Task.WhenAll(tasks));
    }

    // ═══ 分片（独立条目表 + 独立懒启节拍线程）═══

    private sealed class Shard : IDisposable, IAsyncDisposable
    {
        private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

        private readonly int _index;
        private readonly int _capMs;
        private readonly ILogger? _logger;
        internal readonly TimeProvider _clock;   // Entry（registry 级嵌套）取证读——跨嵌套类访问
        private readonly ConcurrentDictionary<Entry, byte> _entries = new();
        private readonly object _lifecycleLock = new();
        private CancellationTokenSource? _cts;
        private Task? _thread;
        private int _disposed;

        internal Shard(int index, int capMs, ILogger? logger, TimeProvider clock)
        {
            _index = index;
            _capMs = capMs;
            _logger = logger;
            _clock = clock;
        }

        internal int EntryCount => _entries.Count;

        private long _lastPacerLoopTicks;   // 节拍线程最近一次循环迭代完成时刻（取证——线程死亡/泊死判别）
        private long _lastWakeAnyTicks;     // 最近一次任一条目唤醒回调时刻（取证）

        internal string Describe()
        {
            var now = _clock.GetMsTimestamp();
            var pacerLag = _lastPacerLoopTicks == 0 ? -1 : now - Volatile.Read(ref _lastPacerLoopTicks);
            var wakeLag = _lastWakeAnyTicks == 0 ? -1 : now - Volatile.Read(ref _lastWakeAnyTicks);
            var entries = new List<string>(_entries.Count);
            foreach (var kv in _entries) entries.Add(kv.Key.Describe());
            return $"shard{_index}:pacerAlive={(_cts is { } c && !c.IsCancellationRequested)}/pacerLag={pacerLag}ms/wakeLag={wakeLag}ms/entries=[{string.Join("; ", entries)}]";
        }

        internal Entry Subscribe(Func<long> deadlineTicks, Action wake)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var entry = new Entry(deadlineTicks, wake, this);
            lock (_lifecycleLock)
            {
                // ★ 锁内二查（订阅/释放并发竞态封堵）：TryAdd + 懒启判断 + Dispose 的取消三者
                //   同锁串行——要么完整订阅（线程必起），要么 Dispose 后抛 ODE；不存在
                //   "订阅成功但线程永不起"的静默僵尸窗口
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (!_entries.TryAdd(entry, 0))
                {
                    entry.Dispose();
                    throw new InvalidOperationException("订阅条目撞键（不可能——Entry 引用键）。");
                }
                // ★ 懒启：本 shard 首个订阅才起节拍线程（起后常驻——泊车 10 醒/s µs 级，
                //   比懒停重启竞态便宜；进程级注册表本就长命）
                if (_cts is null)
                    StartThread();
            }
            return entry;
        }

        private void StartThread()
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _thread = Task.Factory.StartNew(
                () => PacerLoop(ct),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        internal void Remove(Entry entry)
        {
            _entries.TryRemove(entry, out _);
        }

        internal void LogWarn(Exception ex, string message)
            => _logger?.LogWarning(ex, message);

        internal void LogSlowWake(long elapsedMs)
            => _logger?.LogWarning("DeadlineRegistry 唤醒回调超 10ms（拖慢同 shard 消费者）：{Elapsed}ms shard={Shard}", elapsedMs, _index);

        internal void MarkWake()
            => Volatile.Write(ref _lastWakeAnyTicks, _clock.GetMsTimestamp());

        private void PacerLoop(CancellationToken ct)
        {
            try { Thread.CurrentThread.Name = $"deadline-registry-{_index}"; } catch (InvalidOperationException) { /* 不可设——忽略 */ }
            try
            {
                while (true)
                {
                    var now = _clock.GetMsTimestamp();
                    long earliest = long.MaxValue;
                    try
                    {
                        foreach (var kv in _entries)
                        {
                            if (ct.IsCancellationRequested) return;
                            long deadline;
                            try
                            {
                                deadline = kv.Key.DeadlineTicks();
                            }
                            catch (Exception ex)
                            {
                                // ★ deadline 计算异常隔离（不杀节拍线程）——该条目本轮到点不回调
                                LogWarn(ex, $"DeadlineRegistry deadline 计算异常（本轮到点跳过）：shard={_index}");
                                continue;
                            }
                            if (deadline <= now)
                            {
                                kv.Key.SafeWake();
                                continue;
                            }
                            if (deadline < earliest) earliest = deadline;
                        }
                    }
                    catch (Exception ex)
                    {
                        // ★ 扫描面异常兜底（2026-09-03 活性判例）：节拍线程是全进程时间驱动消费者的
                        //   唯一活性源——任何未预期异常都不得杀线程（静默死亡=全节点 tick 断供）。
                        //   本轮丢弃（earliest 保持 MaxValue → 封顶休眠后重扫）。
                        LogWarn(ex, $"DeadlineRegistry 扫描异常（隔离继续）：shard={_index}");
                    }
                    Volatile.Write(ref _lastPacerLoopTicks, _clock.GetMsTimestamp());   // 取证：循环迭代完成时刻

                    var sleepMs = earliest == long.MaxValue
                        ? _capMs                                  // 空表（退订竞态窗口）——低频休眠
                        : (int)Math.Clamp(earliest - now, 1, _capMs);
                    if (WaitCancellable(sleepMs,ct)) return;      // 取消 → 退出
                }
            }
            catch (ObjectDisposedException)
            {
                // cts 竞态释放——退出
            }
        }

        /// <summary>可取消同步等待。true = 已取消（退出）。</summary>
        private static bool WaitCancellable(int milliseconds,CancellationToken ct)
        {
            try
            {
                return ct.WaitHandle.WaitOne(milliseconds);
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        /// <summary>同步释放分片：取消节拍线程的取消源、有界等待节拍线程退出后释放取消源；超时仅记 LogWarning。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_lifecycleLock)
            {
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            }
            var thread = _thread;
            if (thread is not null)
            {
                try
                {
#pragma warning disable TCSG137 // 设计必需：同步 Dispose 契约——返回前有界收尾（TaskSink.Dispose 同步等待同款）
                    if (!thread.Wait(ExitTimeout)) _logger?.LogWarning("DeadlineRegistry 节拍线程等待退出超时：shard={Shard}", _index);
#pragma warning restore TCSG137
                }
                catch { /* 吞线程内异常（循环体已隔离） */ }
            }
            _cts?.Dispose();
        }

        /// <summary>异步释放分片：取消节拍线程的取消源、有界异步等待节拍线程退出后释放取消源；超时仅记 LogWarning。</summary>
        /// <returns>任务在节拍线程退出（或超时放弃等待）并释放取消源后完成。</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_lifecycleLock)
            {
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            }
            if (_thread is { } thread)
            {
                try { await thread.WaitAsync(ExitTimeout).ConfigureAwait(false); }
                catch (TimeoutException) { _logger?.LogWarning("DeadlineRegistry 节拍线程等待退出超时：shard={Shard}", _index); }
                catch { /* 吞线程内异常 */ }
            }
            _cts?.Dispose();
        }
    }

        /// <summary>订阅条目（句柄——Dispose = 退订，幂等）。</summary>
        private sealed class Entry(Func<long> deadlineTicks, Action wake, Shard owner) : IDisposable
        {
            private int _disposed;
            private long _lastWakeTicks;   // 取证：最近一次 SafeWake 时刻（tick 链断判别——pacer 活但条目不 wake = 扫描矛盾点）

            internal long DeadlineTicks() => deadlineTicks();

            /// <summary>条目取证快照（last-wake 滞后 = pacer 未唤醒本条目——tick 链断定位面）。</summary>
            internal string Describe()
            {
                var now = owner._clock.GetMsTimestamp();
                var lag = _lastWakeTicks == 0 ? -1 : now - Interlocked.Read(ref _lastWakeTicks);
                var dl = deadlineTicks() - now;
                return $"dl={dl}ms/lastWakeLag={lag}ms";
            }

        /// <summary>
        /// 唤醒回调（异常隔离——不杀节拍线程；channel 已 Complete 的退订竞态在此吞掉）。
        /// <para>★ 慢回调观测：wake 契约 = 快速 TryWrite——重回调会拖慢同 shard 全部消费者
        ///   （节拍线程串行回调）——&gt;10ms 记 LogWarning 防无声退化。</para></summary>
        internal void SafeWake()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Exchange(ref _lastWakeTicks, owner._clock.GetMsTimestamp());   // 取证：last-wake（tick 链断判别）
            try
            {
                wake();
            }
            catch (Exception ex)
            {
                owner.LogWarn(ex, $"DeadlineRegistry 唤醒回调异常（隔离继续）：{ex.Message}");
            }
            finally
            {
                owner.MarkWake();
                if (sw.ElapsedMilliseconds > 10)
                    owner.LogSlowWake(sw.ElapsedMilliseconds);
            }
        }

        /// <summary>退订：从所属 shard 移除本条目（幂等，双 Dispose 仅首次生效）。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Remove(this);
        }
    }
}
