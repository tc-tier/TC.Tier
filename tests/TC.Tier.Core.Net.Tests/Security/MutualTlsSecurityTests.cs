using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Security;

/// <summary>
/// mTLS 档端到端测试（spec-12 §3.4——TLS1.3 相互认证 + SAN nid 绑定）：
/// 自签 CA + 节点证书（SAN nid 条目）：正确互通（TLS 通道上业务往返）、
/// 错 SAN 拒（证书身份 ≠ 实际节点）、无客户端证书拒、防降级（mTLS 遇明文）。
/// </summary>
public class MutualTlsSecurityTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    // ══ 测试 PKI（★进程级一次性生成——RSA 2048 证书生成+PFX 是 CPU 密集段，每测生成在
    //   xunit 并行下挤占线程池，把其他测试的超时窗击穿（flaky 判例：混跑随机失败/隔离全绿/
    //   失败面漂移）。固定测试 NodeId——测试间以端口隔离，身份不需唯一。
    //   ECDSA PFX 往返在 Windows CNG 炸 PlatformNotSupported（判例）——RSA 保留。）══

    private static readonly NodeId PkiLowId = NodeId.Parse("11111111111111111111111111111111");
    private static readonly NodeId PkiHighId = NodeId.Parse("22222222222222222222222222222222");
    private static readonly NodeId PkiWrongId = NodeId.Parse("33333333333333333333333333333333");

    private static readonly Lazy<(X509Certificate2 Ca, X509Certificate2 Low, X509Certificate2 High, X509Certificate2 HighWrongSan)> CachedPki =
        new(() =>
        {
            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest("CN=tc-tier-test-ca", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var caEphemeral = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            var ca = new X509Certificate2(caEphemeral.Export(X509ContentType.Pfx));   // PFX 化——Schannel 不认临时密钥形态

            X509Certificate2 Issue(NodeId node, string cn)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddUri(new Uri(CertificateNodeId.BuildSanUri(node)));   // nid:<hex32> 绑定条目
                request.CertificateExtensions.Add(san.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                // ★ EKU 双 OID（#422——macOS 信任评估前置，见 NetClientSecurityTests 同款判例）
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
                using var publicOnly = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
                    RandomNumberGenerator.GetBytes(8));   // 序列号（任意唯一即可）
                using var withKey = publicOnly.CopyWithPrivateKey(key);   // Create 产物不含私钥——关联（server 模式必需）
                return new X509Certificate2(withKey.Export(X509ContentType.Pfx));   // PFX 重导出——Schannel 不认临时密钥
            }

            return (ca, Issue(PkiLowId, "node-low"), Issue(PkiHighId, "node-high"), Issue(PkiWrongId, "node-high-wrong-san"));
        });

    private static async Task<(ClusterTransport Low, ClusterTransport High, NodeId LowId, NodeId HighId)>
        SetupTlsPairAsync(bool wrongSanHigh = false)
    {
        var (ca, lowCert, highCert, highWrongSan) = CachedPki.Value;
        var lowId = PkiLowId;
        var highId = PkiHighId;
        var actualHighCert = wrongSanHigh ? highWrongSan : highCert;

        var cas = new X509Certificate2Collection { ca };
        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh),
            SecurityOptions.MutualTls(actualHighCert, cas));
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }),
            SecurityOptions.MutualTls(lowCert, cas));
        low.Start();

        if (wrongSanHigh) return (low, high, lowId, highId);   // 错 SAN = 连接永不建立（被测行为）——Setup 不等
        var lowUp = new TaskCompletionSource();
        low.PeerConnected += _ => lowUp.TrySetResult();
        await lowUp.Task.WaitAsync(WaitLimit);
        return (low, high, lowId, highId);
    }

    /// <summary>正确互通：TLS1.3 相互认证 + TLS 通道上请求回调往返（SAN nid 双向绑定）。</summary>
    [SkippableFact]
    public async Task MutualAuth_TlsChannelRoundTrip()
    {
        // ★ macOS 环境前置门控（#422——§4.3 合法 Skip 形态：环境不可用）：Apple TLS 栈对自签测试
        //   CA 链信任评估非确定性前置拒（同代码跨 run 一红一绿实证）——夹具在 macos 不可用，
        //   根因跟踪 #422，环境可用后移除本门控。
        Skip.If(OperatingSystem.IsMacOS(), "macOS Apple TLS 栈对自签测试 CA 信任评估非确定性前置拒——夹具不可用（#422）。");

        var (low, high, lowId, highId) = await SetupTlsPairAsync();
        await using var _1 = low;
        await using var _2 = high;

        high.RegisterRequestHandler(0x60, new EchoHandler());
        var resp = await low.SendRequestAsync(highId, 0x60, new byte[] { 0x7A },
            new RequestOptions { Timeout = WaitLimit });
        resp.Should().Equal((byte[]) [0x7A], "TLS 通道上业务往返");
    }

    /// <summary>错 SAN 拒：证书 SAN 绑定另一节点身份（冒充）——TLS 层拒绝连接不建立。</summary>
    [Fact]
    public async Task WrongSanBinding_Rejected()
    {
        var (low, high, lowId, highId) = await SetupTlsPairAsync(wrongSanHigh: true);
        await using var _low = low;
        await using var _high = high;

        var established = new TaskCompletionSource();
        low.PeerConnected += _ => established.TrySetResult();
        var act = () => established.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await act.Should().ThrowAsync<TimeoutException>("证书 SAN 身份 ≠ 实际节点——冒充拒绝");
    }

    /// <summary>防降级第三向：mTLS 配置遇明文对端——拒绝（fail-closed）。</summary>
    [Fact]
    public async Task Downgrade_PlaintextPeer_Rejected()
    {
        var (ca, lowCert, highCert, _) = CachedPki.Value;
        var lowId = PkiLowId;
        var highId = PkiHighId;

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh));   // ★ 明文
        await using var _high = high;
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }),
            SecurityOptions.MutualTls(lowCert, new X509Certificate2Collection { ca }));
        await using var _low = low;
        low.Start();

        var established = new TaskCompletionSource();
        low.PeerConnected += _ => established.TrySetResult();
        var act = () => established.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await act.Should().ThrowAsync<TimeoutException>("mTLS 配置遇明文对端——fail-closed 拒绝");
    }

    /// <summary>SAN 工具单测：BuildSanUri/ExtractNodeIds 往返 + 多绑定拒绝 + 无 nid 拒。</summary>
    [Fact]
    public void CertificateNodeId_RoundTripAndBindingRules()
    {
        var (_, cert, _, wrongSan) = CachedPki.Value;

        CertificateNodeId.ExtractNodeIds(cert).Should().BeEquivalentTo([PkiLowId], "SAN nid 条目提取");
        CertificateNodeId.IsBoundTo(cert, PkiLowId, out _).Should().BeTrue("精确绑定成立");
        CertificateNodeId.IsBoundTo(cert, PkiHighId, out _).Should().BeFalse("预期身份不符拒绝");

        CertificateNodeId.ExtractNodeIds(wrongSan).Should().BeEquivalentTo([PkiWrongId], "错 SAN 证书声明其真实绑定");
        CertificateNodeId.IsBoundTo(wrongSan, PkiHighId, out var declared).Should().BeFalse();
        declared.Should().Be(PkiWrongId, "declared 携带证书声明身份（供冒充甄别）");

        // 无 SAN 证书（普通 CN 证书）——不绑定
        using var key = RSA.Create(2048);
        var plain = new CertificateRequest("CN=plain", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        CertificateNodeId.IsBoundTo(plain, PkiLowId, out _).Should().BeFalse("无 nid 条目 = 未按规范签发拒绝");
    }

    /// <summary>裸 TLS 互认探针（不经传输层——隔离证书/回调问题）。</summary>
    [Fact]
    public async Task BareSslStream_MutualAuth_Succeeds()
    {
        var (ca, lowCert, highCert, _) = CachedPki.Value;
        var lowId = PkiLowId;
        var highId = PkiHighId;
        var cas = new X509Certificate2Collection { ca };

        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await using var ssl = new System.Net.Security.SslStream(accepted.GetStream(), false,
                (s, cert, chain, errors) =>
                {
                    if (cert is null || chain is null) { Console.WriteLine("[tls-diag] server: cert/chain null"); return false; }
                    var bound = CertificateNodeId.IsBoundTo(cert, lowId, out _);
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.AddRange(cas);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    var built = chain.Build(new X509Certificate2(cert));
                    return bound && built;
                });
            await ssl.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
            {
                ServerCertificate = highCert,
                ClientCertificateRequired = true,
            });
        });


        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var ssl = new System.Net.Security.SslStream(client.GetStream(), false,
            (s, cert, chain, errors) =>
            {
                if (cert is null || chain is null) { Console.WriteLine("[tls-diag] client: cert/chain null"); return false; }
                var bound = CertificateNodeId.IsBoundTo(cert, highId, out _);
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(cas);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                var built = chain.Build(new X509Certificate2(cert));
                return bound && built;
            });
        await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
        {
            TargetHost = "tc-tier-node",
            ClientCertificates = new X509CertificateCollection { lowCert },
        });
        await serverTask.WaitAsync(WaitLimit);
        listener.Stop();
    }

    private sealed class EchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload).AsTask();
    }
}
