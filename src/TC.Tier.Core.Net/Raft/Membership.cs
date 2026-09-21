namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 成员变更协调器（spec-04 §2 定案——single-server 变更：一次一个节点，API 层串行化）。
/// <list type="bullet">
/// <item><b>加节点</b>：提案配置条目 [C_old ∪ {new}]（旧多数派提交）→ apply 切换活动配置
///   → 新节点经快照/增量追赶（追赶期无投票权——配置驱动成员关系，spec-04 §3）。</item>
/// <item><b>移除节点</b>：提案配置条目 [C_new]（移除）→ apply 切换 → 被移除 leader 自杀降级
///   （状态机 HandleConfigChanged——spec-04 §4）。</item>
/// <item><b>串行化</b>（spec-08 §7）：并发 Add/Remove 排队执行——一次一个变更（joint consensus
///   不做——论文原话 single-server"更简单"，spec-04 §5 定案）。</item>
/// </list>
/// <para>★ 配置切换 = apply 产物（日志即状态机）：提案返回 = committed 且 applied——返回时
/// 活动配置已切换（经 ApplyPipeline 配置条目分流 → 状态机 ConfigChanged）。</para>
/// </summary>
public sealed class Membership : IDisposable
{
    private readonly RaftStateMachine _machine;
    private readonly SemaphoreSlim _gate = new(1, 1);   // 变更串行化（一次一个 single-server 变更）

    /// <summary>构造。</summary>
    /// <param name="machine">raft 状态机（提案经 leader；配置观测）。</param>
    public Membership(RaftStateMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        _machine = machine;
    }

