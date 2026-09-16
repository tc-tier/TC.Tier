namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// Swarm 内容域（#436 件四——持有表按域隔离）：同一节点 raft 快照与 Blob 分发共存一套
/// <see cref="SwarmSync"/> 时互不误伤——raft 换届清表只清 <see cref="Raft"/> 域。
/// </summary>
public enum SwarmContentDomain : byte
{
    /// <summary>raft 快照基线域（Announce 上报 / 换届清表的既有语义）。</summary>
    Raft = 0,

    /// <summary>通用内容域（Blob 段分发——无 leader 概念，注册/查询显式按域）。</summary>
    Content = 1,
}
