using System.Runtime.CompilerServices;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;

namespace TC.Tier.Core.Net.Raft;

// RaftStateMachine 分区（本地事件处理 + 快照安装——spec-03/04/08；全类型分区见 RaftStateMachine.cs 类头）

/// <summary>
/// raft 状态机的事件分区——本地事件（复制/读索引/配置提案）处理与快照安装/导出驱动。
/// </summary>
public sealed partial class RaftStateMachine
{
    // ═══ 本地事件处理 ═══

    private async ValueTask HandleReplicateAsync(RaftEvent.Replicate rep,CancellationToken cancellationToken=default)
    {
        if (_role != RaftRole.Leader)
        {
            if (rep.Slot is { } ns) ns.SetException(new NotLeaderException(LeaderId));
            else rep.Done!.TrySetException(new NotLeaderException(LeaderId));
            rep.Return();
            return;
        }

        // ★ 排空攒批：一次排空队列中连续的 Replicate 事件，一批追加提交——消除逐事件的
        //   （channel 读 + 单条 append 写锁往返 + 注册 + 批尾 TryPeek）机械 ×N。
        //   同循环线程单写者（无新并发面）；FlushPendingAppendsAsync 的攒批/提交判定不变
        //   （队首仍是追加类事件则继续攒——TryPeek 语义对排空后的下一事件照常成立）。
        var cap = _options.Replication.BatchSize;
        _drainEvents ??= new RaftEvent.Replicate[Math.Max(2, cap)];
        _drainEvents[0] = rep;
        var count = 1;
        while (count < _drainEvents.Length
               && _events.Reader.TryPeek(out var next) && next is RaftEvent.Replicate
               && _events.Reader.TryRead(out var consumed))
        {
            _drainEvents[count++] = (RaftEvent.Replicate)consumed;
        }

        var term = _store.Term;
        // ★ 复用批缓冲（降分配/GC 抖动——循环单写者，容量按需增长后稳态零分配）；
        //   wire v3 条目三元组直入批（kind 结构字段——命令原样字节零拷贝，无信封包装）
        if (_leaderBatch.Count < count) _leaderBatch.Capacity = count;
        _leaderBatch.Clear();
        for (var i = 0; i < count; i++)
        {
            var r = _drainEvents[i];
            _leaderBatch.Add((term, RaftEntryKind.Command, r.Command));
        }

        var startIndex = await AppendEntriesAsync(_leaderBatch, cancellationToken);
        for (var i = 0; i < count; i++)
        {
            var r = _drainEvents[i];
            if (r.Slot is { } s2) RegisterCompletion(s2, startIndex + i);
            else if (r.LeaderLocal) RegisterTcsLeaderLocalCompletion(r.Done!, startIndex + i);
            else if (r.Committed) RegisterTcsCommittedCompletion(r.Done!, startIndex + i);
            else RegisterTcsCompletion(r.Done!, startIndex + i);
            r.Return(); // 事件归还（载荷已被 store 追加拷贝）
        }
    }

    /// <summary>循环线程追加（纯追加——prevIndex = 当前已分配尾；返回批首 index）。
    /// ★ 参数放宽为 IReadOnlyList（复用批缓冲 List 直传——稳态零分配）。</summary>
    /// <param name="entries">批条目列表（(Term, Kind, Content) 三元组）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>批首条目的日志 index。</returns>
    private async ValueTask<long> AppendEntriesAsync(List<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken cancellationToken = default)
    {
        var tail = await _store.AppendAsync(_store.AllocatedIndex, entries, cancellationToken);
        return tail - entries.Count + 1;
    }

    /// <summary>配置条目提案处理（spec-04 §1——配置条目 kind 结构字段化）。
    /// <para>★ append 后不 Notify——统一由 <see cref="FlushPendingAppendsAsync"/>（提交后）收口：
    /// 未提交就通知会驱动链发空批（读不到未提交区），resp 清在途后滞后判定仍成立——空批连环。</para></summary>
    /// <param name="pc">配置提案事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask HandleProposeConfigAsync(RaftEvent.ProposeConfig pc, CancellationToken cancellationToken = default)
    {
        if (_role != RaftRole.Leader)
        {
            pc.Done.TrySetException(new NotLeaderException(LeaderId));
            return;
        }

        var startIndex = await AppendEntriesAsync([(_store.Term, RaftEntryKind.Config, pc.Config.Serialize())], cancellationToken);
        lock (_completionLock) _pending[startIndex] = pc.Done;
        _logger?.LogInformation("Raft 配置条目提案：index={Index} members={Count}", startIndex, pc.Config.Count);
    }

