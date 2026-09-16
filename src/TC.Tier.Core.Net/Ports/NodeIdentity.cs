namespace TC.Tier.Core.Net.Ports;

/// <summary>
/// 节点身份（spec-12 §8.3——NodeId + 密钥材料；身份供给端口的载荷）。
/// <para>★ NodeId 跨重启稳定（成员表/votedFor/密钥绑定全部以此为前提）——首次生成保存、
/// 此后加载；密钥材料形态由 W-Security 波次定义（当前为空——Plaintext 档期）。</para>
/// </summary>
public sealed record NodeIdentity
{
    /// <summary>节点 ID（协议一等结构——身份不变量）。</summary>
    public required NodeId Id { get; init; }

    /// <summary>密钥材料（W-Security 形态定义——当前为空）。</summary>
    public byte[] KeyMaterial { get; init; } = [];
}
