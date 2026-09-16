using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// HandshakeInit 载荷（发起方三步握手第一步，32B——spec-12 §3.3）。
/// <code>
/// [0..16) NodeId     发起方节点 ID（16B 不透明字节串——原序直拷，§2 嵌套件）
/// [16]    MinVersion 本端支持头版本下界
/// [17]    MaxVersion 本端支持头版本上界
/// [18]    Features   本端特性位（<see cref="HandshakeFeatures"/>——保留位必须清零）
/// [19..23) ClusterTag 4B 集群归属（装配期配置——身份不变、归属可变；错集群握手期 fail-fast）
/// [23..31) Nonce     8B 发起方随机（应答原样回带——错配/重放甄别）
/// [31]    Security   安全形态（<see cref="HandshakeSecurity"/>）
/// </code>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.StructSize)]
[StructLayout(LayoutKind.Explicit, Size = 32)]
public readonly struct HandshakeInit
{
    /// <summary>发起方节点 ID。</summary>
    [FieldOffset(0)] public readonly NodeId NodeId;

    /// <summary>发起方头版本下界。</summary>
    [FieldOffset(16)] public readonly byte MinVersion;

    /// <summary>发起方头版本上界。</summary>
    [FieldOffset(17)] public readonly byte MaxVersion;

    /// <summary>发起方特性位。</summary>
    [FieldOffset(18)] public readonly byte Features;

    /// <summary>发起方集群归属标签（错集群在握手期 fail-fast——Error + 断连 + 计数）。</summary>
    [FieldOffset(19)] public readonly uint ClusterTag;

    /// <summary>发起方随机数（Ack 原样回带）。</summary>
    [FieldOffset(23)] public readonly ulong Nonce;

    /// <summary>发起方要求的安全形态。</summary>
    [FieldOffset(31)] public readonly byte Security;

    /// <summary>全字段构造（按偏移序——BinaryLayout 生成 Read 的构造路径）。</summary>
    /// <param name="nodeId">发起方节点 ID。</param>
    /// <param name="minVersion">头版本下界。</param>
    /// <param name="maxVersion">头版本上界。</param>
    /// <param name="features">特性位。</param>
    /// <param name="clusterTag">集群归属标签。</param>
    /// <param name="nonce">随机数。</param>
    /// <param name="security">安全形态。</param>
    public HandshakeInit(NodeId nodeId, byte minVersion, byte maxVersion, byte features, uint clusterTag, ulong nonce, byte security)
    {
        NodeId = nodeId;
        MinVersion = minVersion;
        MaxVersion = maxVersion;
        Features = features;
        ClusterTag = clusterTag;
        Nonce = nonce;
        Security = security;
    }
}
