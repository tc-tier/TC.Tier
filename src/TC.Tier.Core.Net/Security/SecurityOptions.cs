using System.Security.Cryptography.X509Certificates;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Security;

/// <summary>安全形态档位（spec-12 §3.4 三档——装配期配置驱动，无协商）。</summary>
public enum SecurityMode : byte
{
    /// <summary>明文（可信域内网——零开销）。</summary>
    Plaintext = HandshakeSecurity.Plaintext,

    /// <summary>无证书非对称（签名+ECDH+AEAD/MAC——推荐缺省；信任锚 = 公钥配置钉扎/TOFU）。</summary>
    KeyPair = HandshakeSecurity.KeyPair,

    /// <summary>TLS1.3 证书体系（CA/合规场景——SAN <c>nid:</c> 绑定 + 短期证书轮换）。</summary>
    MutualTls = HandshakeSecurity.MutualTls,
}

/// <summary>KeyPair 档帧保护粒度（两档共享握手与密钥派生——区别只在帧保护强度，spec-12 §3.4）。</summary>
public enum FrameProtection : byte
{
    /// <summary>仅完整性：会话密钥 MAC——防冒充/伪造/篡改，不加密（低窃听威胁内网最小开销）。</summary>
    MacOnly = 0,

    /// <summary>完整性 + 机密性：帧加密（推荐缺省——与 mTLS 强度等价）。</summary>
    Aead = 1,
}

/// <summary>
/// 安全配置（spec-12 §3.4——装配期一次给全，运行期不可变；防降级 = 本端配置档与对端到达帧
/// 安全档不匹配即 Error + 断连，永不机会主义升降级）。
/// <para>★ 工厂三选一：<see cref="Plain"/> ∥ <see cref="KeyPairPinned"/>（推荐缺省——信任强度
///   = mTLS）∥ <see cref="KeyPairTofu"/>（SSH known_hosts 式——首连有中间人窗口）∥
///   <see cref="MutualTls"/>（证书档——证书由装配方供给）。</para>
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>安全形态档位（握手 Security 字段同源值）。</summary>
    public SecurityMode Mode { get; }

    /// <summary>本端密钥对（KeyPair 档——签名身份 + ECDH 前向保密；其余档 null）。</summary>
    public NodeKeyPair? OwnKey { get; }

    /// <summary>信任锚（KeyPair 档——钉扎只读 ∥ TOFU 可写；其余档 null）。</summary>
    public ITrustAnchorStore? Trust { get; }

    /// <summary>帧保护粒度（KeyPair 档——缺省 <see cref="FrameProtection.Aead"/>）。</summary>
    public FrameProtection Protection { get; }

    /// <summary>本端证书（mTLS 档——私钥证书；其余档 null）。</summary>
    public X509Certificate2? Certificate { get; }

    /// <summary>mTLS 对端 CA（null = 系统信任库；自签形态传自签 CA）。</summary>
    public X509Certificate2Collection? CaCertificates { get; }

    private SecurityOptions(SecurityMode mode, NodeKeyPair? ownKey, ITrustAnchorStore? trust,
        FrameProtection protection, X509Certificate2? certificate, X509Certificate2Collection? ca)
    {
        Mode = mode;
        OwnKey = ownKey;
        Trust = trust;
        Protection = protection;
        Certificate = certificate;
        CaCertificates = ca;
    }

    /// <summary>明文档（可信域内网——缺省）。</summary>
    public static SecurityOptions Plain { get; } = new(SecurityMode.Plaintext, null, null, FrameProtection.Aead, null, null);

    /// <summary>KeyPair 档·公钥钉扎（推荐缺省——WireGuard 式：地址表条目携带对端公钥，信任强度 = mTLS）。</summary>
    /// <param name="own">本端密钥对。</param>
    /// <param name="trust">钉扎只读表（<see cref="PinnedTrustStore"/>——装配期给全）。</param>
    /// <param name="protection">帧保护粒度（缺省 AEAD）。</param>
    /// <returns>KeyPair·钉扎档安全配置。</returns>
    public static SecurityOptions KeyPairPinned(NodeKeyPair own, PinnedTrustStore trust,
        FrameProtection protection = FrameProtection.Aead)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(trust);
        return new(SecurityMode.KeyPair, own, trust, protection, null, null);
    }

    /// <summary>KeyPair 档·TOFU（首连学习公钥写回——首连有中间人窗口，半可信内网形态）。</summary>
    /// <param name="own">本端密钥对。</param>
    /// <param name="trust">可写信任存储（<see cref="InMemoryTrustStore"/> 或组装层持久化实现）。</param>
    /// <param name="protection">帧保护粒度（缺省 AEAD）。</param>
    /// <returns>KeyPair·TOFU 档安全配置。</returns>
    /// <exception cref="ArgumentException">传入只读钉扎表（TOFU 需可写存储）。</exception>
    public static SecurityOptions KeyPairTofu(NodeKeyPair own, ITrustAnchorStore trust,
        FrameProtection protection = FrameProtection.Aead)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(trust);
        if (trust is PinnedTrustStore)
            throw new ArgumentException("TOFU 形态需要可写存储（钉扎表用 KeyPairPinned）。", nameof(trust));
        return new(SecurityMode.KeyPair, own, trust, protection, null, null);
    }

    /// <summary>mTLS 档（TLS1.3 证书体系——SAN <c>nid:&lt;hex32&gt;</c> URI 条目绑定 NodeId）。</summary>
    /// <param name="certificate">本端私钥证书（含 SAN nid 条目）。</param>
    /// <param name="ca">对端 CA（null = 系统信任库；自签形态传自签 CA）。</param>
    /// <returns>mTLS 档安全配置。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> 为 null。</exception>
    public static SecurityOptions MutualTls(X509Certificate2 certificate, X509Certificate2Collection? ca = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new(SecurityMode.MutualTls, null, null, FrameProtection.Aead, certificate, ca);
    }
}
