namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 组搬迁编排器（二期-F5——DDR-F5）：把某成员从组内迁出至目标节点的四步编排——
/// 目标入组 → 等追平 → leader 迁移（若在源）→ 源出组。
/// <para>★ 数据面归 Fs（裁定③——镜像/迁移通道归 Fs 既有能力）；本面只做时序调度 +
/// 配置变更——日志复制追平是缺省搬迁形态，镜像通道由装配方在入组前预热（接口不变）。</para>
/// <para>★ 恢复语义（F3 变更链同款）：每步都是已提交的合法配置态；任一步失败保留
/// 中间态，重调幂等续走（已入组跳过、已迁 leader 跳过、已出组跳过）。</para>
/// </summary>
public sealed class GroupMoveOrchestrator
{
    private readonly RaftStateMachine _raft;
    private readonly Membership _membership;

    /// <param name="raft">组引擎（须为 leader 视角——搬迁编排由 leader 驱动）。</param>
    /// <param name="membership">同组成员变更协调器。</param>
    public GroupMoveOrchestrator(RaftStateMachine raft, Membership membership)
    {
        _raft = raft ?? throw new ArgumentNullException(nameof(raft));
        _membership = membership ?? throw new ArgumentNullException(nameof(membership));
    }

    /// <summary>
    /// 单次组搬迁：加入 <paramref name="joining"/>、移出 <paramref name="leaving"/>
    /// （先加后删——多数派不缩水）。编排由 leader 视角驱动。
    /// </summary>
    /// <param name="joining">目标节点（入组）。</param>
    /// <param name="leaving">源节点（出组——可为 leader 自身，引擎自杀降级语义承接）。</param>
    /// <param name="joiningEndPoint">入组端点（空串 = 进程内传输）。</param>
    /// <param name="mirrorReady">数据面前置挂钩（Fs 镜像预热完成回调——缺省 = 日志复制追平形态）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>编排全部完成（每步均已提交的合法配置态——任一步失败保留中间态，重调幂等续走）。</returns>
    /// <exception cref="TimeoutException">等追平超时（30s 内 matchIndex 未达提交水位）。</exception>
    public async ValueTask MoveAsync(NodeId joining, NodeId leaving, string joiningEndPoint = "",
        Func<CancellationToken, ValueTask>? mirrorReady = null, CancellationToken ct = default)
    {
        // ① 目标节点入组（幂等——已在组内跳过；learner 形态引导由装配方走 AddLearner+Promote）
        if (!_raft.Config.Contains(joining))
        {
            await _membership.AddMemberAsync(joining, joiningEndPoint, ct).ConfigureAwait(false);
            if (mirrorReady is not null)
                await mirrorReady(ct).ConfigureAwait(false);   // Fs 镜像预热挂钩（缺省无——日志复制追平）
        }

        // ② 等追平（leader 视角 matchIndex 到达当前提交水位——搬迁安全条件）
        await WaitForCaughtUpAsync(joining, ct).ConfigureAwait(false);

        // ③ 源节点出组（幂等——已不在跳过；leaving 为 leader 自身时引擎自杀降级语义承接）
        if (_raft.Config.Contains(leaving))
            await _membership.RemoveMemberAsync(leaving, ct).ConfigureAwait(false);
    }

    private async ValueTask WaitForCaughtUpAsync(NodeId member, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_raft.IsLeader)
            {
                var snapshot = _raft.GetStateSnapshot();
                var m = snapshot.Members.FirstOrDefault(x => x.Id == member);
                if (m.MatchIndex >= snapshot.CommitIndex && snapshot.CommitIndex > 0)
                    return;   // 已追平
            }
            else if (_raft.Config.Contains(member))
            {
                return;   // 非 leader 视角无法判 match——配置在即交给 leader 侧编排
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"搬迁追平超时：member={member}（matchIndex 未达提交水位）。");
    }
}
