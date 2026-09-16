using System.Threading.Channels;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 提交→应用管道（spec-05 §3 定案）——与共识循环解耦的独立单 worker：
/// <list type="bullet">
/// <item>按 index 序取 committed 批次 [lastApplied+1, commitIndex] → ApplyAsync 逐条（严格与日志序一致）；</item>
/// <item>appliedIndex 节流落盘（spec-05 §2——每 N 条/每 T 毫秒，断电损失 ≤ 节流窗口重放成本）；</item>
/// <item>配置条目分流（spec-04——配置切换 = apply 产物，经回调通知状态机；判别 =
/// 条目结构字段 Kind（<see cref="RaftEntryKind"/>——wire v3 信封之死））；</item>
/// <item>应用完成 → <see cref="AppliedTo"/> 事件（状态机完成 ReplicateAsync pending——spec-05 契约②）。</item>
/// </list>
/// <para>★ 正确性（spec-05 §3）：raft 安全性 = 多数派提交，与本地 apply 无关——apply 落后安全
/// （只延迟读服务，不产生错误状态）；单消费者 FIFO 保序 → 按序恰好一次（重复仅发生在重启重放——
/// at-least-once 契约，业务幂等）。</para>
/// <para>★ 存储经 <see cref="IRaftStore"/> 端口（spec-12 §8.1）：★ 双源读（spec-03 §7）——
/// applied &lt; N₀ 的跨界区间由 <see cref="RebuildCoordinator"/> 经快照读面补齐（快照 =
/// 可导出的日志前缀镜像），管道无需感知快照面。</para>
/// </summary>
public sealed class ApplyPipeline : IApplySink, IRebuildableApplySink, IAsyncDisposable
{
    private readonly IRaftStore _store;
    private readonly IStateMachine _machine;
    private readonly ApplyPipelineOptions _options;
    private Action<ClusterConfig>? _onConfigChanged; // ★ 装配晚绑定（状态机构造在管道之后——装配 SetConfigCallback）
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一 P1）
    private readonly Channel<long> _queue;
    private readonly TaskSink _tasks; // 背压等待提交任务组（fire-and-forget 纪律——受控丢弃）
    private readonly AsyncPump _pump; // worker 泵（单线程亲和——域内续体回流泵线程，池不在 apply 关键路径）

    private long _lastApplied; // 已应用水位（worker 线程）
    private long _persistedApplied; // 已落盘水位（节流）
    private long _lastPersistTicks; // 距上次落盘（时间节流）
    private ClusterConfig? _currentConfig; // 最新活动配置（配置条目 apply 产物——spec-04）

    private Task? _worker;
    private CancellationTokenSource? _cts;
    private int _started;
    private int _stopped;
    private int _disposed;

    /// <summary>当前活动配置（重建/apply 产物；null = 尚无配置条目——状态机用装配配置回退）。</summary>
    public ClusterConfig? CurrentConfig => Volatile.Read(ref _currentConfig);

    /// <summary>晚绑定配置条目回调（装配——状态机构造完成后）。</summary>
    /// <param name="callback">配置条目 apply 回调（→ 状态机 ConfigChanged 入队——spec-04）。</param>
    public void SetConfigCallback(Action<ClusterConfig> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Volatile.Write(ref _onConfigChanged, callback);
    }

    /// <summary>已应用水位（跨线程观测）。</summary>
    public long AppliedIndex => Interlocked.Read(ref _lastApplied);

    /// <inheritdoc/>
    public event Action<long>? AppliedTo;

