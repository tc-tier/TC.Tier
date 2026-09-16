using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// HyParView 协议消息族（spec-06 §3 消息语义 × spec-12 §6/§10——[WireMessage] 声明即线格式）。
/// <para>★ 线格式（生成物：[Tag 1B][子类字段声明序]——族根无公共前缀字段，全部小端）：</para>
/// <code>
/// Heartbeat  [Tag 1B]
/// Disconnect [Tag 1B]
/// Join       [Tag 1B][Origin 16B][Ttl 4B]
/// Fail       [Tag 1B][Failed 16B]
/// Neighbor   [Tag 1B][Priority 1B][Count 4B][NodeId × Count]
/// Shuffle    [Tag 1B][Count 4B][NodeId × Count]
/// </code>
/// <para>★ 发送方身份不上线（spec-12 §4——连接/分发即身份，帧分发随载荷提供来源
///   <see cref="NodeId"/>；<see cref="JoinMsg.Origin"/>/<see cref="FailMsg.Failed"/>
///   是协议语义字段，非发送方身份回声）。</para>
/// <para>★ 解码防御（生成器逐字段）：未知 tag / 截断 / Count 超上限 = false（畸形报文丢弃）；
///   已知 tag 的尾部扩展字节容忍（前向兼容——旧版本解码已知前缀、忽略新增字段）。</para>
/// <para>★ 承载：数据报形态（spec-12 §6——UDP bearer 天然；协议域
///   <see cref="Wire.ProtocolIds.HyParView"/> 0x02，经内部注册口挂载）。</para>
/// </summary>
[WireMessage]
public abstract record HyParViewMessage
{
    /// <summary>变长视图/样本防御上限（被动视图有界契约——超界 = 畸形报文）。</summary>
    public const int MaxListCount = 256;
}

/// <summary>
/// JOIN 随机游走（spec-06 §3）：新节点经种子节点转发（TTL 递减）→ 沿途节点收藏其入被动视图
/// → 终点回送其活跃视图（<see cref="NeighborMsg"/> 优先路径）。
/// </summary>
[WireMessageTag(0x01)]
public sealed record JoinMsg : HyParViewMessage
{
    /// <summary>游走发起者（原始新节点——转发不变）。</summary>
    public required NodeId Origin { get; init; }

    /// <summary>剩余转发跳数（0 = 终点——回送视图）。</summary>
    public required int Ttl { get; init; }
}

/// <summary>NEIGHBOR 视图交换应答（spec-06 §3——双向建立活跃邻居；优先 = 加入终点直连）。</summary>
[WireMessageTag(0x02)]
public sealed record NeighborMsg : HyParViewMessage
{
    /// <summary>优先建立（JOIN 终点回送——接收方直接补活跃视图）。</summary>
    public required bool Priority { get; init; }

    /// <summary>发送方活跃视图样本（接收方并入被动视图）。</summary>
    [WireMember(MaxCount = MaxListCount)]
    public required NodeId[] View { get; init; }
}

/// <summary>DISCONNECT 主动离开（spec-06 §3——下电前广播；对端移出活跃视图）。</summary>
[WireMessageTag(0x03)]
public sealed record DisconnectMsg : HyParViewMessage;

/// <summary>FAIL 故障通知（spec-06 §3——故障检测触发；对端移出活跃视图、被动视图补位）。</summary>
[WireMessageTag(0x04)]
public sealed record FailMsg : HyParViewMessage
{
    /// <summary>被判定故障的节点。</summary>
    public required NodeId Failed { get; init; }
}

/// <summary>SHUFFLE 被动视图定期交换（spec-06 §2——成员搅动收敛）。</summary>
[WireMessageTag(0x05)]
public sealed record ShuffleMsg : HyParViewMessage
{
    /// <summary>发送方被动视图样本。</summary>
    [WireMember(MaxCount = MaxListCount)]
    public required NodeId[] Sample { get; init; }
}

/// <summary>心跳（PhiAccrual 采样输入——spec-06 §5）。</summary>
[WireMessageTag(0x06)]
public sealed record HeartbeatMsg : HyParViewMessage;
