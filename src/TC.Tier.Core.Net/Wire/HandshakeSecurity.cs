using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 握手安全形态（spec-12 §3.4 三档——当前实现明文；KeyPair/mTLS 档 W-Security 波生效）。
/// </summary>
[ConstantRegistry]
public static partial class HandshakeSecurity
{
    /// <summary>明文（内网/同主机可信域）。</summary>
    public const byte Plaintext = 0x00;

    /// <summary>无证书非对称（签名+ECDH+AEAD——推荐缺省档；两粒度 MAC-only∥AEAD）。</summary>
    public const byte KeyPair = 0x01;

    /// <summary>TLS1.3 mTLS（双向证书 + NodeId↔SAN 绑定——企业 CA/合规场景）。</summary>
    public const byte MutualTls = 0x02;
}