    /// <summary>
    /// 加成员（spec-04 §2 第一步）：提案 [当前配置 ∪ {member}]——旧多数派提交后 apply 生效。
    /// 已存在 = 幂等返回。
    /// </summary>
    /// <param name="member">新成员节点 ID。</param>
    /// <param name="endPoint">端点（进程内传输 = 空串；网络 = host:port——装配层消费）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时配置条目已 committed 且 applied——活动配置已含新成员（voter）。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（无权提案）。</exception>
    public async Task AddMemberAsync(NodeId member, string endPoint = "", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _machine.Config;
            if (current.Contains(member)) return;   // 幂等
            await ProposeAsync(current.Add(member, endPoint), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 加 learner（观察副本/引导形态）：提案 [当前配置 ∪ {member(learner)}]——收日志复制可读，
    /// 不投票不计多数派。已存在 = 幂等返回（无论现角色）。
    /// </summary>
    /// <param name="member">新 learner 节点 ID。</param>
    /// <param name="endPoint">端点（进程内传输 = 空串；网络 = host:port——装配层消费）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时配置条目已 committed 且 applied——活动配置已含新 learner。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（无权提案）。</exception>
    public async Task AddLearnerAsync(NodeId member, string endPoint = "", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _machine.Config;
            if (current.Contains(member)) return;   // 幂等
            await ProposeAsync(current.AddLearner(member, endPoint), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 批量加 learner（#507c——机群批量引导：一条配置条目携多节点，N 节点入队 = O(1) 轮协议）。
    /// <para>★ 安全性：learner 不投票不计多数派——批量加入不改变投票成员集，quorum 交集论证平凡成立
    ///   （<see cref="ClusterConfig.AddLearners"/> 头注释）。单条上限 200（wire Count 1B）——更大批次
    ///   在本 API 内自动分多条批量条目顺序提交（每条都是合法配置快照，串行化门保证无并发交错）。</para>
    /// <para>★ 幂等：已存在的成员跳过；全部已存在 = 零提案直接返回。</para>
    /// </summary>
    /// <param name="learners">批量 learner（ID + 端点；端点空串 = 进程内传输）。</param>
    /// <param name="cancellationToken">取消令牌（提交中取消——已提交条目不回退，重调续走）。</param>
    /// <returns>完成时批量条目已 committed 且 applied——活动配置已含全部新 learner。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（无权提案）。</exception>
    public async Task AddLearnersAsync(
        IEnumerable<(NodeId Id, string EndPoint)> learners, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(learners);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 分批（每条 ≤200——wire 上限余量）：每批前重读活动配置（幂等收敛——前批已入的跳过）
            var batch = new List<(NodeId Id, string EndPoint)>(200);
            foreach (var l in learners)
            {
                if (_machine.Config.Contains(l.Id)) continue;   // 幂等（含本批前序条目已提交的）
                batch.Add(l);
                if (batch.Count == 200)
                {
                    await ProposeAsync(_machine.Config.AddLearners(batch), cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                await ProposeAsync(_machine.Config.AddLearners(batch), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 加 witness（见证者——投票计入选主/提交多数派，不存日志体不自荐、永不晋级；
    /// 三期-F2 装配面便捷操作）：提案 [当前配置 ∪ {member(witness)}]。已存在 = 幂等返回。
    /// </summary>
    /// <param name="member">新 witness 节点 ID。</param>
    /// <param name="endPoint">端点（进程内传输 = 空串；网络 = host:port——装配层消费）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时配置条目已 committed 且 applied——活动配置已含新 witness。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（无权提案）。</exception>
    public async Task AddWitnessAsync(NodeId member, string endPoint = "", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _machine.Config;
            if (current.Contains(member)) return;   // 幂等
            await ProposeAsync(current.AddWitness(member, endPoint), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 晋级 voter（learner → voter——引导流追平后转正）：提案角色切换，返回 = 已提交生效。
    /// 缺席或已是 voter = 幂等返回。
    /// </summary>
    /// <param name="member">要晋级的节点 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时角色切换条目已 committed 且 applied——该成员已转为 voter。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（无权提案）。</exception>
    public async Task PromoteAsync(NodeId member, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _machine.Config;
            if (!current.Contains(member) || current.IsVoter(member)) return;   // 幂等
            await ProposeAsync(current.Promote(member), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 移除成员（spec-04 §2 第二步）：提案 [当前配置 \ {member}]。被移除者是 leader 时其自杀降级
    /// （spec-04 §4）；不存在的成员 = 幂等返回；移除最后一名成员被拒。
    /// </summary>
    /// <param name="member">要移除的节点 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="NotLeaderException">本节点非 Leader（无权提案）。</exception>
    /// <exception cref="InvalidOperationException">试图移除最后一名成员（集群将无多数派）。</exception>
    /// <returns>完成时配置条目已 committed 且 applied——活动配置已移除该成员。</returns>
    public async Task RemoveMemberAsync(NodeId member, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _machine.Config;
            if (!current.Contains(member)) return;   // 幂等
            if (current.Count <= 1)
                throw new InvalidOperationException("不能移除最后一名成员（集群将无多数派）。");
            await ProposeAsync(current.Remove(member), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 多成员原子变更（二期-F3——DDR-F3 编排式变更链，etcd 生产同款）：一次调用把成员集
    /// 变更到 <paramref name="target"/>——先增后删的单步变更链（链上每个中间配置都是已提交
    /// 的合法配置，安全性由单步变更保证、原子性由编排保证，论文 §6 认可备选）。
    /// <para>★ 幂等可续：每步前重读活动配置（已达成目标形态的成员跳过）——中途失败/换届/
    /// 重启后重调本 API 即从当时配置续走，最终收敛目标集。目标 = 当前 = 零提案。</para>
    /// </summary>
    /// <param name="target">目标成员集（voter+witness 全集形态——learner 角色变更走 AddLearner/Promote）。</param>
    /// <param name="cancellationToken">取消令牌（链中断——已提交步骤不回退，重调续走）。</param>
    /// <exception cref="NotLeaderException">本节点非 Leader（无权提案——重路由后重调）。</exception>
    /// <exception cref="InvalidOperationException">变更链将移除最后一名成员（集群将无多数派）。</exception>
    /// <returns>变更链全部完成（先增后删的每一步均已 committed 且 applied——活动配置收敛为 target）。</returns>
    public async Task ChangeMembersAsync(
        IEnumerable<(NodeId Id, string EndPoint)> target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 先增后删（DDR-F3 顺序裁定）：增员不缩多数派、删员在增员生效后执行——
            // 链上中间态多数派 ≥ min(当前, 目标)
            foreach (var (id, endPoint) in target)
            {
                var current = _machine.Config;   // 每步重读（apply 切换后的活动配置）
                if (current.Contains(id)) continue;   // 幂等
                await ProposeAsync(current.Add(id, endPoint), cancellationToken).ConfigureAwait(false);
            }
            var keep = target.Select(t => t.Id).ToHashSet();
            foreach (var m in _machine.Config.Members.ToArray())   // 快照遍历（配置在切换中变化）
            {
                if (!keep.Contains(m.Id))
                {
                    var current = _machine.Config;
                    if (!current.Contains(m.Id)) continue;   // 已被前序删除
                    if (current.Count <= 1)
                        throw new InvalidOperationException("不能移除最后一名成员（集群将无多数派）。");
                    await ProposeAsync(current.Remove(m.Id), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>提案配置条目（仅 leader——非 leader 快速失败；返回 = committed 且 applied = 配置已切换）。</summary>
    private async ValueTask ProposeAsync(ClusterConfig next, CancellationToken cancellationToken=default)
    {
        if (!_machine.IsLeader)
            throw new NotLeaderException(_machine.LeaderId);
        await _machine.ProposeConfigAsync(next, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 释放资源（串行化信号量）。
    /// </summary>
    public void Dispose()
    {
        _gate.Dispose();
    }
}
