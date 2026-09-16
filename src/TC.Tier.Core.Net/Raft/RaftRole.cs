namespace TC.Tier.Core.Net.Raft;

/// <summary>节点角色（spec-01 §3.1 状态集合——Follower/PreCandidate/Candidate/Leader）。</summary>
public enum RaftRole
{
    /// <summary>被动——处理 RPC；选举超时 → PreCandidate。</summary>
    Follower,

    /// <summary>★ PreVote 阶段（spec-01 §3.3）——发 PreVoteReq（不抬任期、零持久化）；多数派预票 → Candidate。</summary>
    PreCandidate,

    /// <summary>任期+1 并落盘后广播 RequestVote；多数派 → Leader。</summary>
    Candidate,

    /// <summary>发心跳/复制；收到更高 term → Follower。</summary>
    Leader,
}
