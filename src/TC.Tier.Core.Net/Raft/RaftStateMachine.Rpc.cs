using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Raft;

// RaftStateMachine 分区（入站分发/出站发送/RPC 处理——spec-12 §5.2/§6 × spec-01/02；
// 全类型分区见 RaftStateMachine.cs 类头）

/// <summary>
/// raft 状态机的 RPC 分区——入站 RPC 分发（AppendEntries 直排/Vote/InstallSnapshot 事件化）
/// 与出站 AppendEntries/投票请求的编码发送。
/// </summary>
public sealed partial class RaftStateMachine
{
    // ═══ 入站分发（请求回调形态——spec-12 §5.2/§6）═══

    /// <summary>协议域请求 handler 桥（注册面持有——介质分发上下文回调）。</summary>
    private sealed class RpcHandler(RaftStateMachine owner) : IRequestHandler
    {
        /// <summary>入站请求转发（直通 owner.<see cref="OnRpcRequest"/>——解码/分发归状态机）。</summary>
        /// <param name="from">请求来源节点。</param>
        /// <param name="payload">RPC 载荷。</param>
        /// <param name="reply">应答上下文。</param>
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => owner.OnRpcRequest(from, payload, reply);
    }

    /// <summary>AppendEntries 直排工作载体（零闭包——字段携带参数 + <see cref="Run"/> 方法组缓存直指
    /// 处理体；一次性小对象随调用收敛，无池化生命周期面）。</summary>
    private sealed class AppendDirectWork
    {
        private readonly RaftStateMachine _owner;
        private readonly NodeId _from;
        private readonly AppendEntriesReq _req;
        private readonly IReplyContext _reply;

        /// <summary>任务组提交入口（缓存方法组——每载体一份委托，无显示类无 lambda 箱）。</summary>
        public readonly Func<CancellationToken, ValueTask> Run;

        public AppendDirectWork(RaftStateMachine owner, NodeId from, AppendEntriesReq req, IReplyContext reply)
        {
            _owner = owner;
            _from = from;
            _req = req;
            _reply = reply;
            Run = RunCore;
        }

        private ValueTask RunCore(CancellationToken cancellationToken = default)
            => new(_owner.HandleAppendEntriesReqDirectAsync(_from, _req, _reply, cancellationToken));
    }

