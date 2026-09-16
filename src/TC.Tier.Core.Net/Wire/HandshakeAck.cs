using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// HandshakeAck 载荷（应答方三步握手第二步，31B——spec-12 §3.3）。
/// <code>
/// [0..16) NodeId     应答方节点 ID（16B 原序直拷）
/// [16]    Version    选定头版本（交集最高）
/// [17]    Features   交集特性位（按位与）
/// [18..22) ClusterTag 发起方标签回显（发起方据此核对——应答侧错集群在收到 Init 时即拒）
/// [22..30) Nonce     发起方 Nonce 原样回带
/// [30]    Security   应答方选定安全形态（配置驱动——防降级 §3.4）
/// </code>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.StructSize)]
[StructLayout(LayoutKind.Explicit, Size = 31)]
public readonly struct HandshakeAck
{
    /// <summary>应答方节点 ID。</summary>
    [FieldOffset(0)] public readonly NodeId NodeId;

    /// <summary>选定头版本（交集最高）。</summary>
    [FieldOffset(16)] public readonly byte Version;

    /// <summary>交集特性位。</summary>
    [FieldOffset(17)] public readonly byte Features;

    /// <summary>发起方集群标签回显。</summary>
    [FieldOffset(18)] public readonly uint ClusterTag;

    /// <summary>发起方 Nonce 回带。</summary>
    [FieldOffset(22)] public readonly ulong Nonce;

    /// <summary>应答方选定安全形态。</summary>
    [FieldOffset(30)] public readonly byte Security;

    /// <summary>全字段构造（按偏移序——BinaryLayout 生成 Read 的构造路径）。</summary>
    /// <param name="nodeId">应答方节点 ID。</param>
    /// <param name="version">选定头版本。</param>
    /// <param name="features">交集特性位。</param>
    /// <param name="clusterTag">发起方标签回显。</param>
    /// <param name="nonce">发起方 Nonce 回带。</param>
    /// <param name="security">安全形态。</param>
    public HandshakeAck(NodeId nodeId, byte version, byte features, uint clusterTag, ulong nonce, byte security)
    {
        NodeId = nodeId;
        Version = version;
        Features = features;
        ClusterTag = clusterTag;
        Nonce = nonce;
        Security = security;
    }
}
