using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;

namespace TC.Tier.Core.Net.Raft;

// RaftStateMachine 分区（角色转换 + 选举——spec-01 §3.2/§5；全类型分区见 RaftStateMachine.cs 类头）

/// <summary>
/// raft 状态机的选举分区——角色转换统一路径（StepDown）、PreVote/Vote 选举流程与主动让位（Resign）。
/// </summary>
public sealed partial class RaftStateMachine
{
    // ═══ 角色转换（spec-01 §3.2 转换总表）═══

    private void SetRole(RaftRole role)
    {
        _role = role;
        Volatile.Write(ref _roleValue, (int)role);
        Volatile.Write(ref _isLeaderFlag, role == RaftRole.Leader ? 1 : 0);
        _logger?.LogInformation("Raft 角色转换：{Role}（term={Term}）", role, _store.Term);
    }

    /// <summary>降级统一路径（spec-01 §3.2）：落盘 (term, votedFor=∅) 先于应答 → Follower + 清 leader +
    /// 复制清理。★ <see cref="_clusterLock"/> 内（接收直排线程与循环线程的角色转换互斥——锁序恒先于
    /// <see cref="_followerAppendGate"/> 获取方）。</summary>
    /// <para>★ 顺序契约（伪同任期 AE 封堵判例）：SetRole(Follower)+复制链停（复制标志清零）
    /// <b>先于</b>任期落盘抬升——若先抬任期，正在途发送的 lane 会在"任期已新、复制标志未清"的
    /// 窗口内读出新任期构造 AppendEntries——对端合法 leader 视作同任期 leader 消息让位，
    /// 集群被降级节点的残留链革职（探针实锤：幸存者 leader 被旧 leader 降级窗口的 AE 掀翻）。</para>
    /// <para>★ 选举窗先掷（瞬时换届竞态封堵判例）：leader 任内选举窗字段从不刷新——角色翻转后
    /// 循环线程若在 ResetElectionTimer 之前读到该过期窗，CheckDeadline 立即成立 → 降级瞬间
    /// 直接 PreCandidate 抢班（探针实锤：降级 ms 级内 PreCandidate→Candidate 掀起新任期）。
    /// 掷新窗必须先于 SetRole——翻转后循环线程读到的永远是未来窗。</para>
    private async ValueTask StepDownAsync(long newTerm,CancellationToken cancellationToken = default)
    {
        await _clusterLock.WaitAsync(cancellationToken);
        try
        {
            if (_store.Term >= newTerm && _role != RaftRole.Leader) return;
            var wasLeader = _role == RaftRole.Leader;
            ResetElectionTimer();                     // ★ 新选举窗先掷（先于角色翻转——过期窗封锁）
            SetRole(RaftRole.Follower);              // 角色/标志先降——lane 读任期前必见标志零
            _replication?.BecomeFollower();          // 复制链停（Active=false + 复制标志零）
            await _store.WriteTermAndVoteAsync(newTerm, NodeId.Empty,cancellationToken);   // ★ 落盘先于应答（契约①）
            _leaderState.Store(new LeaderSlot(0, newTerm));
            if (wasLeader)
            {
                if(_leadershipCts is not null)
                    await _leadershipCts.CancelAsync();   // leadership 令牌随降级取消（先于事件——事件处理器即见取消）
                Volatile.Write(ref _quorumAckTicks, long.MinValue);   // 租约失效（降级后本节点读面走非 leader fail-fast）
                lock (_leaseLock)
                {
                    _followerAckTicks.Clear();
                    _pendingPromote.Clear();   // 晋级登记作废——加入方周期重递 JoinReq 向新 leader 自愈
                }
                Volatile.Write(ref _pendingPromoteCount, 0);
                // ★ 换届取消在途完成源：直排 append 与本降级交错时，已 append <b>未提交</b>的
                //   slot/TCS 不会再被本任期完成——取消（调用方重路由新 leader 重试，raft 客户端
                //   标准模式；条目由新 leader 截尾自愈）。
                // ★ 已提交区保留（重复入日志封堵判例 2026-09-02）：已提交条目不可截断（raft
                //   不变量 + AE 已提交区保护），apply 管线终必消费——若一并 Failed，调用方
                //   NotLeader 重试 → 同命令重复提交（快照 61/60 实锤）。保留 = 由 apply 推进完成。
                var notLeader = new NotLeaderException(LeaderId);
                var commitIndex = _commitPair.Read().Index;
                lock (_completionLock)
                {
                    _window.FailUncommitted(notLeader, commitIndex);
                    foreach (var key in _pending.Keys.Where(k => k > commitIndex).ToArray())
                    {
                        if (_pending.Remove(key, out var tcs)) tcs.TrySetException(notLeader);
                    }
                    foreach (var key in _pendingCommitted.Keys.Where(k => k > commitIndex).ToArray())
                    {
                        if (_pendingCommitted.Remove(key, out var tcs)) tcs.TrySetException(notLeader);
                    }
                    foreach (var key in _pendingLeaderLocal.Keys.Where(k => k > commitIndex).ToArray())
                    {
                        if (_pendingLeaderLocal.Remove(key, out var tcs)) tcs.TrySetException(notLeader);
                    }
                    // ★ 二期-B §6.3：applied waiter 随降级取消（停止/降级 = 无人再推进水位的终态）
                    foreach (var (_, tcs) in _pendingAppliedWaiters) tcs.TrySetCanceled(CancellationToken.None);
                    _pendingAppliedWaiters.Clear();
                }
                FailAllReadIndexWaiters(notLeader, newTerm);   // ★ 二期-B §6.1：挂起 ReadIndex 全失败
                Volatile.Write(ref _transferState, null);   // ★ 二期-D2：转让随降级作废（超时回退终态）
            }
            if (wasLeader)
            {
                LeaderChanged?.Invoke(false);
                _raftView?.OnLeaderChanged(false, newTerm);   // 二期-I2
            }
            _logger?.LogInformation("Raft 降级：term={Term}（wasLeader={WasLeader}）", newTerm, wasLeader);
        }
        finally
        {
            _clusterLock.Release();
        }
    }

