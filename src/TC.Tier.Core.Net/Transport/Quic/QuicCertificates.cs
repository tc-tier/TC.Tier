using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using TC.Tier.Core.Net.Security;

namespace TC.Tier.Core.Net.Transport.Quic;

/// <summary>
/// QUIC TLS 证书供给（spec-11 W5——内网模式：运行时自签证书，QUIC 强制 TLS1.3 =
/// 加密+完整性免费；对齐 TCP 明文模式的信任模型——身份经应用层握手交换，证书链/SAN nid
/// 绑定（防伪造）随 mTLS 体系（W4 已有）后置集成）。
/// <para>★ MsQuic 可用性探测：<see cref="QuicListener.IsSupported"/>（Windows 11/Server 2022+
/// 内置；Linux 须安装 libmsquic）——装配期 fail-fast，不静默降级（介质选择是显式决策）。</para>
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public static class QuicCertificates
{
    /// <summary>QUIC ALPN 协议标识（应用层协商——异栈连接拒收）。</summary>
    public const string AlpnProtocol = "tctier/1";

    /// <summary>MsQuic 运行时是否可用（装配前探测面）。</summary>
    public static bool IsSupported => QuicListener.IsSupported;

    /// <summary>生成节点自签证书（ECDSA P-256，CN = NodeId hex，10 年有效期——进程内单例复用）。</summary>
    /// <param name="self">本端节点 ID。</param>
    /// <returns>自签证书（CN 携带 NodeId；Windows 上经 PFX 重导出持久化私钥）。</returns>
    public static X509Certificate2 CreateSelfSigned(NodeId self)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={self}", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        // ★ 二期-E6：SAN nid 绑定（TC.Tier.Core.Net.Security.CertificateNodeId.BuildSanUri）——
        //   mTLS 模式下证书 SAN 即身份声明（TCP 侧 CertificateNodeId 同款语义）
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(CertificateNodeId.BuildSanUri(self)));
        request.CertificateExtensions.Add(san.Build());
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        if (OperatingSystem.IsWindows())
        {
            // ★ Windows Schannel/MsQuic 拒绝 ephemeral 私钥（TLS alert UserCanceled）——
            //   PFX 重导出持久化私钥（已知 workaround，Linux/macOS 无此问题）
            var pfx = cert.Export(X509ContentType.Pkcs12);
            return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
        }
        return cert;
    }

    /// <summary>服务端认证选项（监听侧——自签证书 + ALPN）。</summary>
    /// <param name="certificate">节点证书（<see cref="CreateSelfSigned"/>）。</param>
    /// <returns>监听侧 TLS1.3 认证选项（ALPN = <see cref="AlpnProtocol"/>；不要求客户端证书——mTLS 后置集成）。</returns>
    public static SslServerAuthenticationOptions ServerAuthentication(X509Certificate2 certificate) => new()
    {
        ApplicationProtocols = [new SslApplicationProtocol(AlpnProtocol)],
        ServerCertificate = certificate,
        ClientCertificateRequired = false,   // 内网模式——mTLS 后置集成
    };

    /// <summary>mTLS 服务端认证（二期-E6——要求客户端证书 + CA 链/防御校验回调）。</summary>
    /// <param name="certificate">节点证书。</param>
    /// <param name="ca">客户端 CA 信任集（null = 仅要求证书存在——链校验交系统库）。</param>
    /// <returns>监听侧 mTLS TLS1.3 认证选项（强制客户端证书；<paramref name="ca"/> 非空 = 自定义信任库链校验——SAN nid 身份绑定，否则按系统策略校验无错误才通过）。</returns>
    public static SslServerAuthenticationOptions ServerAuthenticationMtls(X509Certificate2 certificate,
        X509Certificate2Collection? ca) => new()
    {
        ApplicationProtocols = [new SslApplicationProtocol(AlpnProtocol)],
        ServerCertificate = certificate,
        ClientCertificateRequired = true,   // mTLS——相互认证
        RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
        {
            if (cert is null || chain is null) return false;
            if (ca is { } cas && cas.Count > 0)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(cas);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(cert));
            }
            return errors == System.Net.Security.SslPolicyErrors.None;
        },
    };

    /// <summary>mTLS 客户端认证（二期-E6——校验服务器 CA 链/SAN；本端证书出示）。</summary>
    /// <param name="clientCertificate">本端证书（mTLS 要求）。</param>
    /// <param name="ca">服务器 CA 信任集（null = 系统信任库）。</param>
    /// <returns>拨号侧 mTLS TLS1.3 认证选项（出示本端证书；服务器链校验策略与 <see cref="ServerAuthenticationMtls"/> 同款——ca 非空走自定义信任库，否则按系统策略）。</returns>
    public static SslClientAuthenticationOptions ClientAuthenticationMtls(X509Certificate2 clientCertificate,
        X509Certificate2Collection? ca) => new()
    {
        ApplicationProtocols = [new SslApplicationProtocol(AlpnProtocol)],
        ClientCertificates = new X509CertificateCollection { clientCertificate },
        RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
        {
            if (cert is null || chain is null) return false;
            if (ca is { } cas && cas.Count > 0)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(cas);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(cert));
            }
            return errors == System.Net.Security.SslPolicyErrors.None;
        },
    };

    /// <summary>客户端认证选项（拨号侧——接受自签证书：身份校验在应用层握手，不校验证书链）。</summary>
    /// <returns>拨号侧 TLS1.3 认证选项（ALPN = <see cref="AlpnProtocol"/>；远端证书恒接受——加密/完整性由 TLS1.3 保证）。</returns>
    public static SslClientAuthenticationOptions ClientAuthentication()
    {
#pragma warning disable CA5359 // 设计决策：内网模式对齐 TCP 明文信任模型——身份经应用层握手校验，SAN nid 防伪造随 mTLS 体系后置集成
        return new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [new SslApplicationProtocol(AlpnProtocol)],
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,   // 加密+完整性由 TLS1.3 保证；不校验证书链
            TargetHost = "tctier",
        };
#pragma warning restore CA5359
    }
}
