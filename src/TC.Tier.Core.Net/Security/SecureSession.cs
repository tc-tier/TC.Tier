using System.Security.Cryptography;

namespace TC.Tier.Core.Net.Security;

/// <summary>
/// KeyPair 档会话密钥（握手产物——spec-12 §3.4）。
/// <para>★ 前向保密形态（Noise IK 同构）：静态密钥只做<b>签名</b>（身份证明），密钥协商用
///   每连接<b>临时</b> ECDH——静态私钥日后泄露不危及历史会话。</para>
/// <para>★ 双向密钥（ Initiator/Responder 方向分离——同键双向自反攻击防御）+ 确认密钥
///   （Final 帧密钥确认 MAC）。粒度在帧保护层消费（<see cref="FrameProtection"/>——
///   AEAD=加密+认证；MAC-only=仅认证）。</para>
/// </summary>
public sealed class SecureSession : IDisposable
{
    /// <summary>会话密钥长度（HKDF 输出分段——双向各 32B + 确认 32B）。</summary>
    public const int KeySize = 32;

    /// <summary>本端发送密钥（AEAD=加密键；MAC-only=HMAC 键——按方向取本端发送/对端接收）。</summary>
    public byte[] SendKey { get; }

    /// <summary>对端发送密钥（= 本端接收密钥）。</summary>
    public byte[] RecvKey { get; }

    /// <summary>密钥确认键（Final 帧 MAC——双方同键互验）。</summary>
    public byte[] ConfirmKey { get; }

    /// <summary>帧保护粒度（握手双方配置一致——KeyPair 档协商产物）。</summary>
    public FrameProtection Protection { get; }

    private SecureSession(byte[] send, byte[] recv, byte[] confirm, FrameProtection protection)
    {
        SendKey = send;
        RecvKey = recv;
        ConfirmKey = confirm;
        Protection = protection;
    }

