using System.Security.Cryptography;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Security;

namespace TC.Tier.Core.Net.Tests.Security;

/// <summary>
/// SecureSession 契约测试（spec-12 §3.4 KeyPair 档握手驱动——Noise IK 同构）：
/// 双侧驱动互通（Init 扩展→Ack 扩展→会话密钥一致）、方向密钥分离、密钥确认互验、
/// 错静态公钥拒、Ack 签名篡改拒、畸形扩展段拒、扩展段定长。
/// </summary>
public class SecureSessionTests : IDisposable
{
    private readonly NodeKeyPair _initiatorStatic = NodeKeyPair.Generate();
    private readonly NodeKeyPair _responderStatic = NodeKeyPair.Generate();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _initiatorStatic.Dispose();
        _responderStatic.Dispose();
    }

    /// <summary>双侧全流程互通：Init 扩展 → 应答方验签/产 Ack 扩展 → 发起方验签 → 双方会话密钥一致
    /// （发送/接收方向对偶：i.SendKey == r.RecvKey 且 i.RecvKey == r.SendKey）。</summary>
    [Fact]
    public void BothSides_Derive_ConsistentDirectionalKeys()
    {
        var initNonce = 0x1122334455667788UL;
        using var initEph = NodeKeyPair.Generate();
        using var respEph = NodeKeyPair.Generate();
        var respNonce = 0x8877665544332211UL;

        // 发起方：Init 扩展段
        var initExt = new byte[SecureSession.InitExtensionSize];
        SecureSession.EncodeInitExtension(_initiatorStatic, initEph, initNonce, initExt);

        // 应答方：验 Init → 产 Ack 扩展段 → 会话
        var (initiatorStaticReported, ackExt, responderFinish) = SecureSession.StartAsResponder(
            _responderStatic, initExt, initNonce, FrameProtection.Aead);
        var ack = ackExt(respEph, respNonce);
        using var responderSession = responderFinish(respEph, respNonce);

        // 发起方：消费 Ack → 会话（自报静态公钥随会话返回——钉扎比对/TOFU 学习在调用方）
        var (initiatorSession, responderReported) = SecureSession.FinishAsInitiator(
            _initiatorStatic, initEph, initNonce, ack, FrameProtection.Aead);
        using var _iSession = initiatorSession;

        responderReported.Should().Equal(_responderStatic.PublicKey.ToArray(), "自报静态公钥 = 应答方真实公钥（签名已证持有）");
        initiatorSession.SendKey.Should().Equal(responderSession.RecvKey, "发起方发送 = 应答方接收");
        initiatorSession.RecvKey.Should().Equal(responderSession.SendKey, "应答方发送 = 发起方接收");
        initiatorSession.ConfirmKey.Should().Equal(responderSession.ConfirmKey, "确认键双方同源");
        initiatorSession.SendKey.Should().NotEqual(initiatorSession.RecvKey, "方向键分离（自反攻击防御）");
    }

    /// <summary>密钥确认互验：同一 transcript 哈希双方标签一致；篡改标签拒。</summary>
    [Fact]
    public void ConfirmTag_MutualVerification()
    {
        var (initiator, responder) = Handshake();
        using var _1 = initiator;
        using var _2 = responder;
        var transcript = new byte[32];
        Random.Shared.NextBytes(transcript);

        var tag = initiator.ComputeConfirmTag(transcript);
        responder.VerifyConfirmTag(transcript, tag).Should().BeTrue("双方同键同域——互验成立");
        tag[5] ^= 0xFF;
        responder.VerifyConfirmTag(transcript, tag).Should().BeFalse("篡改标签拒绝");
    }

    /// <summary>伪造静态公钥（冒充形态）：签名域含自报静态公钥——签名者与自报者不一致即失败。</summary>
    [Fact]
    public void ForgedStaticKey_SignatureRejected()
    {
        // A 的临时材料 + A 的签名，但静态公钥段写 B 的（自报伪造）——签名域含静态公钥段，验证失败
        using var eph = NodeKeyPair.Generate();
        var initExt = new byte[SecureSession.InitExtensionSize];
        SecureSession.EncodeInitExtension(_initiatorStatic, eph, 42, initExt);
        _responderStatic.PublicKey.CopyTo(initExt.AsSpan(0, NodeKeyPair.PublicKeySize));   // 替换静态公钥段

        var act = () => SecureSession.StartAsResponder(_responderStatic, initExt, 42, FrameProtection.Aead);
        act.Should().Throw<CryptographicException>("静态公钥在签名域内——替换即验证失败");
    }

    /// <summary>Ack 扩展段签名篡改：发起方 Finish 抛。</summary>
    [Fact]
    public void FinishAsInitiator_TamperedAckSignature_Throws()
    {
        var initNonce = 7UL;
        using var initEph = NodeKeyPair.Generate();
        using var respEph = NodeKeyPair.Generate();
        var initExt = new byte[SecureSession.InitExtensionSize];
        SecureSession.EncodeInitExtension(_initiatorStatic, initEph, initNonce, initExt);
        var (_, ackExt, _) = SecureSession.StartAsResponder(
            _responderStatic, initExt, initNonce, FrameProtection.Aead);
        var ack = ackExt(respEph, 99UL);
        ack[^1] ^= 0xFF;   // 破坏签名尾字节

        var act = () => SecureSession.FinishAsInitiator(_initiatorStatic, initEph, initNonce,
            ack, FrameProtection.Aead);
        act.Should().Throw<CryptographicException>();
    }

    /// <summary>Ack 临时公钥替换（签名域内材料被换）：Finish 拒。</summary>
    [Fact]
    public void FinishAsInitiator_ReplacedEphemeral_Throws()
    {
        var initNonce = 7UL;
        using var initEph = NodeKeyPair.Generate();
        using var respEph = NodeKeyPair.Generate();
        using var otherEph = NodeKeyPair.Generate();
        var initExt = new byte[SecureSession.InitExtensionSize];
        SecureSession.EncodeInitExtension(_initiatorStatic, initEph, initNonce, initExt);
        var (_, ackExt, _) = SecureSession.StartAsResponder(
            _responderStatic, initExt, initNonce, FrameProtection.Aead);
        // 用 respEph 产签名，但替换公钥段为 otherEph——签名域不匹配
        var ack = ackExt(respEph, 99UL);
        otherEph.PublicKey.CopyTo(ack.AsSpan(0, NodeKeyPair.PublicKeySize));

        var act = () => SecureSession.FinishAsInitiator(_initiatorStatic, initEph, initNonce,
            ack, FrameProtection.Aead);
        act.Should().Throw<CryptographicException>("临时公钥在签名域内——替换即验证失败");
    }

    /// <summary>畸形扩展段长度：拒绝（降级截断防御）。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(200)]
    public void MalformedExtensionLength_Throws(int length)
    {
        var act = () => SecureSession.StartAsResponder(_responderStatic, new byte[length], 1,
            FrameProtection.Aead);
        act.Should().Throw<FormatException>();
    }

    /// <summary>扩展段定长断言（握手载荷追加段布局锁定）。</summary>
    [Fact]
    public void ExtensionSizes_Locked()
    {
        SecureSession.InitExtensionSize.Should().Be(33 + 33 + 64, "Init = 静态公钥 + 临时公钥 + 签名");
        SecureSession.AckExtensionSize.Should().Be(33 + 33 + 8 + 64, "Ack = 静态公钥 + 临时公钥 + responderNonce + 签名");
    }

    /// <summary>每连接临时密钥：两次握手会话密钥不同（前向保密的会话独立性面）。</summary>
    [Fact]
    public void DistinctConnections_DistinctKeys()
    {
        var (s1, r1) = Handshake();
        using var _1 = s1;
        using var _2 = r1;
        var (s2, r2) = Handshake();
        using var _3 = s2;
        using var _4 = r2;
        s1.SendKey.Should().NotEqual(s2.SendKey, "每次连接新临时密钥——会话密钥独立");
    }

    /// <summary>双侧握手 helper（会话生命周期归调用方——本方法不 Dispose）。</summary>
    /// <summary>UDP 认证键同源：双方 DeriveUdpKey 一致 + 尾标签验/篡改拒。</summary>
    [Fact]
    public void UdpKey_BothSidesAgree_TagVerifiedAndTamperRejected()
    {
        var (initiator, responder) = Handshake();
        using var _1 = initiator;
        using var _2 = responder;
        var udpKey = initiator.DeriveUdpKey();
        udpKey.Should().Equal(responder.DeriveUdpKey(), "UDP 认证键双方同源（会话确认键派生）");

        var frame = new byte[64];
        Random.Shared.NextBytes(frame);
        var tag = SecureSession.ComputeUdpTag(udpKey, frame);
        SecureSession.VerifyUdpTag(udpKey, frame, tag).Should().BeTrue();
        frame[3] ^= 0xFF;
        SecureSession.VerifyUdpTag(udpKey, frame, tag).Should().BeFalse("帧篡改拒绝");
    }

    private (SecureSession Initiator, SecureSession Responder) Handshake()
    {
        var initNonce = (ulong)Random.Shared.NextInt64();
        using var initEph = NodeKeyPair.Generate();
        using var respEph = NodeKeyPair.Generate();
        var respNonce = (ulong)Random.Shared.NextInt64();
        var initExt = new byte[SecureSession.InitExtensionSize];
        SecureSession.EncodeInitExtension(_initiatorStatic, initEph, initNonce, initExt);
        var (_, ackExt, finish) = SecureSession.StartAsResponder(
            _responderStatic, initExt, initNonce, FrameProtection.Aead);
        var ack = ackExt(respEph, respNonce);
        var respSession = finish(respEph, respNonce);
        var initSession = SecureSession.FinishAsInitiator(
            _initiatorStatic, initEph, initNonce, ack, FrameProtection.Aead).Session;
        return (initSession, respSession);
    }
}
