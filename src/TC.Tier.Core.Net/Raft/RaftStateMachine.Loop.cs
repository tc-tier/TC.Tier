using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;

namespace TC.Tier.Core.Net.Raft;

// RaftStateMachine 分区（事件循环——spec-01 §6.2 单 worker；全类型分区见 RaftStateMachine.cs 类头）
//
// ★ 泵域（AsyncPump 单线程泵——共识循环执行载体，2026-09-03 活性判例）：LoopAsync 跑在
//   专用线程的泵上，本文件全部 await 不写 ConfigureAwait(false)——续体经 PumpContext 回流
//   泵线程（域内回流即 AsyncPump 语义），共识循环的关键路径零线程池依赖（本机池上续体
//   偶发不执行实证：tick 存活/事件积压/循环推进冻结）。域外（复制 lane、投票发送、直排
//   append）保持 TaskSink 池上 fire-and-forget——可丢失自愈语义。

/// <summary>
/// raft 状态机的循环分区——共识事件循环（单 worker 泵域：初始化、事件消费、心跳/选举定时）。
/// </summary>
public sealed partial class RaftStateMachine
{
    // ═══ 事件循环（spec-01 §6.2 单 worker）═══

    /// <summary>共识事件循环（AsyncPump 泵域专用线程——初始化 + 单事件消费；
    /// 退出时清理未决 pending/归还事件）。域内 await 一律不写 ConfigureAwait(false)。</summary>
    /// <param name="cancellationToken">取消令牌（停止/Dispose drain）。</param>
    private async Task LoopAsync(CancellationToken cancellationToken=default)
    {
        // ★ 双 token：组 ct（Dispose drain）+ _loopCts（StopAsync）任一取消即退
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _loopCts?.Token ?? CancellationToken.None);
        cancellationToken = linked.Token;
        _loopStarted.TrySetResult();
        Volatile.Write(ref _lastLoopProgressTicks, _clock.GetMsTimestamp());
        if (_config.Count == 1 && _config.Contains(_self) && _config.IsVoter(_self) && !_config.IsWitness(_self))
        {
            // ★ Standalone 快捷路径（spec-01 §5.5 定案）：N=1 多数派 = 1——启动即 Leader（跳过
            //   PreVote/Candidate 全流程、零网络开销）；心跳循环照常（无 peer 空转）。
            //   ★ voter 门（二期-B 锚点暴露的既有潜伏缺陷）：单 learner 配置（Count==1 非 voter）
            //     不得走捷径——learner 无多数派可 lead，Commit 推进面（MajorityThreshold 对零
            //     voter 配置）也无定义；单 learner 节点 = 待加入的 follower（JoinAsync 形态）。
            //   ★ witness 门（三期-F2 装配面暴露）：witness 计多数派（IsVoter=true）但永不自荐
            //     （高水位无日志体——选举超时门已拦，捷径路径同规）；单 witness 节点 =
            //     待加入的断言流 follower（JoinAsync asWitness 形态）。
            await BecomeLeaderAsync(cancellationToken);
        }
        else
        {
            ResetElectionTimer();
        }
        try
        {
            while (true)
            {
                // ★ 纯事件读（无每轮临时 timer——TimerQueue 全局锁竞争消除）；
                //   空闲唤醒由 DeadlineRegistry 节拍投 Tick；deadline 到期检查在每事件处理后
                //   （事件密集时心跳/选举仍推进；空闲时节拍封顶 100ms 粒度内触发）
                var evt = await _events.Reader.ReadAsync(cancellationToken);
                if (evt is RaftEvent.Tick or RaftEvent.PersistCompleted)
                {
                    // ★ 两者同为"仅唤醒"事件：节拍 → deadline 检查；完工唤醒 → 批尾观察在循环尾部
                    //   FlushPendingAppendsAsync 收口（W4 persist 下环——fsync 期间事件照常消费）
                    CheckDeadline(_clock.GetMsTimestamp());
                }
                else
                {
                    await HandleEventAsync(evt, cancellationToken);
                    CheckDeadline(_clock.GetMsTimestamp());
                }
                await FlushPendingAppendsAsync(cancellationToken);
                Volatile.Write(ref _lastLoopProgressTicks, _clock.GetMsTimestamp());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _loopException, ex);
            _logger?.LogError(ex, "Raft 状态机循环异常终止（fail-fast）：node={Self}", _self);
        }
        finally
        {
            // ★ W4 下环兜底：在途组提交有界 drain（fsync 时长级——CT 无涉，等待即 IO 自然有界）。
            //   失败入 _loopException（未被观察时）；已有主异常则吞并列发次因——停摆归因不换主。
            if (_persistCommitTask is { } inFlight)
            {
                try
                {
                    await inFlight;
                }
                catch (Exception ex) when (Volatile.Read(ref _loopException) is null)
                {
                    Volatile.Write(ref _loopException, ex);
                }
                catch
                {
                    // 已有主异常——并发次因不覆盖归因
                }
                _persistCommitTask = null;
            }

            // 未决 pending 全部取消（节点停摆——调用方经 ct/异常感知）
            lock (_completionLock)
            {
                _window.CancelAll();
                foreach (var tcs in _pending.Values) tcs.TrySetCanceled(cancellationToken);
                _pending.Clear();
                foreach (var tcs in _pendingCommitted.Values) tcs.TrySetCanceled(cancellationToken);
                _pendingCommitted.Clear();
                foreach (var tcs in _pendingLeaderLocal.Values) tcs.TrySetCanceled(cancellationToken);
                _pendingLeaderLocal.Clear();
                foreach (var (_, tcs) in _pendingAppliedWaiters) tcs.TrySetCanceled(cancellationToken);   // ★ 二期-B §6.3
                _pendingAppliedWaiters.Clear();
            }
            while (_events.Reader.TryRead(out var leftover))   // 未处理事件归还（池化防泄漏）
            {
                if (leftover is RaftEvent.Replicate r) r.Return();
            }
            // ★ 二期-B：批量取消挂起 ReadIndex waiter（本地 TCS 取消；转发 waiter 无回程可依赖——
            //   对端 per-call 超时自愈，连接已随循环收尾）
            lock (_readIndexLock)
            {
                _readIndexRoundActive = false;
                foreach (var w in _readIndexWaiters) w.Local?.TrySetCanceled(cancellationToken);
                _readIndexWaiters.Clear();
            }
            _events.Writer.TryComplete();
        }
    }

    /// <summary>deadline 到期检查（每事件后 + tick 唤醒——到期即走定时器路径）。</summary>
    /// <param name="now">当前时间戳（_clock.GetMsTimestamp()）。</param>
    private void CheckDeadline(long now)
    {
        if (now < NextDeadlineTicks()) return;   // 未到期
        // ★ 调度饥饿判定（#504 挂死族根因——follower 泵线程被饿时墙钟超期 ≠ leader 失联）：
        //   自上次循环推进完成（_lastLoopProgressTicks）已错过至少一个完整最小选举窗 = 观测能力
        //   缺失（可能错过 leader 心跳，也可能 leader 正常只是自己没被调度）——不触发选举，按
        //   当前时刻重掷选举截止，让积压入站事件（心跳）先被处理；被饿期间的墙钟不计入选举超时。
        //   leader 心跳侧不改：迟发心跳无害（follower 靠"收到即处理"续命，不要求准时）。
        if (_role != RaftRole.Leader)
        {
            var gap = now - Volatile.Read(ref _lastLoopProgressTicks);
            if (gap >= (long)_options.ElectionTimeoutMin.TotalMilliseconds)
            {
                ResetElectionTimer();
                return;
            }
        }
        HandleTimerElapsed(now);
    }

    /// <summary>
    /// ★ 批尾组提交（单写者批式直冲尾部）+ <b>W4 persist 下环</b>：存储写路径全程只在循环（append）
    /// 线程——复制链只读已提交区（消除跨线程写锁竞争 park 税）。队首还有追加类事件 → 攒批不提交不唤醒；
    /// 批尾 → 组提交<b>发起为在途任务、不内联等待</b>（fsync 期间事件循环照常消费——心跳/选举/复制
    /// 不受阻，换届风暴残余的机制根除）；任务完工自投 <see cref="RaftEvent.PersistCompleted"/> 唤醒，
    /// 下一观察点（每事件尾部 + 节拍兜底）收尾：一次 fsync = 一批，drain/commit 推进/复制唤醒语义不变。
    /// <para>★ 循环级兜底（死锁三角破解）：批尾判断若只挂在 HandleReplicate 尾部，Tick 等
    /// 非追加事件混入队列会把"本应批尾"的 append 卡成不提交不唤醒——链只读已提交区
    /// 永远读不到新条目、caller 永等（链等提交 / 提交等批尾 / 批尾等 append）。
    /// 本方法在<b>每个</b>事件处理后执行——Tick 等事件不算攒批（处理完即兜底 flush），
    /// 串行路径不被 tick 周期推迟。</para>
    /// <para>★ 应答点不变量：任何完成档（LeaderLocal/committed）仍在水位覆盖之后（观察点收尾）；
    /// 单写者不变量：commit 只在循环线程发起（_persistCommitTask 在途门——同串 fsync 不并发）。</para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask FlushPendingAppendsAsync(CancellationToken cancellationToken = default)
    {
        // ═══ 在途观察（先于一切角色/水位判定——换届/停摆路径也要观察异常）═══
        if (_persistCommitTask is { } inFlight)
        {
            if (!inFlight.IsCompleted) return;   // fsync 在途——本批追加攒进下一次提交（WAL 双页写不让渡）
            _persistCommitTask = null;
            await inFlight;   // 已完成任务——同步续体；异常原样重抛（fail-fast 与内联 await 等价）
            if (_role != RaftRole.Leader) return;   // 换届后批尾后续无意义（等待者已被 StepDown/finally 取消）
            DrainLeaderLocal();   // ★ fsync 已覆盖全部挂起 index——LeaderLocal 档等待者在此完成（应答点前移，不等多数派/apply）
            if (_replication is { } rep)   // CA2012：ValueTask 直 await（coalesce 临时量 = 分析器判非直接消费）
            {
                // ★ 本地持久化 = 自己的 matchIndex 到位——commit 推进尝试（N=1 Standalone 的唯一
                //   触发点：无 follower 应答驱动；多节点下幂等——多数派未跟上自然早退，resp 路径照旧）
                await rep.TryAdvanceCommitAsync(cancellationToken);
                rep.NotifyAppended();
            }
        }

        if (_role != RaftRole.Leader) return;
        if (_store.AllocatedIndex <= _store.PersistedIndex)
        {
            // ★ wal 自动提交轨的推进观察点（V0.1 根修）：持久化可由 wal 侧提交链推进到分配尾
            //   （策略同步提交——OnAppended 触发 / 后台时间循环）——此时 raft 在途提交任务不存在，
            //   上方观察点不运行；N=1 无 follower 应答驱动，commit 推进唯一触发点即本观察点，
            //   缺失 = persisted 推进而 commitIndex 恒 0、循环只消费 Tick、ReplicateAsync 永挂
            //   （2 核 pin 取证实锤）。此处补跑推进判定：与在途观察点同语义（DrainLeaderLocal
            //   应答 LeaderLocal 档；TryAdvanceCommitAsync 幂等早退——n ≤ commit 零成本，
            //   空闲 leader 每 Tick 一次 O(members) 快照读）；仅 commit 真推进才唤醒复制
            //   （NotifyAppended 累计攒批计数——无条件调用会虚增）。
            DrainLeaderLocal();
            if (_replication is { } rep)
            {
                var commitBefore = CommitIndex;
                await rep.TryAdvanceCommitAsync(cancellationToken);
                if (CommitIndex != commitBefore) rep.NotifyAppended();
            }
            return;
        }   // 无未提交 append
        if (_events.Reader.TryPeek(out var next) && IsAppendEvent(next)) return;   // 后续还有追加——攒
        _persistCommitTask = PersistAndSignalAsync(_store.AllocatedIndex,cancellationToken);   // 发起（在途——不阻塞循环）
    }

    /// <summary>在途组提交任务（循环线程发起）：等水位覆盖目标后自投完工唤醒事件——
    /// 观察点由此被即时拉起（空闲态不吃节拍延迟——LeaderLocal 应答/复制唤醒零回归）。
    /// 异常经任务面由观察点/finally 重抛；唤醒写入对完结 channel 静默失败（退出竞态无害）。</summary>
    /// <param name="target">目标持久化水位（AllocatedIndex——fsync 推进至此）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PersistAndSignalAsync(long target,CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.WaitForPersistedAsync(target,cancellationToken);
        }
        finally
        {
            _events.Writer.TryWrite(PersistCompletedEvent);
        }
    }

    /// <summary>完成持久化水位已覆盖的 LeaderLocal 档 pending（本地 fsync 组提交后——
    /// 应答档完成点；换届/停机路径的取消清理见 StepDownAsync/循环 finally）。</summary>
    private void DrainLeaderLocal()
    {
        if (_pendingLeaderLocal.Count == 0) return;
        lock (_completionLock)
        {
            var persisted = _store.PersistedIndex;
            foreach (var key in _pendingLeaderLocal.Keys)
                if (key <= persisted) _completedLeaderLocalKeys.Add(key);
            foreach (var key in _completedLeaderLocalKeys)
            {
                if (_pendingLeaderLocal.Remove(key, out var tcs))
                    tcs.TrySetResult(key);
            }
            _completedLeaderLocalKeys.Clear();
        }
    }

    private static bool IsAppendEvent(RaftEvent evt)
        => evt is RaftEvent.Replicate or RaftEvent.ReplicateBatch or RaftEvent.ProposeConfig;

    /// <summary>下一次 deadline（registry 节拍拉取 + 循环内 CheckDeadline 共用）。
    /// ★ 复制窗到期不折入此处（判例 2026-09-04 双实验证伪）：(1) 折入 = 窗 epoch 连续触发
    /// leader 分支 → 每 epoch 重置心跳 → 相位纠缠崩塌；(2) 改 ReplicationProcess 自持
    /// registry 条目 = 拉取模型唤醒延迟（pacer 睡眠余量数十 ms——心跳/选举量级可接受，
    /// 1ms 批窗物理不可达）。窗到期最终落 one-shot Timer（TimerQueue——非共识活性路径：
    /// 心跳 tick 仍为活性源 + 兜底，TimerQueue 孤儿判例不适用）。</summary>
    private long NextDeadlineTicks() => _role switch
    {
        RaftRole.Leader => Interlocked.Read(ref _heartbeatDeadlineTicks),
        _ => Interlocked.Read(ref _electionDeadlineTicks),
    };

    private void ResetElectionTimer()
    {
        // ★ Random 非线程安全（循环线程与 AppendEntries 直排线程并发掷窗——状态污染防御）
        TimeSpan timeout;
        lock (_random)
            timeout = _options.RollElectionTimeout(_random);
        Interlocked.Exchange(ref _electionDeadlineTicks,
            _clock.GetMsTimestamp() + (long)timeout.TotalMilliseconds);
    }

    private void ResetHeartbeatTimer()
    {
        Interlocked.Exchange(ref _heartbeatDeadlineTicks,
            _clock.GetMsTimestamp() + (long)_options.HeartbeatInterval.TotalMilliseconds);
    }

    private void HandleTimerElapsed(long now)
    {
        if (_role == RaftRole.Leader)
        {
            ResetHeartbeatTimer();
            if (_replication is { } rep)   // CA2012：ValueTask 直 await
                rep.HandleTick();
            return;
        }
        // 选举超时到期——每次触发后重掷（spec-01 §4.1 规则 4）
        ResetElectionTimer();
        HandleElectionTimeout();
    }

    private async ValueTask HandleEventAsync(RaftEvent evt, CancellationToken cancellationToken = default)
    {
        switch (evt)
        {
            case RaftEvent.Rpc { Message: PreVoteReq req, Reply: { } preVoteReply } rpc:
                await HandlePreVoteReqAsync(rpc.From, req, preVoteReply, cancellationToken);
                break;
            case RaftEvent.Rpc { Message: PreVoteResp resp } rpc:
                await HandlePreVoteRespAsync(rpc.From, resp, cancellationToken);
                break;
            case RaftEvent.Rpc { Message: RequestVoteReq req, Reply: { } voteReply } rpc:
                await HandleRequestVoteReqAsync(rpc.From, req, voteReply, cancellationToken);
                break;
            case RaftEvent.Rpc { Message: RequestVoteResp resp } rpc:
                await HandleRequestVoteRespAsync(rpc.From, resp, cancellationToken);
                break;
            // ★ AppendEntries 请求已接收直排（请求回调线程 + follower 日志门串行）——不经事件队列；
            //   AppendEntries 应答经复制 lane 的请求任务回投（HandleAppendEntriesRespAsync）
            case RaftEvent.Rpc { Message: InstallSnapshotReq req, Reply: { } snapReply } rpc:
                await HandleInstallSnapshotReqAsync(rpc.From, req, snapReply, cancellationToken);
                break;
            case RaftEvent.Rpc { Message: InstallSnapshotResp resp } rpc:
                await HandleInstallSnapshotRespAsync(rpc.From, resp, cancellationToken);
                break;
            case RaftEvent.Rpc { Message: ReadIndexReq req, Reply: { } riReply } rpc:
                await HandleReadIndexReqAsync(rpc.From, req, riReply, cancellationToken);
                break;
            case RaftEvent.Rpc { Message: TransferLeaderReq req, Reply: { } tlReply } rpc:
                await HandleTransferLeaderReqAsync(rpc.From, req, tlReply, cancellationToken);
                break;
            case RaftEvent.Replicate rep:
                await HandleReplicateAsync(rep,cancellationToken);
                break;
            case RaftEvent.ReplicateBatch batch:
                await HandleReplicateBatchAsync(batch,cancellationToken);
                break;
            case RaftEvent.ReadIndex ri:
                HandleReadIndex(ri);
                break;
            case RaftEvent.ProposeConfig pc:
                await HandleProposeConfigAsync(pc,cancellationToken);
                break;
            case RaftEvent.PromoteLearner pl:
                await HandlePromoteLearnerAsync(pl,cancellationToken);
                break;
            case RaftEvent.ConfigChanged cc:
                HandleConfigChanged(cc.Config,cancellationToken);
                break;
            case RaftEvent.Tick:
                break;   // 已在循环主体处理（防御不可达）
            case RaftEvent.Stop stop:
                stop.Done.TrySetResult();
                return;   // ★ 停止事件 = 循环退出指令（finally 完成清理——不再消费队列）
            default:
                _logger?.LogWarning("未识别事件：{Event}", evt.GetType().Name);
                break;
        }
    }
}
