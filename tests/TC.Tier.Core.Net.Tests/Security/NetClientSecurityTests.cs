using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Hosting;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Tests.Hosting;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Security;

/// <summary>
/// 客户端安全档 E2E（二期-A——NetClientBuilder 装配面，验证矩阵 A 行）：
/// KeyPair / mTLS 客户端互通 + 身份正确；错 CA / 错 SAN 走契约异常面（NetIOException，
/// 原始 AuthenticationException 不外逸——握手异常收口）；服务端 NodeId ≠ 证书 SAN
/// 拨号侧拒（缺口回归）；明文客户端撞安全服务端防降级拒。
/// </summary>
public class NetClientSecurityTests
{
    private const byte EchoProtocol = 0x64;   // 注册区（0x60-0xAF 使用方自管号——测试域）
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private sealed class EchoRequestHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();
    }

    // ══ 测试 PKI（★进程级一次性生成——RSA 2048 证书生成+PFX 是 CPU 密集段，xunit 并行下
    //   每测生成击穿其他测试超时窗（MutualTlsSecurityTests 同款判例）。固定测试 NodeId——
    //   端口隔离即可。双 CA：otherCa 供"错 CA"形态（客户端只信它，服务器证书不被信任）。══

    private static readonly NodeId PkiServerId = NodeId.Parse("11111111111111111111111111111111");
    private static readonly NodeId PkiClientId = NodeId.Parse("22222222222222222222222222222222");
    private static readonly NodeId PkiWrongId = NodeId.Parse("33333333333333333333333333333333");

    private static readonly Lazy<(X509Certificate2 Ca, X509Certificate2 OtherCa, X509Certificate2 Server,
        X509Certificate2 Client, X509Certificate2 ServerWrongSan)> CachedPki = new(() =>
    {
        X509Certificate2 SelfSignedCa(string cn)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));   // PFX 化——Schannel 不认临时密钥形态
        }

        X509Certificate2 Issue(X509Certificate2 ca, NodeId node, string cn)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddUri(new Uri(CertificateNodeId.BuildSanUri(node)));   // nid:<hex32> 绑定条目
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            // ★ EKU 双 OID（#422——macOS 信任评估前置）：缺 ServerAuth/ClientAuth EKU 时 Apple TLS 栈
            //   在托管校验回调生效前即拒证书（正向路径都握不上手）；Linux/Windows 无此前置判定，双 OID 无害。
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
            using var publicOnly = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
                RandomNumberGenerator.GetBytes(8));
            using var withKey = publicOnly.CopyWithPrivateKey(key);
            return new X509Certificate2(withKey.Export(X509ContentType.Pfx));   // PFX 重导出——同上
        }

        var ca = SelfSignedCa("tc-tier-netclient-ca");
        return (ca, SelfSignedCa("tc-tier-netclient-other-ca"),
            Issue(ca, PkiServerId, "srv"), Issue(ca, PkiClientId, "cli"), Issue(ca, PkiWrongId, "srv-wrong-san"));
    });

    // ══ A 行：KeyPair / mTLS 客户端 E2E（builder 级——连通 + 身份正确 + 业务往返）══

    [Fact]
    public async Task KeyPair_Client_ConnectsAndLearnsRemoteId()
    {
        var clientId = NodeId.NewRandom();
        var serverId = NodeId.NewRandom();
        using var clientKey = NodeKeyPair.Generate();
        using var serverKey = NodeKeyPair.Generate();
        var port = TestEndpoints.ReservePort();

        await using var server = await ClusterBuilder.Create(serverId)
            .Listen(TestEndpoints.Loopback(port))
            .WithKeyPair(SecurityOptions.KeyPairPinned(serverKey,
                new PinnedTrustStore([clientId], [clientKey.PublicKey.ToArray()])))
            .StartAsync();

        await using var client = await NetClientBuilder.Create(clientId)
            .Connect(TestEndpoints.Loopback(port))
            .WithKeyPair(SecurityOptions.KeyPairPinned(clientKey,
                new PinnedTrustStore([serverId], [serverKey.PublicKey.ToArray()])))
            .StartAsync();

        client.RemoteId.Should().Be(serverId, "KeyPair 档互通——握手得知对端身份");
        client.Self.Should().Be(clientId);
    }

    [SkippableFact]
    public async Task MutualTls_Client_ConnectsAndEchoes()
    {
        var (ca, _, serverCert, clientCert, _) = CachedPki.Value;
        var cas = new X509Certificate2Collection { ca };
        var port = TestEndpoints.ReservePort();

        // ★ macOS 环境前置门控（#422——§4.3 合法 Skip 形态：环境不可用，非为过 CI）：Apple TLS 栈对
        //   自签测试 CA 链的信任评估**非确定性**前置拒（同代码跨 run 一红一绿实证：34703590515 红 /
        //   34704233062 绿）——托管的确定性断言两面都会偶发翻红。夹具级根因（自签 CA + Apple 信任
        //   评估）在 #422 跟踪，环境可用后移除本门控。
        Skip.If(OperatingSystem.IsMacOS(), "macOS Apple TLS 栈对自签测试 CA 信任评估非确定性前置拒——夹具不可用（#422）。");

        // ★ 服务端就绪观测（NetClientBuilderTests 判例同款）：先订阅 PeerConnected 再拨号
        await using var server = await ClusterBuilder.Create(PkiServerId)
            .Listen(TestEndpoints.Loopback(port))
            .WithMutualTls(serverCert, cas)
            .StartAsync();
        var serverSawClient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PeerConnected += _ => serverSawClient.TrySetResult();

        await using var client = await NetClientBuilder.Create(PkiClientId)
            .Connect(TestEndpoints.Loopback(port))
            .WithMutualTls(clientCert, cas)
            .RegisterRequestHandler(EchoProtocol, new EchoRequestHandler())
            .StartAsync();

        client.RemoteId.Should().Be(PkiServerId, "mTLS 档互通——握手得知对端身份");
        await serverSawClient.Task.WaitAsync(WaitLimit);
        var payload = new byte[] { 0xAA, 0xBB };
        var echoed = await server.SendRequestAsync(PkiClientId, EchoProtocol, payload);
        echoed.Should().Equal(payload, "TLS 通道上业务往返");
    }

    // ══ A 行：错 CA——契约异常面（NetIOException，原始 AuthenticationException 不外逸）══

    [Fact]
    public async Task MutualTls_WrongCa_FailsAsNetIOException()
    {
        var (_, otherCa, serverCert, clientCert, _) = CachedPki.Value;
        var port = TestEndpoints.ReservePort();

        await using var server = await ClusterBuilder.Create(PkiServerId)
            .Listen(TestEndpoints.Loopback(port))
            .WithMutualTls(serverCert, new X509Certificate2Collection { otherCa })   // 服务端只信 otherCa
            .StartAsync();

        var act = async () => await NetClientBuilder.Create(PkiClientId)
            .Connect(TestEndpoints.Loopback(port))
            .WithMutualTls(clientCert, new X509Certificate2Collection { otherCa })   // 客户端只信 otherCa——服务器证书 ca 签发不被信任
            .StartAsync();

        var thrown = await act.Should().ThrowAsync<NetIOException>("错 CA = 握手失败契约面").WaitAsync(WaitLimit);
        thrown.Which.InnerException.Should().BeNull("原始 AuthenticationException 不外逸（握手异常收口）");
    }

    // ══ A 行：错 SAN / 服务端 NodeId ≠ 证书 SAN——拨号侧对称核对（缺口回归）══

    [Fact]
    public async Task MutualTls_ServerCertSanMismatch_DialSideRejects()
    {
        var (ca, _, _, clientCert, serverWrongSan) = CachedPki.Value;
        var cas = new X509Certificate2Collection { ca };
        var port = TestEndpoints.ReservePort();

        // 服务端身份 = PkiServerId，证书 SAN 声明 PkiWrongId——TLS 链合法（同 CA 签发），
        // 拨号侧 Ack 对称核对必须拒（修复前客户端无感知接受 = 冒名中转窗口）
        await using var server = await ClusterBuilder.Create(PkiServerId)
            .Listen(TestEndpoints.Loopback(port))
            .WithMutualTls(serverWrongSan, cas)
            .StartAsync();

        var act = async () => await NetClientBuilder.Create(PkiClientId)
            .Connect(TestEndpoints.Loopback(port))
            .WithMutualTls(clientCert, cas)
            .StartAsync();

        var thrown = await act.Should().ThrowAsync<NetIOException>("SAN 声明 ≠ 宣称身份——拨号侧拒").WaitAsync(WaitLimit);
        // ★ macOS 分平台（#422）：平台信任评估可能先行拒（握手失败形态）也可能放行至托管核对
        //   （非确定性）——两路径殊途同归都是 NetIOException（冒名证书不可用），文案层不钉；
        //   "证书 SAN 声明"应用层文案在其余平台钉（托管核对确定性可达）。
        if (!OperatingSystem.IsMacOS())
            thrown.Which.Message.Should().Contain("证书 SAN 声明", "拨号侧对称核对命中（监听侧同文案族）");
    }

    // ══ A 行：明文客户端 → 安全服务端——防降级拒 ══

    [Fact]
    public async Task Plaintext_Client_ToMutualTlsServer_Rejected()
    {
        var (ca, _, serverCert, _, _) = CachedPki.Value;
        var port = TestEndpoints.ReservePort();

        await using var server = await ClusterBuilder.Create(PkiServerId)
            .Listen(TestEndpoints.Loopback(port))
            .WithMutualTls(serverCert, new X509Certificate2Collection { ca })
            .StartAsync();

        var act = async () => await NetClientBuilder.Create(NodeId.NewRandom())
            .Connect(TestEndpoints.Loopback(port))
            .StartAsync();   // 明文客户端——无安全档

        await act.Should().ThrowAsync<NetIOException>("明文撞 mTLS——TLS 层拒/握手断连，防降级契约面").WaitAsync(WaitLimit);
    }
}
