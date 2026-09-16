using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// Gossip 广播帧头（37B——<c>[MsgId 16B][Origin 16B][Ttl 1B][PayloadLength 4B]</c>，payload 紧随）。
/// MsgId = 广播身份（去重键——全网同消息至多投递一次）；Origin = 最初广播者（中继不变——
/// 投递面语义）；布局知识声明式单点（生成 codec 出偏移/读写/尺寸——零手写字节序）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 37)]
public readonly struct GossipFrameHeader
{
    /// <summary>消息身份（去重键——随机生成，环回/多路径副本由此归一）。</summary>
    [FieldOffset(0)] public readonly Opaque16 MsgId;

    /// <summary>最初广播者（中继逐跳不变）。</summary>
    [FieldOffset(16)] public readonly NodeId Origin;

    /// <summary>剩余转发跳数（每中继减一，减尽即停）。</summary>
    [FieldOffset(32)] public readonly byte Ttl;

    /// <summary>payload 字节数（紧随头之后）。</summary>
    [FieldOffset(33)] public readonly int PayloadLength;

    /// <summary>构造（参数序 = 偏移序——生成整体 Read 的契约形态）。</summary>
    /// <param name="msgId">消息身份（去重键）。</param>
    /// <param name="origin">最初广播者。</param>
    /// <param name="ttl">剩余转发跳数。</param>
    /// <param name="payloadLength">payload 字节数。</param>
    public GossipFrameHeader(Opaque16 msgId, NodeId origin, byte ttl, int payloadLength)
    {
        MsgId = msgId;
        Origin = origin;
        Ttl = ttl;
        PayloadLength = payloadLength;
    }
}
