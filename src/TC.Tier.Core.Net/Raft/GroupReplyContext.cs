using System.Buffers.Binary;
using TC.Tier.Core.Net.Channels;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 组应答包装上下文（二期-C1 §4.1——组通道入站请求的 <see cref="IReplyContext"/> 视图）：
/// <see cref="ReplyAsync"/> 在引擎应答外层补写 <c>[GroupId 8B]</c> 前缀后经原上下文回写；
/// <see cref="Peer"/> / <see cref="ProtocolId"/> / <see cref="CorrelationId"/> 透传（回程路由
/// 知识在介质——host/组通道零回程知识）。分配开销（8+len 拷贝）先正确后优化（§13.2——
/// 池化/分段写后置）。
/// </summary>
internal sealed class GroupReplyContext(IReplyContext inner, RaftGroupId groupId) : IReplyContext
{
    public NodeId Peer => inner.Peer;

    public byte ProtocolId => inner.ProtocolId;

    public ulong CorrelationId => inner.CorrelationId;

    public async ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var framed = new byte[RaftGroupHost.GroupPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(framed, groupId.Value);
        payload.Span.CopyTo(framed.AsSpan(RaftGroupHost.GroupPrefixBytes));
        await inner.ReplyAsync(framed, ct);
    }
}
