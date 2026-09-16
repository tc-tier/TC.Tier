using TC.Tier.Core.Net;

namespace TC.Tier.Products.Net.Queue;

/// <summary>
/// 非辖权节点异常（tierqueue-replicated-spec §3——非辖权 Dequeue/Ack 立即抛，不经共识；
/// 调用方凭 <see cref="Home"/> 直连辖权节点或启用转发档。对齐 NotLeaderException 惯例）。
/// </summary>
public sealed class NotGroupHomeException : InvalidOperationException
{
    /// <summary>当前辖权节点（Empty = 辖权未定——等 HomeCmd 落定后重试）。</summary>
    public NodeId Home { get; }

    /// <summary>涉及的消费组名。</summary>
    public string Group { get; }

    /// <summary>构造。</summary>
    /// <param name="group">消费组名。</param>
    /// <param name="home">当前辖权节点（可为 Empty——辖权未定）。</param>
    public NotGroupHomeException(string group, NodeId home)
        : base(home != NodeId.Empty
            ? $"本节点不是组 {group} 的辖权节点——当前辖权：{home}（凭 Home 直连或启用转发档）。"
            : $"本节点不是组 {group} 的辖权节点（辖权未定——等 HomeCmd 落定后重试）。")
        => (Group, Home) = (group, home);
}
