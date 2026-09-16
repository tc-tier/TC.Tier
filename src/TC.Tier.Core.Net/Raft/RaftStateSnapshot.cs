using TC.Tier.Core.Net.Transport;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 成员复制状态（二期-D1 状态导出——leader 视角的复制进度；非 leader 节点该成员表为空/零值）。
/// </summary>
/// <param name="Id">成员节点 ID。</param>
/// <param name="Role">配置角色（Voter/Learner）。</param>
/// <param name="MatchIndex">已确认匹配的日志尾（leader 视角——复制进度；非 leader = 0）。</param>
/// <param name="Lag">复制滞后（leader commit − match；非 leader = 0）。</param>
public readonly record struct RaftMemberState(NodeId Id, ClusterMemberRole Role, long MatchIndex, long Lag);

/// <summary>
/// 统一状态导出快照（二期-D1 NETGAP-005——健康/就绪/管理面的数据面）：
/// Role/LeaderId/Term/CommitIndex/AppliedIndex/日志尾/快照覆盖点/成员与复制进度 + healthz/readyz 语义。
/// <para>★ readyz 语义：<see cref="Ready"/> = 本端角色为 Leader 或已知 leader（LeaderId 非空）——
/// 分区/选举中 = not ready（探针语义：不返回过期承诺）。</para>
/// <para>★ healthz 语义：<see cref="Healthy"/> = 循环存活（无 LoopException）且已启动——停摆/故障 = unhealthy。</para>
/// </summary>
/// <param name="Self">本端节点 ID。</param>
/// <param name="Role">当前角色。</param>
/// <param name="Term">当前任期。</param>
/// <param name="LeaderId">当前已知 leader（未知 = null）。</param>
/// <param name="CommitIndex">提交水位。</param>
/// <param name="AppliedIndex">应用水位。</param>
/// <param name="LastLogIndex">日志尾。</param>
/// <param name="SnapshotIndex">快照覆盖点。</param>
/// <param name="IsVoter">是否投票成员（learner = false）。</param>
/// <param name="Healthy">healthz——循环存活且已启动。</param>
/// <param name="Ready">readyz——已知 leader（含自身在位）。</param>
/// <param name="Members">成员与复制进度（leader 视角——各成员 match/lag；非 leader = 空）。</param>
/// <param name="GroupId">组标识（二期-I6——多组装配区分；单组 = Empty）。</param>
public sealed record RaftStateSnapshot(
    NodeId Self,
    RaftRole Role,
    long Term,
    NodeId? LeaderId,
    long CommitIndex,
    long AppliedIndex,
    long LastLogIndex,
    long SnapshotIndex,
    bool IsVoter,
    bool Healthy,
    bool Ready,
    IReadOnlyList<RaftMemberState> Members,
    RaftGroupId GroupId = default);