    /// <summary>
    /// 入站 RPC 分发：解码失败 = 不应答（对端超时——尽力语义）；AppendEntries 请求接收直排
    /// （<see cref="_followerAppendGate"/> 串行）；Vote/InstallSnapshot 低频走事件队列（循环线程
    /// 串行 + <see cref="_clusterLock"/>，应答经 reply 上下文异步回写）。
    /// </summary>
    /// <param name="from">请求来源节点。</param>
    /// <param name="payload">RPC 载荷（已按协议域分发）。</param>
    /// <param name="reply">应答上下文（回程路由知识在介质）。</param>
    internal void OnRpcRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
    {
        if (!RaftRpcCodec.TryDecode(payload.Span, out var rpc))
        {
            _logger?.LogWarning("Raft RPC 载荷解码失败（协议域格式违规——丢弃）：from={From} len={Length}", from, payload.Length);
            return;
        }

        switch (rpc)
        {
            case AppendEntriesReq req:
                // ★ 接收直排处理受控提交（fire-and-forget 纪律——任务不裸丢弃，组内观测/Dispose 兜底）。
                //   ★ 零闭包直排载体（W3 乙案——原 async lambda 闭包+双状态机箱 per 请求消灭）：
                //   工作对象字段携带参数 + 缓存方法组委托直指处理体（每请求仅一对象一委托，
                //   GC 收敛；HandleAppendEntriesReqDirectAsync 自身真挂起保持 async）
                _loops.SubmitFast(new AppendDirectWork(this, from, req, reply).Run);
                return;
            case RequestVoteReq or PreVoteReq or InstallSnapshotReq or ReadIndexReq or TransferLeaderReq:
                if (!_events.Writer.TryWrite(new RaftEvent.Rpc(from, rpc, reply)))
                    _loops.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
                        await WriteRpcAsync(from, rpc, reply, ct).ConfigureAwait(false)));
                return;
            case JoinReq req:
                // 低频运维请求——受控提交直处理（leader 面：缺席提案 learner 入组 + 登记追平晋级）
                _loops.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
                    await HandleJoinReqAsync(from, req, reply, ct).ConfigureAwait(false)));
                return;
            default:
                // Resp 不应作为入站请求到达（应答由传输 CorrId 配对回投）——畸形丢弃
                _logger?.LogWarning("Raft 收到非请求消息（丢弃）：from={From} type={Type}", from, rpc.GetType().Name);
                return;
        }
    }

    private async Task WriteRpcAsync(NodeId from, RaftRpc rpc, IReplyContext? reply,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _events.Writer.WriteAsync(new RaftEvent.Rpc(from, rpc, reply), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>入站 ReadIndex 转发处理（二期-B §6.2——事件队列循环线程串行）：
    /// 更高任期 = 落盘降级（spec-01 §3.2 统一规则）后应答 -1；非 leader / 本端任期更新
    /// （调用方落后——抬任期后重试）= 应答 (currentTerm, -1)；leader = 并入批量轮次
    /// （<see cref="EnqueueReadIndexWaiter"/>——与本地读同队列同轮次）。</summary>
    private async ValueTask HandleReadIndexReqAsync(NodeId from, ReadIndexReq req, IReplyContext reply,
        CancellationToken cancellationToken = default)
    {
        if (req.Term > _store.Term)
            await StepDownAsync(req.Term, cancellationToken);
        if (_role != RaftRole.Leader || req.Term != _store.Term)
        {
            await ReplyAsync(reply, new ReadIndexResp { Term = _store.Term, ReadIndex = -1 }, cancellationToken);
            return;
        }
        if (_config.Count == 1)
        {
            await ReplyAsync(reply, new ReadIndexResp { Term = _store.Term, ReadIndex = _commitPair.Read().Index },
                cancellationToken);
            return;
        }
        EnqueueReadIndexWaiter(new ReadIndexWaiter(null, reply, from));
    }

    /// <summary>入站领导权转让处理（二期-D2——TimeoutNow 语义，事件队列循环线程串行）：
    /// 任期相符且本端为 voter follower = 受理——跳过预票立即真选举（term+1 自投）；
    /// 其余形态应答 Accepted=false（调用方超时回退——本端继续在位）。</summary>
    private async ValueTask HandleTransferLeaderReqAsync(NodeId from, TransferLeaderReq req, IReplyContext reply,
        CancellationToken cancellationToken = default)
    {
        if (req.Term == _store.Term && _role == RaftRole.Follower && _config.IsVoter(_self) && !_removedFromConfig)
        {
            await ReplyAsync(reply, new TransferLeaderResp { Term = _store.Term, Accepted = true }, cancellationToken);
            _logger?.LogInformation("Raft 领导权转让受理（TimeoutNow——跳过预票）：from={From} term={Term}", from, req.Term);
            await StartRealElectionAsync(cancellationToken);
            return;
        }
        await ReplyAsync(reply, new TransferLeaderResp { Term = _store.Term, Accepted = false }, cancellationToken);
    }

    /// <summary>完成一轮 ReadIndex（多数派确认达成——取当前 commitIndex 一次性完成全部 waiter；
    /// commit 单调性保证该 index ≥ 每个 waiter 入队时的水位，线性化点 = 轮次完成）。
    /// 本地 waiter 直完成；转发 waiter 经 _loops 调度回写（应答线程不等待网络写——尽力语义，
    /// 对端 per-call 超时自愈）。【应答到达线程调用】</summary>
    private void CompleteReadIndexRound(List<ReadIndexWaiter> waiters)
    {
        var index = _commitPair.Read().Index;
        foreach (var w in waiters)
        {
            if (w.Local is { } tcs)
                tcs.TrySetResult(index);
            else if (w.Forwarded is { } reply)
                _loops.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
                    await ReplyAsync(reply, new ReadIndexResp { Term = _store.Term, ReadIndex = index }, ct)));
        }
    }

    /// <summary>应答回写（尽力——回程失败 = 对端超时重试）。
    /// ★ 泵域内不写 ConfigureAwait(false)——回程续体回流应答方泵线程（同活性判例）。</summary>
    /// <param name="reply">应答上下文（回程路由由介质持有）。</param>
    /// <param name="resp">RPC 应答消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async ValueTask ReplyAsync(IReplyContext reply, RaftRpc resp,
        CancellationToken cancellationToken = default)
    {
        var len = RaftRpcCodec.EncodePooled(resp, out var buffer);
        try
        {
            await reply.ReplyAsync(buffer.AsMemory(0, len), cancellationToken);
        }
        catch (Exception)
        {
            // 回程失败 = 尽力送达最后一步——对端在途超时自愈
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ═══ 出站发送（请求回调形态——at-most-once 缺省 + raft 定时器自愈）═══
    // ★ 本节 await 不写 ConfigureAwait(false)（2026-09-03 活性判例）：投票/快照 RPC 由共识
    //   循环/lane 的泵线程发起——域内续体回流发起方泵线程（应答完成不依赖池续体调度；
    //   池续体偶发不执行 = 预票应答丢失 = PreCandidate 循环，轨迹实锤）。域外线程调用时
    //   无上下文可捕获，行为与 CA(false) 等价。

    /// <summary>出站 RPC：发送并等应答（per-call 超时对齐重试窗）。null = 失败/超时/取消/畸形应答。
    /// ★ 0-Copy：载荷池化编码，await 应答全程持租——重试重发闭包共享同一缓冲，应答裁决后归还。</summary>
    /// <param name="target">目标节点。</param>
    /// <param name="request">RPC 请求消息。</param>
    /// <param name="timeout">单次等待应答超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应答 RPC 消息；null = 失败/超时/取消/畸形应答。</returns>
    private async Task<RaftRpc?> SendRpcAsync(NodeId target, RaftRpc request, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var len = RaftRpcCodec.EncodePooled(request, out var buffer);
        try
        {
            var respBytes = await _transport.SendRequestAsync(target, ProtocolIds.Raft,
                buffer.AsMemory(0, len), new RequestOptions { Timeout = timeout }, cancellationToken);
            return RaftRpcCodec.TryDecode(respBytes, out var resp) ? resp : null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            return null; // 目标未连/超时/停止——尽力送达语义，raft 定时器自愈
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>投票 RPC 受控发送（fire-and-forget 形态——快提交；应答回投事件队列，循环线程串行计票）。
    /// ★ 发送/应答回投链全程泵域亲和（首段内联在调用方泵线程启动，后续续体回流同一泵）。</summary>
    private void SubmitVoteRpc(NodeId target, RaftRpc req)
    {
        var timeout = TimeSpan.FromMilliseconds(2 * _options.ElectionTimeoutMax.TotalMilliseconds);
        _loops.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
        {
            var resp = await SendRpcAsync(target, req, timeout, ct);
            if (resp is not null && !_events.Writer.TryWrite(new RaftEvent.Rpc(target, resp, null)))
                await WriteRpcAsync(target, resp, null, ct);
        }));
    }

    private void OnAppliedTo(long index)
    {
        // ★ 快道：apply worker 线程直接完成 pending——不经共识循环事件队列
        //   （省一跳线程唤醒；串行复制延迟主源之一）。事件序天然保持（apply 单 worker 按序回调）；
        //   完成结构与循环侧注册经 _completionLock 互斥。
        CompleteApplied(index);
    }

    /// <summary>完成 ≤ index 的全部 pending（applied 推进 + 窗口双水位前缀完成 + TCS 直通完成）——apply worker 线程调用。
    /// <para>★ committed 档 TCS/slot 不在此完成（完成源 = commit 推进——见 <see cref="CompleteCommitted"/>；
    /// 此处只以当前 commit 水位参与窗口双水位遍历：早于 applied 推进已完成的 committed 槽被消费）。</para></summary>
    private void CompleteApplied(long index)
    {
        lock (_completionLock)
        {
            if (index > _appliedWatermark) _appliedWatermark = index;
            _window.CompleteUpTo(_commitPair.Read().Index, index); // 双水位：committed 档吃 commit、applied 档吃 applied
            foreach (var key in _pending.Keys)
                if (key <= index)
                    _completedKeys.Add(key);
            foreach (var key in _completedKeys)
            {
                if (_pending.Remove(key, out var tcs))
                    tcs.TrySetResult(key);
            }

            _completedKeys.Clear();

            // ★ 二期-B §6.3：applied waiter 完成 ≤ index（WaitForAppliedAsync——扫描紧凑，
            //   waiter 低频、无序，保序压缩即可）
            if (_pendingAppliedWaiters.Count > 0)
            {
                var satisfied = 0;
                for (var i = 0; i < _pendingAppliedWaiters.Count; i++)
                {
                    var (waiterIndex, done) = _pendingAppliedWaiters[i];
                    if (waiterIndex <= index)
                    {
                        done.TrySetResult();
                        satisfied++;
                    }
                    else if (satisfied > 0)
                    {
                        _pendingAppliedWaiters[i - satisfied] = (waiterIndex, done);
                    }
                }
                if (satisfied > 0)
                    _pendingAppliedWaiters.RemoveRange(_pendingAppliedWaiters.Count - satisfied, satisfied);
            }
        }
        _raftView?.OnAppliedAdvanced(_appliedWatermark);   // 二期-I2（锁外——外部 sink 不持完成锁）
    }

    /// <summary>完成 ≤ commit 的全部 committed 档 pending（窗口双水位 + TCS 直通）——
    /// <see cref="AdvanceCommit"/> CAS 赢家调用（任意线程：多 lane 并发上报 / follower LeaderCommit 跟随）。</summary>
    private void CompleteCommitted(long commit)
    {
        lock (_completionLock)
        {
            _window.CompleteUpTo(commit, _appliedWatermark);
            foreach (var key in _pendingCommitted.Keys)
                if (key <= commit)
                    _completedCommittedKeys.Add(key);
            foreach (var key in _completedCommittedKeys)
            {
                if (_pendingCommitted.Remove(key, out var tcs))
                    tcs.TrySetResult(key);
            }

            _completedCommittedKeys.Clear();
        }
    }

    // ═══ RPC 处理：PreVote（spec-01 §3.3——零持久化）═══

    private async ValueTask HandlePreVoteReqAsync(NodeId from, PreVoteReq req, IReplyContext reply,
        CancellationToken cancellationToken = default)
    {
        if (req.Term < _store.Term)
        {
            await ReplyAsync(reply, new PreVoteResp { Term = _store.Term, Granted = false }, cancellationToken);
            return;
        }

        // ★ learner 不授预票（件 B——不参与选举投票；candidate 多数派只数 voter，拒绝无碍当选）
        // ★ PreVote 不降级（论文 §9.6：模拟任期——真实选举才落盘降级）：
        //   分区节点反复 PreVote 无法抬集群 term（否则选民降级后自己也竞选——选举风暴）
        // ★ votedFor 不参与 PreVote 裁决（论文 §9.6 模拟语义）：PreVote 模拟的是 next term
        //   （spec-01 §3.2「新任期重走 PreVote」）——当前任期内已投的票（含 split vote 后投给自己）
        //   对新任期不构成绑定。若沿用 RequestVote 的 votedFor 门禁，split vote 后双候选互锁在
        //   PreCandidate 永不抬任期（votedFor=self 永拒对方预票）——选举死锁（实锤楔死判例）。
        //   票唯一性仍由真实投票门禁（WriteTermAndVoteAsync 落盘 votedFor）保证。
        // ★ 在位 leader 否决（论文 §9.6 PreVote 本义——防扰主，2026-09-03 收敛停滞实锤）：
        //   本节点是 leader，或已知 leader 且其心跳在最近一个选举窗内仍在续约（_lastLeaderContact
        //   独立计时——选举超时路径会重掷选举窗，挂 deadline 上会让否决永不解除、选举死锁）
        //   = 集群有主——拒预票。否则满载下心跳间歇超窗即触发扰主真选举（在位 leader 授预票+
        //   被 StepDown 链掀翻），term 空转飙升、全员 PreCandidate 循环（快照实锤）。
        //   leader 死亡后心跳停止续约 → ElectionTimeoutMax 内否决自动解除，选举照常发起。
        var hasLiveLeader = _role == RaftRole.Leader
                            || (LeaderId is not null
                                && Environment.TickCount64 - Volatile.Read(ref _lastLeaderContactTicks)
                                < (long)_options.ElectionTimeoutMax.TotalMilliseconds);
        var granted = _config.IsVoter(_self)
                      && !hasLiveLeader
                      && IsUpToDate(req.LastLogTerm, req.LastLogIndex, _store.LastLogTerm, _store.LastLogIndex);
        TraceElection($"PreVoteReq←{from.ToString()[..6]} g={granted} veto={hasLiveLeader}");
        await ReplyAsync(reply, new PreVoteResp
        {
            Term = _store.Term,
            Granted = granted,
        }, cancellationToken);
    }

    private async ValueTask HandlePreVoteRespAsync(NodeId from, PreVoteResp resp,
        CancellationToken cancellationToken = default)
    {
        if (_role != RaftRole.PreCandidate || !_config.Contains(from)) return;
        if (resp.Term > _store.Term)
        {
            await StepDownAsync(resp.Term, cancellationToken); // 集群 term 已更高——降级
            return;
        }

        if (resp.Term < _store.Term) return; // 陈旧应答（旧任期）——忽略
        if (resp.Granted) _preVoteGranted.Add(from);
        TraceElection($"PreVoteResp←{from.ToString()[..6]} g={resp.Granted} n={CountVoters(_preVoteGranted)}");
        if (Quorum.HasMajority(CountVoters(_preVoteGranted), _config.VoterCount))
        {
            _logger?.LogInformation("Raft 获多数派预票：votes={Votes}/{Voters}", CountVoters(_preVoteGranted),
                _config.VoterCount);
            TraceElection("prevote majority");
            await StartRealElectionAsync(cancellationToken);
        }
    }

    // ═══ RPC 处理：RequestVote（spec-01 §5.2——选民侧，应答前落盘）═══

    private async ValueTask HandleRequestVoteReqAsync(NodeId from, RequestVoteReq req, IReplyContext reply,
        CancellationToken cancellationToken = default)
    {
        if (req.Term < _store.Term)
        {
            await ReplyAsync(reply, new RequestVoteResp { Term = _store.Term, Granted = false }, cancellationToken);
            return;
        }

        if (req.Term > _store.Term)
            await StepDownAsync(req.Term, cancellationToken); // 降级清 votedFor（落盘先于应答）

        var voted = _store.VotedFor;
        // ★ learner 不投票（件 B——candidate 多数派只数 voter，拒绝无碍当选）
        var granted = _config.IsVoter(_self)
                      && (voted == NodeId.Empty || voted == req.CandidateId)
                      && IsUpToDate(req.LastLogTerm, req.LastLogIndex, _store.LastLogTerm, _store.LastLogIndex);
        if (granted)
        {
            await _store.WriteTermAndVoteAsync(_store.Term, req.CandidateId, cancellationToken); // ★ 授权落盘先于应答
            _leaderState.Store(new LeaderSlot(0, _store.Term)); // 投票后 leader 未知（spec-01 §5.2）
            _logger?.LogInformation("Raft 授权投票：term={Term} candidate={Candidate}", _store.Term, req.CandidateId);
        }

        await ReplyAsync(reply, new RequestVoteResp { Term = _store.Term, Granted = granted }, cancellationToken);
    }

    private async ValueTask HandleRequestVoteRespAsync(NodeId from, RequestVoteResp resp,
        CancellationToken cancellationToken = default)
    {
        if (_role != RaftRole.Candidate || !_config.Contains(from)) return;
        if (resp.Term > _store.Term)
        {
            await StepDownAsync(resp.Term, cancellationToken);
            return;
        }

        if (resp.Term < _store.Term) return; // 陈旧应答——忽略
        if (resp.Granted) _voteGranted.Add(from);
        TraceElection($"VoteResp←{from.ToString()[..6]} g={resp.Granted} n={CountVoters(_voteGranted)} t={resp.Term}");
        if (Quorum.HasMajority(CountVoters(_voteGranted), _config.VoterCount))
        {
            _logger?.LogInformation("Raft 当选：term={Term} votes={Votes}/{Voters}", _store.Term,
                CountVoters(_voteGranted), _config.VoterCount);
            await BecomeLeaderAsync(cancellationToken);
        }
    }

    // ═══ RPC 处理：AppendEntries（spec-02 §3——follower 侧；持久化先于应答）═══

    /// <summary>
    /// AppendEntries 请求处理（★ 接收直排：请求回调线程调用，不经事件队列）。
    /// <para>★ 日志段互斥：term 重查 + prevLog 校验 + 追加 + 持久化 + 应答——经
    ///   <see cref="_followerAppendGate"/> 串行（检查序列不交错；等待期间 term 可能已被并发消息
    ///   推进，gate 内重查）。异常隔离：处理异常记日志不发应答——leader 在途超时重试自愈。</para>
    /// <para>★ 追加 = <see cref="IRaftStore.AppendAsync"/>（prevIndex 断言 = raft prevLog 检查一体：
    ///   匹配纯追加 / 分叉尾随断言截断）+ <see cref="IRaftStore.WaitForPersistedAsync"/>（应答前同步点）。</para>
    /// </summary>
    /// <param name="from">leader 节点。</param>
    /// <param name="req">AppendEntries 请求。</param>
    /// <param name="reply">应答上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task HandleAppendEntriesReqDirectAsync(NodeId from, AppendEntriesReq req, IReplyContext reply,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // ★ 条目区前置校验（wire v3——[Count 4B][×N: 头 13B + Content]）：计数越界 = 畸形
            //   （丢弃不应答——对端在途超时自愈）。空区 = 心跳（零条目）。区内条目解码在
            //   follower 日志门内（批缓冲 gate 串行单写者）。
            var entryCount = 0;
            if (!req.EntriesRegion.IsEmpty
                && (!RaftEntriesRegion.TryReadCount(req.EntriesRegion, out entryCount, out _)
                    || entryCount > RaftRpc.MaxEntriesPerAppend))
            {
                _logger?.LogWarning("Raft AppendEntries 条目区畸形（丢弃不应答）：from={From} len={Len}",
                    from, req.EntriesRegion.Length);
                return;
            }

            if (req.Term < _store.Term)
            {
                await ReplyAsync(reply, new AppendEntriesResp
                {
                    Term = _store.Term,
                    Success = false,
                    MatchIndex = 0,
                    ConflictTerm = 0,
                    ConflictIndex = 0,
                    SnapshotIndex = _store.SnapshotIndex,
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _followerAppendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentTerm = _store.Term;
                if (req.Term < currentTerm)
                {
                    await ReplyAsync(reply, new AppendEntriesResp
                    {
                        Term = currentTerm,
                        Success = false,
                        MatchIndex = 0,
                        ConflictTerm = 0,
                        ConflictIndex = 0,
                        SnapshotIndex = _store.SnapshotIndex,
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (req.Term > currentTerm)
                    await StepDownAsync(req.Term, cancellationToken).ConfigureAwait(false);
                else if (Role != RaftRole.Follower)
                {
                    // 同 term 合法 leader 消息 → 候选/leader 让位（spec-01 §3.2）——短临界角色转换
                    await _clusterLock.WaitAsync(cancellationToken);
                    try
                    {
                        if (Role != RaftRole.Follower)
                        {
                            ResetElectionTimer(); // ★ 新选举窗先掷（同 StepDownAsync——过期窗封锁）
                            SetRole(RaftRole.Follower);
                            _replication?.BecomeFollower();
                        }
                    }
                    finally
                    {
                        _clusterLock.Release();
                    }
                }

                // ★ 合法 leader 心跳 → 重置选举超时 + 记录 leader
                RecordLeader(from);
                ResetElectionTimer();

                // ★ 已提交区保护（乱序投递防御——raft 不变量：已提交条目不可删）：prevLogIndex 低于
                //   本地 commitIndex 的带条目请求是乱序迟到的旧批——其重写区间含已提交条目——拒绝并
                //   指向 commit+1（leader 冲突回退单调递减收敛——每拒一次 nextIndex 至少减一）
                if (entryCount > 0 && req.PrevLogIndex < _commitPair.Read().Index)
                {
                    await ReplyAsync(reply, new AppendEntriesResp
                    {
                        Term = _store.Term,
                        Success = false,
                        MatchIndex = 0,
                        ConflictTerm = 0,
                        ConflictIndex = _commitPair.Read().Index + 1,
                        SnapshotIndex = _store.SnapshotIndex,
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // ★ 二期-F2（DDR-F2）：witness 分支——无日志体，PrevLog 校验与条目持久化不适用。
                //   断言推进：Leader 前沿 = PrevLogIndex + entryCount（witness lane 恒空区+前沿锚；
                //   兼容异常带体——同样只取位置），高水位单调不回退（投票安全性论证组成）。
                //   LeaderCommit 传播照常（apply 走 no-op 状态机）。
                if (_config.IsWitness(_self))
                {
                    await _store.AssertHighWatermarkAsync(req.PrevLogIndex + entryCount, req.Term, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (!await _store.PrevLogMatchesAsync(req.PrevLogIndex, req.PrevLogTerm, cancellationToken)
                        .ConfigureAwait(false))
                {
                    // 拒绝 + conflict hint（spec-02 §4）
                    long conflictTerm = 0, conflictIndex;
                    if (req.PrevLogIndex > _store.PersistedIndex)
                    {
                        conflictIndex = _store.PersistedIndex + 1; // 日志比 leader 短
                    }
                    else if (req.PrevLogIndex <= _store.SnapshotIndex)
                    {
                        conflictIndex = _store.SnapshotIndex + 1; // 快照区——leader 应改走快照安装
                    }
                    else if (req.PrevLogIndex > _store.LastLogIndex)
                    {
                        // 防御性补全（persisted 钳制 ≤ 尾后不可达——保留使处理路径对任意 prev 全定义，
                        // 异常路径不发应答会让 leader 在途重试永不同收敛）
                        conflictIndex = _store.LastLogIndex + 1;
                    }
                    else
                    {
                        (conflictTerm, conflictIndex) =
                            await FindConflictHintAsync(req.PrevLogIndex, cancellationToken).ConfigureAwait(false);
                    }

                    _logger?.LogInformation("Raft 拒绝 AppendEntries：prev={Prev} term={Term} hint=({CT},{CI})",
                        req.PrevLogIndex, req.PrevLogTerm, conflictTerm, conflictIndex);
                    await ReplyAsync(reply, new AppendEntriesResp
                    {
                        Term = _store.Term,
                        Success = false,
                        MatchIndex = 0,
                        ConflictTerm = conflictTerm,
                        ConflictIndex = conflictIndex,
                        SnapshotIndex = _store.SnapshotIndex,
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // 匹配 → 追加 + 显式持久化（★ 应答前完成——spec-02 §3 契约）；
                // witness 不落体（内容丢弃——上面断言已推进高水位）
                if (entryCount > 0 && !_config.IsWitness(_self))
                {
                    // ★ 快照覆盖前缀钳位（T6 P1——幂等应答替代整体拒绝）：follower 自压缩把 N₀ 推过
                    //   leader 的接续点（firstNew ≤ SnapshotIndex）时，覆盖区内条目已经持久化在先
                    //   （截断协议：先快照后截头）——按"已有"钳位跳过，锚点=N₀ 续接剩余条目。
                    //   旧实现整体拒绝：leader 回退 nextIndex 重发同区→再拒循环；且自压缩中途落地
                    //   （N₀ 半路推进）使 store 读撞 head 抛异常→catch 静默无应答→leader InFlightSlot
                    //   永等→lane 停滞→心跳停→换届风暴（T6 取证实锤）。覆盖全部条目=纯幂等应答。
                    //   ★ 残余窄窗：钳位读 SnapshotIndex 之后、store 落位之前自压缩再推进 → store 抛
                    //   startIndex<head → catch 拒收应答（见 finally 前 catch）→ leader 携新 conflict
                    //   hint 重试一轮即收敛（有界自愈）。
                    var anchor = req.PrevLogIndex;
                    var coveredSkip = 0;
                    if (anchor + 1 <= _store.SnapshotIndex)
                        coveredSkip = (int)Math.Min(_store.SnapshotIndex - anchor, entryCount);
                    // ★ 条目区解码（gate 内——批缓冲复用，gate 串行单写者）：Content = 区内零拷贝切片
                    //   （store AppendAsync 落位自拷贝）；区内截断 = 畸形——不发应答（对端超时重试自愈）
                    var region = req.EntriesRegion;
                    RaftEntriesRegion.TryReadCount(region, out _, out var cursor);
                    var batch = _followerBatch;
                    batch.Clear();
                    if (batch.Capacity < entryCount) batch.Capacity = entryCount;
                    for (var i = 0; i < entryCount; i++)
                    {
                        if (!RaftEntriesRegion.TryReadEntry(region, ref cursor, out var et, out var ek, out var ec))
                        {
                            _logger?.LogWarning("Raft AppendEntries 条目区截断（畸形——丢弃不应答）：from={From} entry={Entry}",
                                from, i);
                            return;
                        }

                        batch.Add((et, ek, ec));
                    }

                    // ★ 快照覆盖前缀钳位：跳过已持久化在先的覆盖区（anchor 续接——见上钳位注释）
                    if (coveredSkip > 0) batch.RemoveRange(0, coveredSkip);
                    if (batch.Count > 0)
                    {
                        await _store.AppendAsync(anchor, batch, cancellationToken)
                            .ConfigureAwait(false); // 断言一体（分叉尾截断内建）
                        await _store.WaitForPersistedAsync(anchor + batch.Count, cancellationToken)
                            .ConfigureAwait(false); // ★ 持久化先于应答
                    }
                }

                // ★ F2：witness 不推进 commit（无状态机——apply 管道无日志体可读；高水位即
                //   其持久化语义，commit 水位对 witness 无意义）
                if (!_config.IsWitness(_self) && req.LeaderCommit > _commitPair.Read().Index)
                {
                    var newCommit = Math.Min(req.LeaderCommit, _store.PersistedIndex);
                    AdvanceCommit(newCommit);
                }

                await ReplyAsync(reply, new AppendEntriesResp
                {
                    Term = _store.Term,
                    Success = true,
                    MatchIndex = _store.PersistedIndex,
                    ConflictTerm = 0,
                    ConflictIndex = 0,
                    SnapshotIndex = _store.SnapshotIndex, // ★ 汇报 N₀（spec-03 §4——leader 据此决策快照安装）
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _followerAppendGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AppendEntries 处理异常（接收直排）：from={From} term={Term}", from, req.Term);
            // ★ 拒收应答替代静默（T6 P1）：静默=leader InFlightSlot 永等→复制 lane 停滞→心跳停
            //   →换届风暴（取证实锤）。携新状态拒收：leader 按 conflict hint 调整 nextIndex 重试
            //   一轮即收敛（有界自愈）；已应答路径不可能进本 catch（应答即 return）。
            try
            {
                await ReplyAsync(reply, new AppendEntriesResp
                {
                    Term = _store.Term,
                    Success = false,
                    MatchIndex = 0,
                    ConflictTerm = 0,
                    // ★ hint=PersistedIndex+1（非 SnapshotIndex+1）：异常路径下 follower 持有
                    //   ≤persisted 的全部数据——hint 指向近尾，leader 续传一轮收敛；指 1 会把
                    //   nextIndex 打回起点=全量重发风暴（日志洪流+重试循环，取证实锤）
                    ConflictIndex = _store.PersistedIndex + 1,
                    SnapshotIndex = _store.SnapshotIndex,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception replyEx)
            {
                _logger?.LogDebug("AppendEntries 异常路径拒收应答失败（对端已超时自愈）：from={From} message={Message}", from, replyEx.Message);   // 二期-I5：模板化纪律
            }
        }
    }

    /// <summary>主数据区冲突 hint：冲突处 term + 该 term 首条 index（递减扫描——冲突回退低频，RPC 轮数有界）。</summary>
    /// <param name="index">冲突起始 index。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>(冲突处 term, 该 term 的首条 index)。</returns>
    private async ValueTask<(long Term, long FirstIndex)> FindConflictHintAsync(long index,
        CancellationToken cancellationToken = default)
    {
        var term = await _store.ReadLogTermAsync(index, cancellationToken).ConfigureAwait(false);
        var first = index;
        while (first - 1 > _store.SnapshotIndex)
        {
            var prevTerm = await _store.ReadLogTermAsync(first - 1, cancellationToken).ConfigureAwait(false);
            if (prevTerm != term) break;
            first--;
        }

        return (term, first);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask HandleAppendEntriesRespAsync(NodeId from, AppendEntriesResp resp)
        => HandleAppendEntriesRespAsync(from, resp, CancellationToken.None);

    /// <summary>
    /// AppendEntries 应答处理（★ 复制 lane 的请求任务回投——应答到达线程调用）：
    /// 更高 term → 降级（<see cref="_clusterLock"/> 内落盘）；ReadIndex 确认计数（低频小锁）；
    /// 复制应答直投 lane（空闲可入侵直跑——<see cref="ReplicationProcess.HandleRespAsync"/>）。
    /// </summary>
    /// <param name="from">应答来源节点。</param>
    /// <param name="resp">AppendEntries 应答。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal async ValueTask HandleAppendEntriesRespAsync(NodeId from, AppendEntriesResp resp,
        CancellationToken cancellationToken)
    {
        try
        {
            if (resp.Term > _store.Term)
            {
                await StepDownAsync(resp.Term, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (Role != RaftRole.Leader) return;

            // ★ ReadIndex 批量确认（二期-B §6.1——spec-08 §4：leader 向多数派确认身份）。
            //   ★ 任期门（评审补充）：仅认 resp.Term == 本端任期——降级→重当选窗口内旧任期残留
            //     应答不计入当前轮（批量跨轮确认集前必须关门）。
            //   ★ 多数派含 leader 自身（缺陷修正——Quorum.HasMajority 契约"granted 含自己一票"；
            //     旧实现只数远端确认 = 3 节点集群要求全员确认，挂 1 非 leader 节点读即不可用）。
            //   ★ 锚点门：本任期 no-op 未提交前轮次保持挂起（新 leader commit 停在前任期水位，
            //     应答过期位点 = 线性读破约）——确认集记忆多数派，锚点提交后随 ack 重查即完成。
            if (resp.Success && resp.Term == _store.Term)
            {
                List<ReadIndexWaiter>? done = null;
                lock (_readIndexLock)
                {
                    if (_readIndexRoundActive)
                    {
                        _readIndexConfirmations.Add(from);
                        if (_commitPair.Read().Index >= Volatile.Read(ref _readIndexHoldIndex)
                            && Quorum.HasMajority(CountVoters(_readIndexConfirmations) + 1, _config.VoterCount))
                        {
                            _readIndexRoundActive = false;
                            done = new List<ReadIndexWaiter>(_readIndexWaiters);
                            _readIndexWaiters.Clear();
                        }
                    }
                }
                if (done is not null)
                    CompleteReadIndexRound(done);
            }

            if (_replication is not null)
                await _replication.HandleRespAsync(from, resp, cancellationToken).ConfigureAwait(false);

            // ★ 二期-D2：转让目标追平持久化尾 → 发 TimeoutNow（目标跳过预票立即真选举；单发）
            if (resp.Success
                && Volatile.Read(ref _transferState) is NodeId transferTo && transferTo == from
                && Interlocked.Exchange(ref _transferTimeoutNowSent, 1) == 0
                && _replication is { } repX
                && repX.TryGetMatchIndex(from, out var match) && match >= _store.PersistedIndex)
            {
                _logger?.LogInformation("Raft 转让目标已追平（match={Match}）——发 TimeoutNow：target={To}", match, from);
                _loops.SubmitFast((Func<CancellationToken, ValueTask>)(async ctk =>
                    await SendRpcAsync(from, new TransferLeaderReq { Term = _store.Term },
                        _options.ElectionTimeoutMax, ctk)));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AppendEntries 应答处理异常（lane 回投）：from={From}", from);
        }
    }
}