    /// <summary>learner 晋级处理（JoinReq.AutoPromote 登记 + 复制应答判追平——事件入队到循环线程）：
    /// 以<b>新鲜</b>活动配置出角色切换提案（lane 线程不读配置出提案——并发提案丢更新判例）。
    /// 无完成等待者——提交/应用随多数派推进自然完成，加入方轮询 <see cref="RaftStateMachine.IsVoter"/>/// </summary>
    /// <param name="pl">learner 晋级事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask HandlePromoteLearnerAsync(RaftEvent.PromoteLearner pl, CancellationToken cancellationToken = default)
    {
        if (_role != RaftRole.Leader) return; // 换届后残留事件——新 leader 由加入方重递 JoinReq 自愈
        var next = Config.Promote(pl.Member);
        if (next == Config) return; // 已是 voter/已缺席（幂等）
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        // ★ 受控丢弃豁免（TCSG138——显式观测形态）：ContinueWith(OnlyOnFaulted) 消费 fault 并告警，
        //   续体即观察面（未提交的 learner 晋级提案失败不外泄——加入方轮询自愈，见上注）。
#pragma warning disable TCSG138 // 设计必需：fault 已被续体观测（OnlyOnFaulted 日志），非静默吞
        _ = tcs.Task.ContinueWith(
#pragma warning restore TCSG138
            t => _logger?.LogWarning("Raft learner 晋级提案未提交：member={Member} 原因={Reason}", pl.Member,
                t.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);
        if (!_events.Writer.TryWrite(new RaftEvent.ProposeConfig(next, tcs)))
            await _events.Writer.WriteAsync(new RaftEvent.ProposeConfig(next, tcs),cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Raft learner 晋级提案：member={Member}", pl.Member);
    }

    /// <summary>加入集群请求处理（leader 面）：缺席 = 提案 learner 入组（角色切换走
    /// <see cref="HandlePromoteLearnerAsync"/>）；<see cref="JoinReq.AutoPromote"/> 登记追平晋级意图。
    /// 非 leader = 不受理，应答回已知 leader 提示（加入方轮转 bootstrap /// </summary>
    /// <param name="from">加入方节点。</param>
    /// <param name="req">加入请求。</param>
    /// <param name="reply">应答上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask HandleJoinReqAsync(NodeId from, JoinReq req, IReplyContext reply,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0)
        {
            await ReplyAsync(reply,
                    new JoinResp { Term = _store.Term, Accepted = false, LeaderId = LeaderId ?? NodeId.Empty },cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var config = Config;
        if (!config.Contains(req.CandidateId))
            await ProposeConfigAsync(config.AddLearner(req.CandidateId), cancellationToken).ConfigureAwait(false);
        if (req.AutoPromote)
            lock (_leaseLock)
            {
                if (_pendingPromote.TryAdd(req.CandidateId, 1))
                    Interlocked.Increment(ref _pendingPromoteCount);
            }

        _logger?.LogInformation("Raft 加入请求受理：candidate={Candidate} autoPromote={AutoPromote}", req.CandidateId,
            req.AutoPromote);
        await ReplyAsync(reply, new JoinResp { Term = _store.Term, Accepted = true, LeaderId = _self },cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>批复制处理（spec-08 §1——一批一次追加；批尾 index 完成 tcs）。
    /// <para>★ append 后不 Notify——统一由 <see cref="FlushPendingAppendsAsync"/>（提交后）收口（同
    /// HandleProposeConfigAsync 注释：未提交通知驱动链发空批连环）。</para></summary>
    /// <param name="batch">批复制事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask HandleReplicateBatchAsync(RaftEvent.ReplicateBatch batch, CancellationToken cancellationToken = default)
    {
        if (_role != RaftRole.Leader)
        {
            batch.Done.TrySetException(new NotLeaderException(LeaderId));
            return;
        }

        // ★ 复用批缓冲（同 HandleReplicateAsync——降分配/GC 抖动）
        _leaderBatch.Clear();
        foreach (var t in batch.Commands)
            _leaderBatch.Add((_store.Term, RaftEntryKind.Command, t));
        var startIndex = await AppendEntriesAsync(_leaderBatch, cancellationToken);
        lock (_completionLock) _pending[startIndex + _leaderBatch.Count - 1] = batch.Done;
    }

    /// <summary>
    /// 线性读处理（spec-08 §4 + 二期-B §6.1 批量化）：并入 waiter 批量轮次——无在途轮次 =
    /// 开新轮（清确认集、置在途、立即推心跳多数派确认身份）；在途 = 直接入队（本轮完成时
    /// 一并拿到当时的 commitIndex——commit 单调性保证 ≥ 各自入队时水位）。
    /// Standalone（N=1）：多数派 = 自己——立即返回。并发读不再相互覆盖（单槽缺陷根治）。
    /// </summary>
    /// <param name="ri">线性读事件（含完成源）。</param>
    private void HandleReadIndex(RaftEvent.ReadIndex ri)
    {
        if (_role != RaftRole.Leader)
        {
            ri.Done.TrySetException(new NotLeaderException(LeaderId));
            return;
        }

        if (_config.Count == 1)
        {
            ri.Done.TrySetResult(_commitPair.Read().Index); // ★ Standalone 快捷路径（多数派 = 自己）
            return;
        }

        EnqueueReadIndexWaiter(new ReadIndexWaiter(ri.Done, null, default));
    }

    /// <summary>并入批量轮次（循环线程入队 / 转发处理共享——_readIndexLock 串行；无在途轮次 =
    /// 开新轮：清确认集、置在途、立即推心跳——确认往返一轮）。</summary>
    private void EnqueueReadIndexWaiter(ReadIndexWaiter waiter)
    {
        lock (_readIndexLock)
        {
            _readIndexWaiters.Add(waiter);
            if (!_readIndexRoundActive)
            {
                _readIndexRoundActive = true;
                _readIndexConfirmations = [];
            }
        }

        if (_replication is { } rep) // CA2012：ValueTask 直 await
            rep.HandleTick(); // 立即心跳——确认往返一轮
    }

    private void HandleConfigChanged(ClusterConfig config, CancellationToken cancellationToken = default)
    {
        _clusterLock.Wait(cancellationToken);
        try
        {
            _config = config;
            RebuildMemberOrder(config);
            _logger?.LogInformation("Raft 配置切换：members={Count} self={Self} in={In}", config.Count, _self,
                config.Contains(_self));
            if (_role == RaftRole.Leader)
            {
                if (!config.Contains(_self))
                {
                    // ★ 被移除 leader 自杀（spec-04 §4）：立即降级——停止复制/心跳 + 不再参与选举
                    //   （★ _removedFromConfig 必置——否则被移除 leader 仍可发起竞选并把自己重新选上：
                    //   幸存者授权无成员资格门禁、其自身预票按新配置多数派计数——实锤楔死判例）
                    _logger?.LogInformation("Raft leader 被移除——自杀降级");
                    _removedFromConfig = true;
                    SetRole(RaftRole.Follower);
                    _replication?.BecomeFollower();
                    _leaderState.Store(new LeaderSlot(0, _store.Term));
                    LeaderChanged?.Invoke(false);
                }
                else
                {
                    _replication?.ApplyConfig(config);
                }
            }
            else if (!config.Contains(_self))
            {
                // 配置外节点：不参与选举（超时事件忽略——HandleElectionTimeout 首行判定）
                _removedFromConfig = true;
            }
            else
            {
                _removedFromConfig = false;
            }
        }
        finally
        {
            _clusterLock.Release();
        }
    }

    /// <summary>commitIndex 推进（Leader 判定 / Follower 跟随——单调不减）→ 完成 committed 档等待者
    /// + 投递 apply 管道 + 事件。
    /// <para>★ 复合 CAS：(commitIndex, term) 对整体原子替换——推进瞬间 term 快照入对，
    /// 期间换届（term 已变）则 CAS 失败重读放弃——Raft §5.4.2 不能提交旧 term
    /// 条目的安全对语义，不依赖 leader-term 不变式。多 lane 并发上报安全（CAS 循环只让
    /// 推进赢家完成等待者/投递 apply/事件，重复上报幂等跳过）。</para></summary>
    /// <param name="newCommit">新提交 index（单调不减）。</param>
    private void AdvanceCommit(long newCommit)
    {
        while (true)
        {
            var current = _commitPair.Read();
            if (newCommit <= current.Index) return;
            if (!_commitPair.TryCompareExchange(current, new CommitPair(newCommit, _store.Term)))
                continue; // 并发推进或换届——重读重试（旧 term 的过期推进自然被拒）
            CompleteCommitted(newCommit);
            _apply.Submit(newCommit);
            EntryCommitted?.Invoke(newCommit);
            _raftView?.OnCommitAdvanced(newCommit);   // 二期-I2
            return;
        }
    }

    /// <summary>记录已知 leader（接收直排/gate 内调用——复合对原子发布，任意线程读无锁）。
    /// 同时续约 leader 心跳计时（PreVote 在位否决判活——见 HandlePreVoteReqAsync）。</summary>
    /// <param name="leader">已知 leader 节点 ID。</param>
    private void RecordLeader(NodeId leader)
    {
        Volatile.Write(ref _lastLeaderContactTicks, Environment.TickCount64);
        _leaderState.Store(new LeaderSlot(SeqOf(leader), _store.Term));
    }

    /// <summary>成员序（成员表序 i+1；不在表 = 0 未知——配置切换窗口的旧 seq 反查自然失配为 null）。</summary>
    private long SeqOf(NodeId id)
    {
        var order = _memberOrder;
        for (var i = 0; i < order.Length; i++)
            if (order[i] == id)
                return i + 1;
        return 0;
    }

    /// <summary>本节点成员序（当选时快照——clusterLock 内 _config 稳定）。</summary>
    private long SelfSeq() => SeqOf(_self);

    /// <summary>授权集合中的 voter 票数（learner 不计——件 B 多数派口径；learner 不投票但
    /// 防御式过滤——旧任期遗留集合内容不污染计票）。</summary>
    private int CountVoters(HashSet<NodeId> granted)
    {
        var n = 0;
        foreach (var id in granted)
            if (_config.IsVoter(id))
                n++;
        return n;
    }

    /// <summary>成员序表重建（配置装配/切换时——copy-on-write volatile 发布）。</summary>
    private void RebuildMemberOrder(ClusterConfig config)
    {
        var order = new NodeId[config.Count];
        var i = 0;
        foreach (var m in config.Members) order[i++] = m.Id;
        _memberOrder = order;
    }

    // ═══ 快照安装（spec-03——leader 导出 + 握手 RPC；follower 导入 + 重建）═══
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask OnSnapshotNeededAsync(NodeId peer) => OnSnapshotNeededAsync(peer, CancellationToken.None);

    /// <summary>
    /// leader 侧快照安装（spec-03 §2）：导出（注入面——装配绑定目标；★快照创建=实现侧策略，
    /// 完成时 <see cref="IRaftStore.SnapshotIndex"/> 已反映导出点）→ InstallSnapshotReq 握手 →
    /// follower 导入重建后应答 → nextIndex = N₀+1 推增量。
    /// <para>★ 防重入走并发集合；每轮尝试（成功/失败）后进 per-peer 冷却窗——安装导出 = 全镜像读
    /// +块化+握手（重活），边界未解除时无冷却 = lane 周期级满速重跑烧 CPU（MultiNode@20 实测）；
    /// 冷却窗内边界未解除由心跳节拍兜底重驱。</para>
    /// </summary>
    /// <param name="peer">需要安装快照的 follower 节点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask OnSnapshotNeededAsync(NodeId peer, CancellationToken cancellationToken)
    {
        // ★ lane 链线程调用：角色判定走 volatile 发布、防重入走并发集合
        if (Role != RaftRole.Leader || !_installingSnapshots.TryAdd(peer, 0)) return;
        try
        {
            var now = Environment.TickCount64;
            if (_installRetryAfter.TryGetValue(peer, out var retryAfter) && now < retryAfter) return; // 冷却窗
            _logger?.LogInformation("Raft 快照安装开始：peer={Peer}", peer);
            if (_snapshotTransfer is null)
            {
                _logger?.LogWarning("快照传输未装配——跳过快照安装（nextIndex 停滞）：peer={Peer}", peer);
                return;
            }

            // 1. 导出（快照创建=实现侧策略——完成时 SnapshotIndex 已反映导出点；快照期间 Append 继续，增量随后正常推）
            //    协调数据随握手面行（swarm 形态 manifest+holders；流式形态 null = RPC 协调字段零值）
            var coordination = await _snapshotTransfer.ExportSnapshotAsync(peer, cancellationToken);
            var n0 = _store.SnapshotIndex;
            // 2. 握手 RPC（应答到达后转 HandleInstallSnapshotRespAsync——超时/失败移除标记重试）
            var timeout = TimeSpan.FromMilliseconds(2 * _options.ElectionTimeoutMax.TotalMilliseconds);
            var resp = await SendRpcAsync(peer, new InstallSnapshotReq
            {
                Term = _store.Term,
                LeaderId = _self,
                SnapshotIndex = n0,
                Swarm = coordination is not null,
                ManifestId = coordination?.ManifestId ?? default,
                BlockSize = coordination?.BlockSize ?? 0,
                TotalBytes = coordination?.TotalBytes ?? 0,
                Checksums = coordination?.Checksums ?? [],
                Holders = coordination?.Holders ?? [],
            }, timeout, _loopCts?.Token ?? cancellationToken);
            if (resp is InstallSnapshotResp installResp)
                await HandleInstallSnapshotRespAsync(peer, installResp, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Raft 快照导出失败：peer={Peer}", peer);
        }
        finally
        {
            // ★ 标记移除 + 冷却窗统一收口（成功/失败/超时/StepDown 全路径——原失败点零散 TryRemove
            //   且 StepDown 路径漏移除标记=该 peer 安装永久停滞）
            _installingSnapshots.TryRemove(peer, out _);
            _installRetryAfter[peer] = Environment.TickCount64 + InstallRetryIntervalMs;
        }
    }

    /// <summary>
    /// follower 侧 InstallSnapshot（spec-03 §2）：导入（注入面——事务式段写）→ 尾缓存刷新 →
    /// 业务状态重建（<see cref="IRebuildableApplySink"/>）→ commitIndex 跳到覆盖点 → 应答。
    /// <para>★ <see cref="_followerAppendGate"/> 内：导入/重锚与 AppendEntries 直排的日志操作
    /// 互斥（等待期间 term 可能已推进，gate 内重查）。</para>
    /// </summary>
    /// <param name="from">leader 节点。</param>
    /// <param name="req">InstallSnapshot 请求。</param>
    /// <param name="reply">应答上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask HandleInstallSnapshotReqAsync(NodeId from, InstallSnapshotReq req, IReplyContext reply,
        CancellationToken cancellationToken = default)
    {
        await _followerAppendGate.WaitAsync(cancellationToken);
        try
        {
            if (req.Term < _store.Term)
            {
                _logger?.LogWarning("Raft 拒绝 InstallSnapshot：reqTerm={Req} myTerm={My}", req.Term, _store.Term);
                await ReplyAsync(reply,
                    new InstallSnapshotResp { Term = _store.Term, Success = false, SnapshotIndex = 0 },cancellationToken);
                return;
            }

            if (req.Term > _store.Term)
                await StepDownAsync(req.Term, cancellationToken);
            else if (_role != RaftRole.Follower)
            {
                await _clusterLock.WaitAsync(cancellationToken);
                try
                {
                    if (_role != RaftRole.Follower)
                    {
                        SetRole(RaftRole.Follower);
                        _replication?.BecomeFollower();
                    }
                }
                finally
                {
                    _clusterLock.Release();
                }
            }

            RecordLeader(from);
            ResetElectionTimer();

            if (_snapshotTransfer is null)
                throw new InvalidOperationException("快照传输未装配——无法导入。");
            // ★ 陈旧安装守卫（重发/乱序应答丢失重试）：本地快照已 ≥ 请求覆盖点——跳过导入直接应答
            //   （重导入会把主数据重锚回旧 N₀——已追平的增量被清，虽可经复制自愈但纯浪费）
            if (req.SnapshotIndex <= _store.SnapshotIndex)
            {
                _logger?.LogInformation("Raft 跳过陈旧快照安装：req={Req} local={Local}", req.SnapshotIndex,
                    _store.SnapshotIndex);
                await ReplyAsync(reply, new InstallSnapshotResp
                {
                    Term = _store.Term,
                    Success = true,
                    SnapshotIndex = _store.SnapshotIndex,
                },cancellationToken);
                return;
            }

            // 1. 导入（事务式——失败回滚旧快照完好）+ ★ 主数据重锚（导入内建——追加位对齐 N₀+1，
            //    全局 index 续接）；握手面协调数据重建（swarm 形态——单源流式 = null）
            var coordination = req.Swarm
                ? new SnapshotCoordination
                {
                    ManifestId = req.ManifestId,
                    BlockSize = req.BlockSize,
                    TotalBytes = req.TotalBytes,
                    Checksums = req.Checksums,
                    Holders = req.Holders,
                }
                : null;
            await _snapshotTransfer.ImportSnapshotAsync(from, coordination, cancellationToken);
            // ★ 导入后尾缓存/term 索引刷新（存储已重锚——尾缓存整体作废重建）
            await _store.RefreshTailAfterImportAsync(cancellationToken);
            // 2. 业务状态重建（appliedIndex → N₀；配置条目随重建恢复）
            if (_apply is IRebuildableApplySink rebuildable)
                await rebuildable.RebuildFromSnapshotAsync(cancellationToken);
            // 3. commitIndex 跳到快照覆盖点（★ 快照 = 已提交数据——论文 InstallSnapshot 语义；
            //    重建后 appliedIndex=N₀ → Submit(N₀) 无重复应用）
            if (req.SnapshotIndex > _commitPair.Read().Index)
                AdvanceCommit(req.SnapshotIndex);
            // ★ 二期-B §6.3：重建不发 AppliedTo——显式推 applied 水位并完成 ≤ N₀ 的 waiter
            OnSnapshotRebuilt(req.SnapshotIndex);
            _raftView?.OnSnapshotInstalled(req.SnapshotIndex);   // 二期-I2

            _logger?.LogInformation("Raft 快照安装完成：n0={N0}", req.SnapshotIndex);
            await ReplyAsync(reply, new InstallSnapshotResp
            {
                Term = _store.Term,
                Success = true,
                SnapshotIndex = req.SnapshotIndex,
            },cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Raft 快照导入失败（旧快照完好——重试）：n0={N0} type={Type} msg={Msg}",
                req.SnapshotIndex, ex.GetType().Name, ex.Message);
            await ReplyAsync(reply, new InstallSnapshotResp { Term = _store.Term, Success = false, SnapshotIndex = 0 },cancellationToken);
        }
        finally
        {
            _followerAppendGate.Release();
        }
    }

    private async ValueTask HandleInstallSnapshotRespAsync(NodeId from, InstallSnapshotResp resp,
        CancellationToken cancellationToken = default)
    {
        if (resp.Term > _store.Term)
        {
            await StepDownAsync(resp.Term, cancellationToken);
            return;
        }

        if (Role != RaftRole.Leader) return;
        _installingSnapshots.TryRemove(from, out _);
        if (resp.Success)
        {
            // ★ nextIndex = N₀+1 → 增量追平（spec-03 §4——快照与复制并行，零阻塞）
            _replication?.CompleteInstall(from, resp.SnapshotIndex);
        }
        else
        {
            _logger?.LogWarning("Raft 快照安装被拒：peer={Peer}（重试）", from);
        }
    }
}