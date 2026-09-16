namespace TC.Tier.Core.Net.Raft;

/// <summary>非 Leader 写入异常（spec-08 §1——ReplicateAsync 非 Leader 立即抛，不等待超时；调用方经 LeaderId 重路由）。</summary>
public sealed class NotLeaderException : InvalidOperationException
{
    /// <summary>当前已知 leader（可能为空——未知/选举中）。</summary>
    public NodeId? Leader { get; }

    /// <summary>构造。</summary>
    /// <param name="leader">当前已知 leader（可为空）。</param>
    public NotLeaderException(NodeId? leader = null)
        : base(leader is { } l ? $"本节点不是 leader——当前 leader：{l}。" : "本节点不是 leader。")
        => Leader = leader;
}