    /// <summary>UDP 报文认证键（HKDF(confirm, "udp")——TCP 会话派生，双方同源；
    /// 报文形态 = 标准帧 + 32B 尾标签（帧字节全入 MAC 域））。</summary>
    /// <returns>32B UDP 认证键（双方同源——同一会话派生值一致）。</returns>
    public byte[] DeriveUdpKey()
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ConfirmKey, 32,
            salt: Array.Empty<byte>(), info: "tc-tier-net/v1/udp-auth"u8.ToArray());
    }

    /// <summary>UDP 尾标签计算（HMAC(udpKey, frame)）。</summary>
    /// <param name="udpKey">UDP 认证键（<see cref="DeriveUdpKey"/> 派生）。</param>
    /// <param name="frame">完整报文（帧字节全入 MAC 域）。</param>
    /// <returns>32B HMAC-SHA256 尾标签。</returns>
    public static byte[] ComputeUdpTag(byte[] udpKey, ReadOnlySpan<byte> frame)
    {
        using var hmac = new HMACSHA256(udpKey);
        var tag = new byte[32];
        hmac.TryComputeHash(frame, tag, out _);
        return tag;
    }

    /// <summary>UDP 尾标签校验（常量时间比较）。</summary>
    /// <param name="udpKey">UDP 认证键（<see cref="DeriveUdpKey"/> 派生）。</param>
    /// <param name="frame">报文帧部分（不含尾标签）。</param>
    /// <param name="tag">报文尾部的 32B 标签。</param>
    /// <returns>true = 标签匹配（报文认证通过）；false = 标签不符（丢弃并计数）。</returns>
    public static bool VerifyUdpTag(byte[] udpKey, ReadOnlySpan<byte> frame, ReadOnlySpan<byte> tag)
        => CryptographicOperations.FixedTimeEquals(ComputeUdpTag(udpKey, frame), tag);

    /// <summary>Final 帧密钥确认标签（HMAC-SHA256(confirm, "final"||transcript)——双方互验同值）。</summary>
    /// <param name="transcriptHash">握手记录哈希（Init/Ack 全材料）。</param>
    /// <returns>32B 确认标签（写入 Final 帧供对端校验）。</returns>
    public byte[] ComputeConfirmTag(ReadOnlySpan<byte> transcriptHash)
    {
        using var hmac = new HMACSHA256(ConfirmKey);
        var input = new byte[6 + transcriptHash.Length];
        "final"u8.CopyTo(input);
        transcriptHash.CopyTo(input.AsSpan(6));
        return hmac.ComputeHash(input);
    }

    /// <summary>校验对端 Final 标签（密钥确认失败 = Error + 断连——握手不完整）。</summary>
    /// <param name="transcriptHash">握手记录哈希（Init/Ack 全材料）。</param>
    /// <param name="tag">对端 Final 帧携带的确认标签。</param>
    /// <returns>true = 标签匹配（双方密钥一致、握手材料完整）；false = 确认失败。</returns>
    public bool VerifyConfirmTag(ReadOnlySpan<byte> transcriptHash, ReadOnlySpan<byte> tag)
    {
        var expected = ComputeConfirmTag(transcriptHash);
        return CryptographicOperations.FixedTimeEquals(expected, tag);
    }

    /// <summary>密钥销毁（尽力——托管数组清零后弃置）。</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(SendKey);
        CryptographicOperations.ZeroMemory(RecvKey);
        CryptographicOperations.ZeroMemory(ConfirmKey);
    }

    // ══ 握手驱动（双方共用——Initiator/Responder 两视角）═══

    /// <summary>发起方视角握手驱动：持有本端静态+临时密钥，消费 Ack 扩展段（自报静态公钥）产出会话。
    /// <para>★ 验签用 Ack 自报的静态公钥；返回该公钥供调用方做钉扎比对/TOFU 学习（验签成立 ≠
    ///   信任锚成立——两道校验分离）。</para></summary>
    /// <param name="staticKey">本端静态密钥（签名身份）。</param>
    /// <param name="ephemeral">本端临时密钥（本次连接新建——ECDH 用后即弃）。</param>
    /// <param name="initiatorNonce">Init 携带的 Nonce。</param>
    /// <param name="responderExtension">Ack 扩展段（[静态公钥][临时公钥][nonce][签名]）。</param>
    /// <param name="protection">帧保护粒度（本端配置——对端一致由调用方核对）。</param>
    /// <returns>（会话, 应答方自报静态公钥——33B）。</returns>
    public static (SecureSession Session, byte[] ResponderStaticKey) FinishAsInitiator(NodeKeyPair staticKey,
        NodeKeyPair ephemeral, ulong initiatorNonce, ReadOnlySpan<byte> responderExtension,
        FrameProtection protection)
    {
        var (responderStatic, responderEph, responderNonce, responderSig) = DecodeAckExtension(responderExtension);
        // Ack 签名域：双方静态+临时公钥+双方 Nonce（Init 材料全入域——防替换）
        var transcript = BuildTranscript(staticKey.PublicKey, responderStatic,
            ephemeral.PublicKey, responderEph, initiatorNonce, responderNonce);
        if (!NodeKeyPair.Verify(responderStatic, transcript, responderSig))
            throw new CryptographicException("Ack 签名验证失败（对端静态身份证明不成立——Error + 断连）。");
        var session = DeriveSession(ephemeral, responderEph, initiatorNonce, responderNonce,
            initiatorSide: true, protection);
        return (session, responderStatic);
    }

    /// <summary>应答方视角握手驱动：校验 Init 扩展段签名（自报静态公钥）→ 产 Ack 扩展段 → 会话工厂。
    /// <para>★ 两步形态（应答方先验 Init 再发 Ack——签名失败立即 Error 断连，不产 Ack）。
    ///   返回发起方自报静态公钥供钉扎比对/TOFU 学习。</para></summary>
    /// <param name="staticKey">本端静态密钥。</param>
    /// <param name="initiatorExtension">Init 扩展段（[静态公钥][临时公钥][签名]）。</param>
    /// <param name="initiatorNonce">Init 携带的 Nonce。</param>
    /// <param name="protection">帧保护粒度。</param>
    /// <returns>（InitiatorStatic,（Ack 扩段编码, 会话完成））。</returns>
    public static (byte[] InitiatorStaticKey, Func<NodeKeyPair, ulong, byte[]> AckExtension,
        Func<NodeKeyPair, ulong, SecureSession> Finish)
        StartAsResponder(NodeKeyPair staticKey, ReadOnlySpan<byte> initiatorExtension, ulong initiatorNonce,
        FrameProtection protection)
    {
        var (initiatorStatic, initiatorEph, initiatorSig) = DecodeInitExtension(initiatorExtension);
        // Init 签名域不含 responder 材料（发起时未知——responder 段空，与 EncodeInitExtension 签名域一致）
        var initTranscript = BuildTranscript(initiatorStatic, default, initiatorEph, default, initiatorNonce, 0);
        if (!NodeKeyPair.Verify(initiatorStatic, initTranscript, initiatorSig))
            throw new CryptographicException("Init 签名验证失败（对端静态身份证明不成立——Error + 断连）。");

        return (
            initiatorStatic,
            AckExtension: (ephemeral, responderNonce) =>
            {
                // Ack 签名域升级为双方全材料（应答时全知——含 Init 材料 = 防替换）
                var ackTranscript = BuildTranscript(initiatorStatic, staticKey.PublicKey,
                    initiatorEph, ephemeral.PublicKey, initiatorNonce, responderNonce);
                var sig = staticKey.Sign(ackTranscript);
                return EncodeAckExtension(staticKey.PublicKey, ephemeral.PublicKey, responderNonce, sig);
            },
            Finish: (ephemeral, responderNonce) =>
                DeriveSession(ephemeral, initiatorEph, initiatorNonce, responderNonce,
                    initiatorSide: false, protection));
    }

    // ══ 扩展段编解码（追加在握手定长载荷之后——spec-12 §3.3 变长扩展）═══
    // ★ 扩展段自报静态公钥（TOFU 锚来源）：[静态公钥 33] + Init:[临时公钥 33][签名 64] /
    //   Ack:[临时公钥 33][responderNonce 8][签名 64]——签名域含双方静态公钥（S2 BuildTranscript），
    //   钉扎模式比对锚，TOFU 模式学习（验签成功后写回）。

    /// <summary>Init 扩展段长度（静态公钥 + 临时公钥 + 签名）。</summary>
    public const int InitExtensionSize = NodeKeyPair.PublicKeySize * 2 + NodeKeyPair.SignatureSize;

    /// <summary>Ack 扩展段长度（静态公钥 + 临时公钥 + responderNonce + 签名）。</summary>
    public const int AckExtensionSize = NodeKeyPair.PublicKeySize * 2 + sizeof(ulong) + NodeKeyPair.SignatureSize;

    /// <summary>编码 Init 扩展段（发起方：静态+临时公钥 + 静态私钥对 Init 材料的签名）。</summary>
    /// <param name="staticKey">本端静态密钥（签名）。</param>
    /// <param name="ephemeral">本端临时密钥（本次连接新建）。</param>
    /// <param name="initiatorNonce">Init Nonce。</param>
    /// <param name="destination">目标（≥ <see cref="InitExtensionSize"/>）。</param>
    public static void EncodeInitExtension(NodeKeyPair staticKey, NodeKeyPair ephemeral,
        ulong initiatorNonce, Span<byte> destination)
    {
        // Init 签名域：发起方静态+临时公钥+Nonce（responder 材料未知——段空，验证侧同域）
        var transcript = BuildTranscript(staticKey.PublicKey, default, ephemeral.PublicKey, default, initiatorNonce, 0);
        staticKey.PublicKey.CopyTo(destination[..NodeKeyPair.PublicKeySize]);
        ephemeral.PublicKey.CopyTo(destination.Slice(NodeKeyPair.PublicKeySize, NodeKeyPair.PublicKeySize));
        var sig = staticKey.Sign(transcript);
        sig.CopyTo(destination[(NodeKeyPair.PublicKeySize * 2)..]);
    }

    /// <summary>解码 Init 扩展段（静态公钥自报 + 临时公钥 + 签名）。</summary>
    public static (byte[] StaticKey, byte[] EphemeralKey, byte[] Signature) DecodeInitExtension(ReadOnlySpan<byte> ext)
    {
        if (ext.Length != InitExtensionSize) throw NewMalformed();
        var keySize = NodeKeyPair.PublicKeySize;
        return (
            ext[..keySize].ToArray(),
            ext.Slice(keySize, keySize).ToArray(),
            ext[(keySize * 2)..].ToArray());
    }

    private static byte[] EncodeAckExtension(ReadOnlySpan<byte> staticPublic, ReadOnlySpan<byte> ephemeralPublic,
        ulong responderNonce, byte[] signature)
    {
        var ext = new byte[AckExtensionSize];
        var keySize = NodeKeyPair.PublicKeySize;
        staticPublic.CopyTo(ext);
        ephemeralPublic.CopyTo(ext.AsSpan(keySize));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(ext.AsSpan(keySize * 2), responderNonce);
        signature.CopyTo(ext.AsSpan(keySize * 2 + sizeof(ulong)));
        return ext;
    }

    /// <summary>解码 Ack 扩展段（静态公钥自报 + 临时公钥 + responderNonce + 签名）。</summary>
    public static (byte[] StaticKey, byte[] EphemeralKey, ulong Nonce, byte[] Signature) DecodeAckExtension(ReadOnlySpan<byte> ext)
    {
        if (ext.Length != AckExtensionSize) throw NewMalformed();
        var keySize = NodeKeyPair.PublicKeySize;
        return (
            ext[..keySize].ToArray(),
            ext.Slice(keySize, keySize).ToArray(),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ext.Slice(keySize * 2, sizeof(ulong))),
            ext[(keySize * 2 + sizeof(ulong))..].ToArray());
    }

    private static FormatException NewMalformed()
        => new("握手扩展段长度不符（畸形/降级截断——Error + 断连）。");

    /// <summary>签名域（域分离标签 + 双方静态公钥 + 双方临时公钥 + 双方 Nonce——替换/重放材料全入域）。
    /// Responder 侧缺省段（Init 签名时）填零长度标记（长度前缀 1B——无歧义拼接）。</summary>
    private static byte[] BuildTranscript(ReadOnlySpan<byte> initiatorStatic, ReadOnlySpan<byte> responderStatic,
        ReadOnlySpan<byte> initiatorEph, ReadOnlySpan<byte> responderEph, ulong initNonce, ulong respNonce)
    {
        // 长度 = 标签 17B + 四段（各 1B 长度前缀 + 段体）+ 双 Nonce 16B
        var buffer = new byte[17 + 4 + 16 + initiatorStatic.Length + responderStatic.Length
            + initiatorEph.Length + responderEph.Length];
        var pos = 0;
        void Put(ReadOnlySpan<byte> segment)
        {
            buffer[pos++] = (byte)segment.Length;   // 长度前缀——变长段无歧义拼接
            segment.CopyTo(buffer.AsSpan(pos));
            pos += segment.Length;
        }
        void PutU64(ulong v)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(pos), v);
            pos += 8;
        }
        "tc-tier-net/v1/kp"u8.CopyTo(buffer); pos += 17;
        Put(initiatorStatic); Put(responderStatic); Put(initiatorEph); Put(responderEph);
        PutU64(initNonce); PutU64(respNonce);
        return buffer;
    }

    /// <summary>会话密钥派生：HKDF-SHA256（salt=双方 Nonce， ikm=临时 ECDH 共享，
    /// info=协议域标签）→ 双向密钥 + 确认密钥（方向按角色取段）。</summary>
    private static SecureSession DeriveSession(NodeKeyPair ownEphemeral, ReadOnlySpan<byte> peerEphemeral,
        ulong initNonce, ulong respNonce, bool initiatorSide, FrameProtection protection)
    {
        var shared = ownEphemeral.DeriveSharedSecret(peerEphemeral);
        var salt = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(salt, initNonce);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(salt.AsSpan(8), respNonce);
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeySize * 3,
            salt, "tc-tier-net/v1/kp-session"u8.ToArray());
        var send = okm[..KeySize].ToArray();    // 发起方发送段
        var recv = okm[KeySize..(KeySize * 2)].ToArray();   // 应答方发送段
        var confirm = okm[(KeySize * 2)..].ToArray();
        return initiatorSide
            ? new SecureSession(send, recv, confirm, protection)
            : new SecureSession(recv, send, confirm, protection);   // 应答方方向对调
    }
}