    /// <summary>
    /// 构造（零 IO——启动经 <see cref="StartAsync"/>；装配顺序：<see cref="RebuildCoordinator"/> 重建 →
    /// 本管道 StartAsync → 状态机循环启动）。</summary>
    /// <param name="store">存储端口（读日志 + appliedIndex 落盘）。</param>
    /// <param name="machine">业务状态机（ApplyAsync 单 worker 调用）。</param>
    /// <param name="options">节流/背压参数。</param>
    /// <param name="onConfigChanged">配置条目 apply 回调（→ 状态机 ConfigChanged 入队——spec-04；可为 null——装配后经 <see cref="SetConfigCallback"/> 绑定）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="clock">时间供给源（过期度量；null = 系统时钟）。</param>
    public ApplyPipeline(IRaftStore store, IStateMachine machine, ApplyPipelineOptions options,
        Action<ClusterConfig>? onConfigChanged = null, ILogger? logger = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock ?? TimeProvider.System;   // 时钟缝 件一 P1（appliedIndex 持久化节奏）
        _store = store;
        _machine = machine;
        _options = options;
        _onConfigChanged = onConfigChanged;
        _logger = logger;
        _tasks = new TaskSink($"apply-{store.GetHashCode()}", logger: logger);
        _pump = new AsyncPump($"apply-worker-{store.GetHashCode()}", logger);
        _queue = Channel.CreateBounded<long>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // 满 = 提交方 await = 提交推进自然减速（spec-05 §6）
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// 启动 worker（lastApplied 起点 = 存储端口恢复值——重建后启动）。
    /// </summary>
    /// <returns>完成时 worker 已启动（同步完成——只可一次，重复启动抛）。</returns>
    /// <exception cref="InvalidOperationException">重复启动。</exception>
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("ApplyPipeline 已启动（StartAsync 只可一次）。");
        _lastApplied = _store.AppliedIndex;
        _persistedApplied = _lastApplied;
        _lastPersistTicks = _clock.GetMsTimestamp();
        _cts = new CancellationTokenSource();
        // ★ LongRunning 专用线程 + AsyncPump 泵域（共识循环同款——apply 链挂线程池 = 单条复制
        //   延迟抖动主源；域内续体回流泵线程，池续体丢失不再挂死 apply——2026-09-03 applied 卡
        //   committed-1 实锤形态）
        var ct = _cts.Token;
        var pump = _pump;
        _worker = Task.Factory.StartNew(() =>
            {
                try { pump.Run(() => WorkerAsync(ct)); }
                catch (Exception ex) { _logger?.LogError(ex, "ApplyPipeline worker 泵外逃逸异常：store={Store}", _store.GetHashCode()); }
            },
            CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        return Task.CompletedTask;
    }

    /// <summary>停止 worker（drain 在途提交后退出）。</summary>
    /// <param name="cancellationToken">取消令牌（等待 worker 退出的取消——worker 卡在业务 ApplyAsync 时兜底）。</param>
    /// <returns>完成时 worker 已退出、队列已封闭（幂等——重复调用立即完成）。</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _queue.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                /* worker 卡在业务 ApplyAsync——cancellationToken 兜底 */
            }
            catch (OperationCanceledException)
            {
                /* cancellationToken 兜底到期（上层 Dispose 限时）——放弃等待，资源由进程收尾 */
            }
        }

        if (_cts is not null && !_cts.IsCancellationRequested)
            await _cts.CancelAsync();
    }

    /// <inheritdoc/>
    /// <returns>完成时任务组已排空、worker（若在跑）已停止（幂等）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_started != 0 && _stopped == 0)
            await StopAsync().ConfigureAwait(false);
        await _tasks.DisposeAsync().ConfigureAwait(false); // 背压等待任务组 drain
        _cts?.Dispose();
    }

    // ═══ RebuildableApplySink（spec-03——快照安装后重建）═══

    /// <inheritdoc/>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时业务状态已重建、appliedIndex 已重置（快照覆盖点 + 已提交未应用增量）。</returns>
    public async ValueTask RebuildFromSnapshotAsync(CancellationToken cancellationToken = default)
    {
        // 存储层导入已完成（调用方保证）——重建业务状态 + appliedIndex 重置（快照覆盖点 + 已提交未应用增量）
        var applied = await RebuildCoordinator.RebuildAsync(_store, _machine,
            cfg => _onConfigChanged?.Invoke(cfg), cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastApplied, applied);
        _persistedApplied = applied;
        _lastPersistTicks = _clock.GetMsTimestamp();
    }

    // ═══ IApplySink（状态机共识循环调用——commitIndex 推进投递）═══

    /// <inheritdoc/>
    /// <param name="commitIndex">目标提交水位（投递 [lastApplied+1, commitIndex] 应用区间）。</param>
    public void Submit(long commitIndex)
    {
        if (!_queue.Writer.TryWrite(commitIndex))
            _tasks.SubmitFast((Func<CancellationToken, ValueTask>)(async cancellationToken =>
                await WriteAsync(commitIndex, cancellationToken).ConfigureAwait(false))); // 队列满——背压等待（受控提交，任务组观测）
    }

    private async Task WriteAsync(long commitIndex,CancellationToken cancellationToken = default)
    {
        try
        {
            await _queue.Writer.WriteAsync(commitIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
    }

    // ═══ apply 单 worker（spec-05 §3——独立循环，与共识循环解耦）═══
    // ★ 泵域（AsyncPump 单线程泵——2026-09-03 活性判例）：本方法链全部 await 不写
    //   ConfigureAwait(false)——续体经 PumpContext 回流 worker 泵线程，apply 关键路径零池依赖。

    private async Task WorkerAsync(CancellationToken cancellationToken=default)
    {
        // ★ 单次 drain 异常不杀 worker：worker 死亡 = 后续 Submit 全部堆队列无人消费 → applied
        //   永卡 → ReplicateAsync 永挂。异常记录后重入队 commit 重试——有界延迟防永久错误热循环，
        //   持续异常经日志可观测。
        while (await _queue.Reader.WaitToReadAsync(cancellationToken))
        {
            var commit = await _queue.Reader.ReadAsync(cancellationToken);
            try
            {
                await DrainAsync(commit, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ApplyPipeline drain 异常（重入队重试）：commit={Commit} lastApplied={Last}",
                    commit, AppliedIndex);
                if (AppliedIndex < commit)
                {
                    try { await _clock.Delay(10, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                    if (!_queue.Writer.TryWrite(commit))   // 满则走背压任务（Submit 同款形态）
                        _tasks.SubmitFast((Func<CancellationToken, ValueTask>)(async ct2 =>
                            await WriteAsync(commit,ct2).ConfigureAwait(false)));
                }
            }
        }
    }

    /// <summary>应用 [lastApplied+1, commit] 区间（双源读——跨界区间见 <see cref="RebuildCoordinator"/>，严格与日志序一致）。
    /// <para>★ 快照推进竞态护栏（判例 2026-09-02 实锤——复制永挂根因）：快照安装/导出并发把
    /// SnapshotIndex 推过本地 applied 水位时，首轮双源读的"n0 快照 + 主数据读"两段不原子——
    /// 尾段条目在两次读之间被截进快照区 → 主数据读静默跳过 → applied 不推进且无异常无后续
    /// Submit → ReplicateAsync 永挂（实测形态：leader c61/a60/s61——followers 全 applied）。
    /// 未追满目标则重读（重读拿新 n0，经快照读面补齐），仍缺则重入队有界自愈。</para></summary>
    private async ValueTask DrainAsync(long commit, CancellationToken cancellationToken=default)
    {
        var last = Interlocked.Read(ref _lastApplied);
        if (commit <= last) return;

        var onConfig = Volatile.Read(ref _onConfigChanged);
        for (var pass = 0; pass < 3 && last < commit; pass++)
        {
            last = await RebuildCoordinator.ApplyCommittedRangeAsync(
                _store, _machine, last + 1, commit,
                cfg =>
                {
                    Volatile.Write(ref _currentConfig, cfg);
                    onConfig?.Invoke(cfg);
                    _logger?.LogInformation("ApplyPipeline 应用配置条目：members={Count}", cfg.Count);
                },
                cancellationToken);
        }
        if (last < commit)
        {
            _logger?.LogWarning("ApplyPipeline drain 未追满目标（快照推进竞态/镜像缺口）：commit={Commit} lastApplied={Last}——重入队重试",
                commit, last);
            // ★ 重入队节流（重试节奏兜底）：读面洞/竞态未解除前无延迟重入队 = 满速热循环
            //   （gate churn 自旋实测）——10ms 节流把任何残余缺口从 CPU 风暴降为有界自愈
            try { await _clock.Delay(10, cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (!_queue.Writer.TryWrite(commit))
                _tasks.SubmitFast((Func<CancellationToken, ValueTask>)(async ct2 =>
                    await WriteAsync(commit,ct2).ConfigureAwait(false)));
            return;
        }

        // ★ 节流落盘（区间应用完成后）
        if (last - _persistedApplied >= _options.PersistEvery
            || (last > _persistedApplied
                && Environment.TickCount64 - _lastPersistTicks >= (long)_options.PersistInterval.TotalMilliseconds))
        {
            await _store.UpdateAppliedIndexAsync(last, cancellationToken);
            _persistedApplied = last;
            _lastPersistTicks = _clock.GetMsTimestamp();
        }

        Interlocked.Exchange(ref _lastApplied, last);
        if (last > 0) AppliedTo?.Invoke(last);
    }
}