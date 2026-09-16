using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Products.Net.Host;

/// <summary>
/// 组句柄（二期-C3 §9——<see cref="TierRaftHost.GetGroup"/> 返回面）：组身份 + 该组完整
/// 产品节点（TierWal/raft/apply/swarm 装配链）的读面入口。
/// </summary>
public interface ITierRaftGroup
{
    /// <summary>组标识。</summary>
    RaftGroupId GroupId { get; }

    /// <summary>该组产品节点（raft/apply/Wal/Swarm 全装配链读面）。</summary>
    TierRaftNode Node { get; }

    /// <summary>raft 状态机（复制/提交/线性读/角色面）。</summary>
    RaftStateMachine Raft { get; }

    /// <summary>当前活动配置（apply 产物——成员对账数据源）。</summary>
    ClusterConfig Config { get; }
}