    /// <summary>失败全部挂起 ReadIndex waiter（二期-B §6.1——降级挂死缺陷根治）：本地 waiter
    /// 以 <see cref="NotLeaderException"/> 失败（调用方重路由新 leader）；转发 waiter 经 _loops
    /// 应答 <c>ReadIndexResp(term, -1)</c>（尽力回写——对端按 leader 变更重试）。【_clusterLock 内调用】</summary>
    private void FailAllReadIndexWaiters(NotLeaderException notLeader, long newTerm)
    {
        List<ReadIndexWaiter> waiters;
        lock (_readIndexLock)
        {
            _readIndexRoundActive = false;
            _readIndexConfirmations = [];
            waiters = new List<ReadIndexWaiter>(_readIndexWaiters);
            _readIndexWaiters.Clear();
        }
        Volatile.Write(ref _readIndexHoldIndex, 0);   // 锚点随 leadership 清零
        foreach (var w in waiters)
        {
            if (w.Local is { } tcs)
                tcs.TrySetException(notLeader);
            else if (w.Forwarded is { } reply)
                _loops.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
                    await ReplyAsync(reply, new ReadIndexResp { Term = newTerm, ReadIndex = -1 }, ct)));
        }
    }

    /// <summary>follower 自响应答抬任期（二期-B §6.2——转发线性读遇更高任期应答）：顺序契约同
    /// <see cref="StepDownAsync"/>（新选举窗先掷 → 角色翻转 → 复制链停 → 任期+清投票落盘）；
    /// 非 leader 专用——无 pending/租约清理面。leader 侧不可达（守卫直返）。</summary>
    private async ValueTask AdvanceTermFromRespAsync(long newTerm, CancellationToken cancellationToken = default)
    {
        await _clusterLock.WaitAsync(cancellationToken);
        try
        {
            if (newTerm <= _store.Term || _role == RaftRole.Leader) return;
            ResetElectionTimer();                     // ★ 新选举窗先掷（先于角色翻转——过期窗封锁）
            SetRole(RaftRole.Follower);               // 角色翻转（本应已是 Follower——防御 Candidate）
            _replication?.BecomeFollower();           // 复制链停
            await _store.WriteTermAndVoteAsync(newTerm, NodeId.Empty, cancellationToken);   // ★ 落盘先于续作（契约①）
            _leaderState.Store(new LeaderSlot(0, newTerm));   // leader 未知——等心跳/选举收敛
            _logger?.LogInformation("Raft 抬任期（转发应答携带更高任期）：term={Term}", newTerm);
        }
        finally
        {
            _clusterLock.Release();
        }
    }

    // ═══ 选举（spec-01 §5）═══

    /// <summary>
    /// 选举超时处理（spec-01 §4.1 规则 4）：Follower → PreCandidate → Candidate → Follower。
    /// </summary>
    private void HandleElectionTimeout()
    {
        if (_removedFromConfig) return;   // 被移除节点不参与选举（spec-04 §4）
        if (!_config.IsVoter(_self)) return;   // learner 不发起选举（件 B——不投票不计多数派；voter 全停机时 learner 不当选）
        if (_config.IsWitness(_self)) return;   // ★ 二期-F2：witness 投票但不自荐（高水位无日志体——永不成为 leader）
        TraceElection($"timeout role={_role} t={_store.Term}");
        switch (_role)
        {
            case RaftRole.Follower:
                StartPreElection();
                break;
            case RaftRole.PreCandidate:
                // 预票不足/超时——同 term 重发（零持久化）
                BroadcastPreVote();
                break;
            case RaftRole.Candidate:
                // 选举超时——新任期重走 PreVote（spec-01 §3.2 转换表）
                StartPreElection();
                break;
        }
    }

    /// <summary>预选举启动（<see cref="_clusterLock"/> 内——角色转换互斥）。</summary>
    private void StartPreElection()
    {
        _clusterLock.Wait(CancellationToken.None);
        try
        {
            SetRole(RaftRole.PreCandidate);
            _preVoteGranted.Clear();
            _preVoteGranted.Add(_self);   // 自己隐含一票
        }
        finally { _clusterLock.Release(); }
        BroadcastPreVote();
    }

    private void BroadcastPreVote()
    {
        // PreVote 请求带当前 term（选民不降级——分区节点无法抬集群 term，spec-01 §3.3 原始意图）
        var req = new PreVoteReq
        {
            Term = _store.Term,
            CandidateId = _self,
            LastLogIndex = _store.LastLogIndex,
            LastLogTerm = _store.LastLogTerm,
        };
        foreach (var m in _config.Members)
            if (m.Id != _self && _config.IsVoter(m.Id)) SubmitVoteRpc(m.Id, req);   // F2：含 witness（投票成员）
    }

    private async ValueTask StartRealElectionAsync(CancellationToken cancellationToken = default)
    {
        await _clusterLock.WaitAsync(cancellationToken);
        try
        {
            // ★ 抬任期落盘先于广播（spec-01 §4.2——契约①）
            await _store.WriteTermAndVoteAsync(_store.Term + 1, _self,cancellationToken);
            SetRole(RaftRole.Candidate);
            _voteGranted.Clear();
            _voteGranted.Add(_self);
            TraceElection($"realElect t={_store.Term}");
            _raftView?.OnElectionStarted();   // 二期-I2
            BroadcastVotes();
        }
        finally { _clusterLock.Release(); }
        _logger?.LogInformation("Raft 发起选举：term={Term} members={Count}", _store.Term, _config.Count);
    }

    /// <summary>
    /// 领导权定向转让（二期-D2——etcd MoveLeader 同族）：登记转让目标后等待目标追平日志，
    /// 追平即发 TimeoutNow（目标跳过预票立即真选举——抬任期使本端 StepDown）。返回 = 让位完成；
    /// 超时回退 = <see cref="TimeoutException"/>（本端仍在位可继续服务——转让意图作废）。
    /// </summary>
    /// <param name="target">转让目标（须为活动配置中的 voter）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="NotLeaderException">本节点非 leader。</exception>
    /// <exception cref="ArgumentException">目标不在活动配置或非 voter。</exception>
    /// <exception cref="TimeoutException">转让窗口超限（目标未追平/未发起选举）——超时回退。</exception>
    /// <returns>让位完成（目标真选举抬任期 → 本端已降级 follower）。</returns>
    public async ValueTask TransferLeadershipAsync(NodeId target, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0)
            throw new NotLeaderException(LeaderId);
        _raftView?.OnTransfer("requested");   // 二期-I2
        if (!_config.Contains(target) || !_config.IsFullVoter(target))
            throw new ArgumentException($"转让目标须为活动配置中的全量 voter（witness 无日志体不可承接——二期-F2；收到 {target}）。", nameof(target));
        Volatile.Write(ref _transferState, target);
        Volatile.Write(ref _transferTimeoutNowSent, 0);
        // 总时限 = 4 选举窗（目标追平 + 真选举收敛的保守上界）——超限回退（本端仍在位）
        var deadline = _clock.GetMsTimestamp() + (long)(4 * _options.ElectionTimeoutMax).TotalMilliseconds;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _isLeaderFlag) == 0)
                return;   // 目标真选举抬任期 → 本端 StepDown 统一路径——让位完成
            if (_clock.GetMsTimestamp() >= deadline)
                throw new TimeoutException($"领导权转让超时（target={target}）——超时回退：本端仍在位。");
            await _clock.Delay(25, ct);
        }
    }

    /// <summary>
    /// 优雅 drain（二期-D3——停新 + 排空 + 有界收尾）：置 drain 旗标后新提案立即以
    /// <see cref="NotLeaderException"/> 拒绝（客户端重路由语义）；等待在途收敛窗后按
    /// transferTarget 转让或自降级（滚动维护标准形态——零 leadership 真空）。
    /// </summary>
    /// <param name="transferTarget">转让目标（null = 自降级 Resign）。</param>
    /// <param name="drainWindow">在途排空窗（缺省 500ms——客户端停止投递后在途自然收敛）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>drain 全流程完成（排空窗等待 + 转让/自降级收尾——drain 旗标已清，新提案恢复受理）。</returns>
    public async ValueTask DrainAsync(NodeId? transferTarget = null, TimeSpan? drainWindow = null, CancellationToken ct = default)
    {
        Volatile.Write(ref _draining, 1);
        try
        {
            var window = drainWindow ?? TimeSpan.FromMilliseconds(500);
            if (window > TimeSpan.Zero)
                await _clock.Delay(window, ct);
            if (Volatile.Read(ref _isLeaderFlag) != 0)
            {
                if (transferTarget is { } target)
                    await TransferLeadershipAsync(target, ct);
                else
                    await ResignAsync(ct);
            }
        }
        finally
        {
            Volatile.Write(ref _draining, 0);
        }
    }

    /// <summary>RequestVote 广播（term/日志尾快照后发送——当选路径与直排线程共享）。</summary>
    private void BroadcastVotes()
    {
        var req = new RequestVoteReq
        {
            Term = _store.Term,
            CandidateId = _self,
            LastLogIndex = _store.LastLogIndex,
            LastLogTerm = _store.LastLogTerm,
        };
        foreach (var m in _config.Members)
            if (m.Id != _self && _config.IsVoter(m.Id)) SubmitVoteRpc(m.Id, req);   // F2：含 witness（投票成员）
    }

    private async ValueTask BecomeLeaderAsync(CancellationToken cancellationToken = default)
    {
        await _clusterLock.WaitAsync(cancellationToken);
        try
        {
            SetRole(RaftRole.Leader);
            // leadership 令牌换新（每任期一枚——旧令牌已随上次降级取消）
            if(_leadershipCts is not null)
                await _leadershipCts.CancelAsync();
            Volatile.Write(ref _leadershipCts, new CancellationTokenSource());
            // 租约读确认态重置（新任期——选举票本身即本任期首次多数派确认，租约窗自此起算）
            lock (_leaseLock) _followerAckTicks.Clear();
            Volatile.Write(ref _quorumAckTicks, _clock.GetMsTimestamp());
            TraceElection($"LEADER t={_store.Term}");
            _leaderState.Store(new LeaderSlot(SelfSeq(), _store.Term));
            _replication!.BecomeLeader(_config);
        }
        finally { _clusterLock.Release(); }
        LeaderChanged?.Invoke(true);
        // ★ 二期-B §6.4：上任即追加本任期锚点空条目（Noop——论文 §6.4/§8.2 惯例）：commit 计数
        //   只认本任期条目——不追加则新 leader 的 commitIndex 停在前任期水位（含 0），ReadIndex
        //   以其应答 = 线性读破约（前任期已提交写对新 leader 读面不可见）。锚点提交即推进水位。
        //   append 失败 = 存储故障——同追加路径 fail-fast 语义（沿循环异常面上抛）。
        var anchorIndex = await AppendEntriesAsync(
            new List<(long Term, byte Kind, ReadOnlyMemory<byte> Content)>
                { (_store.Term, RaftEntryKind.Noop, ReadOnlyMemory<byte>.Empty) },
            cancellationToken);
        Volatile.Write(ref _readIndexHoldIndex, anchorIndex);   // 锚点未提交前 ReadIndex 轮次挂起
        _raftView?.OnLeaderChanged(true, _store.Term);   // 二期-I2
        // 当选即时广播空 AppendEntries（spec-01 §5.3 确立权威——压掉分裂选举；链首拍即发）
        ResetHeartbeatTimer();
    }

    /// <summary>
    /// 主动让位（运维动作——节点下线/维护前先让位，避免等选举窗的超时窗口）：
    /// leader 立即降为 follower（任期不变、清本任投票——仍可给其他候选者投票），
    /// 集群经选举窗自然选出新 leader。<see cref="LeadershipToken"/> 随降级取消。
    /// 非 leader 调用抛 <see cref="NotLeaderException"/>（无位可让——明确失败而非幂等静默）。
    /// </summary>
    /// <param name="ct">取消令牌（让位前等待——降级落盘阶段取消）。</param>
    /// <exception cref="NotLeaderException">本节点非 Leader（无位可让）。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 已取消。</exception>
    /// <returns>完成时本节点已降级为 follower（任期不变、清本任投票——LeadershipToken 已取消）。</returns>
    public async ValueTask ResignAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0)
            throw new NotLeaderException(LeaderId);
        ct.ThrowIfCancellationRequested();
        // 任期不变的自降级——StepDown 统一路径一次到位（角色/复制清理/在途取消/事件）
        await StepDownAsync(_store.Term,ct).ConfigureAwait(false);
    }

    private static bool IsUpToDate(long candidateLastLogTerm, long candidateLastLogIndex, long myLastLogTerm, long myLastLogIndex)
    {
        if (candidateLastLogTerm > myLastLogTerm) return true;
        return candidateLastLogTerm == myLastLogTerm && candidateLastLogIndex >= myLastLogIndex;
    }
}
