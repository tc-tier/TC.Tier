using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 协议域 ID 注册表——内部核心区常量 + 五区制区间助手（spec-12 §3.5；[ConstantRegistry] 派生，
/// 重复值编译期 TCSG040 拦截）。
/// <para>★ Net 零产品知识：内部核心区精确等于 Core.Net 自有的号；第一方产品（TierQueue/TierKV…）
///   与第三方、测试同为一类<b>使用方</b>——在注册区 0x60-0xAF 自己拿号、自己文档化，不进本表。</para>
/// </summary>
[ConstantRegistry(Zones = new[] { "IsCore:0x00-0x4F", "IsUserRegistrable:0x60-0xAF", "IsForbidden:0x50-0x5F,0xB0-0xFF" })]
public static partial class ProtocolIds
{
    /// <summary>管理/错误——Core.Net 自身。</summary>
    public const byte Management = 0x00;

    /// <summary>Raft 共识（spec-01..05 消息）。</summary>
    public const byte Raft = 0x01;

    /// <summary>HyParView 成员层（spec-06）。</summary>
    public const byte HyParView = 0x02;

    /// <summary>SwarmSync 块清单/取块控制（spec-12 §6.1）。</summary>
    public const byte SwarmSync = 0x03;

    /// <summary>快照流会话承载（快照安装的流式数据面）。</summary>
    public const byte SnapshotStream = 0x04;

    /// <summary>Gossip 广播（spec-06 §6 dissemination——<see cref="P2P.SwarmBroadcast"/> 数据报面）。</summary>
    public const byte Gossip = 0x05;

    /// <summary>联邦拉取/批次承载（二期-F6——DDR-F6 跨集群已提交条目流）。</summary>
    public const byte Federation = 0x06;
}
