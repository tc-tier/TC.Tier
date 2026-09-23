using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TC.Tier.CodeGen;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Security;

/// <summary>
/// 安全记录层（spec-12 §3.4——KeyPair 档握手后的全帧保护）。
/// <para>★ 记录形态（TCP 流）：[长度 4B][计数器 8B][载荷]——载荷按粒度 = 密文+16B AEAD 标签
///   （<see cref="FrameProtection.Aead"/>：AES-256-GCM，全帧加密+认证）∥ 明文帧+32B HMAC
///   （<see cref="FrameProtection.MacOnly"/>：仅完整性——防冒充/伪造/篡改不加密）。解包后
///   的字节 = 标准帧（16B 帧头 + payload），下游帧解析零改动。</para>
/// <para>★ 计数器 nonce（NIST SP 800-38D 形态）：12B = 4B 零填充 ‖ 8B 发送计数器——每方向
///   独立计数；接收侧严格递增校验（重放/乱序拒绝——TCP 有序流上计数器回退 = 篡改/重放）。</para>
/// </summary>
public sealed class SecureRecordCodec : IDisposable
{
    /// <summary>记录头长度（长度 4B + 计数器 8B）。</summary>
    public const int RecordHeaderSize = 12;

    /// <summary>AEAD 标签长度（GCM-128/256 同为 16B）。</summary>
    public const int AeadTagSize = 16;

    /// <summary>HMAC-SHA256 标签长度（MAC-only 粒度）。</summary>
    public const int MacTagSize = 32;

    /// <summary>单条记录载荷上限（明文帧 ≤ 16MB 帧协议上限 + 密文开销——畸形防御界；
    /// 尺寸从生成物/帧协议常量派生，禁手写布局值）。</summary>
    public static int MaxRecordBytes => (int)FrameCodec.MaxPayloadLength + FrameCodec.HeaderSize + AeadTagSize + RecordHeaderSize;

    /// <summary>
    /// 安全记录头（12B 定长：[长度 4B][计数器 8B]——<b>BigEndian</b>，TLS 记录风格，
    /// 全仓唯一 BE 线格式消息）。[BinaryLayout] 单一声明——长度/计数器偏移与端序收口生成器，
    /// Seal/Open 不再手写偏移；载荷（密文+AEAD 标签 / 明文+HMAC）真变长，域代码。
    /// </summary>
    [BinaryLayout(Endianness = LayoutEndianness.BigEndian, Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = 12)]
    internal struct SecureRecordHeader
    {
        /// <summary>载荷长度（记录总长 - 头 12B；BigEndian）。</summary>
        [FieldOffset(0)] public int Length;

        /// <summary>发送计数器（每方向独立递增——接收侧严格递增校验防重放）。</summary>
        [FieldOffset(4)] public long Counter;
    }

    private readonly byte[] _sendKey;
    private readonly byte[] _recvKey;
    private readonly FrameProtection _protection;
    private readonly object _sendLock = new();   // 发送计数器+密码件互斥（多协议域并发写）
    private readonly object _recvLock = new();   // 接收计数器+密码件互斥（Open 侧同责）
    private AesGcm? _sendAead;
    private AesGcm? _recvAead;
    private HMACSHA256? _sendMac;
    private HMACSHA256? _recvMac;
    private long _sendCounter = -1;
    private long _recvCounter = -1;

    /// <summary>构造（会话密钥按方向取——<see cref="SecureSession"/> 产物；
    /// ★独立持有密钥副本——session 先释放不影响已建立的记录层，本类 Dispose 自清）。</summary>
    /// <param name="session">握手产物（方向键已按角色对正）。</param>
    public SecureRecordCodec(SecureSession session)
    {
        _sendKey = session.SendKey.ToArray();
        _recvKey = session.RecvKey.ToArray();
        _protection = session.Protection;
        if (_protection == FrameProtection.Aead)
        {
            _sendAead = new AesGcm(_sendKey, AeadTagSize);
            _recvAead = new AesGcm(_recvKey, AeadTagSize);
        }
        else
        {
            _sendMac = new HMACSHA256(_sendKey);
            _recvMac = new HMACSHA256(_recvKey);
        }
    }

    /// <summary>包装一帧（整帧字节 → 记录字节）。线程安全（发送计数器+密码件全程互斥——
    /// AesGcm/HMACSHA256 单实例非线程安全：OpenSSL 路径缓存共享 EVP_CIPHER_CTX，
    /// 并发 Encrypt 竞态 → "cipher operation failed"；锁须覆盖取号→加密全程，保计数器与
    /// nonce/密文原子配对）。</summary>
    /// <param name="frame">明文整帧（16B 帧头 + payload——<see cref="FrameCodec"/> 产物）。</param>
    /// <returns>记录（[长度 4B][计数器 8B][载荷]）。</returns>
    public byte[] Seal(ReadOnlySpan<byte> frame)
    {
        byte[] record;
        lock (_sendLock)
        {
            _sendCounter++;
            var counter = _sendCounter;
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteInt64BigEndian(nonce[4..], counter);

            if (_protection == FrameProtection.Aead)
            {
                record = new byte[RecordHeaderSize + frame.Length + AeadTagSize];
                _sendAead!.Encrypt(nonce, frame,
                    record.AsSpan(RecordHeaderSize, frame.Length),      // ciphertext 目标 = 明文等长（tag 独立参数）
                    record.AsSpan(RecordHeaderSize + frame.Length, AeadTagSize));
            }
            else
            {
                record = new byte[RecordHeaderSize + frame.Length + MacTagSize];
                frame.CopyTo(record.AsSpan(RecordHeaderSize));
                // MAC 域 = 帧体（计数器防重放由严格递增校验独立承担——篡改计数器即验证失败断连）
                _sendMac!.TryComputeHash(frame, record.AsSpan(RecordHeaderSize + frame.Length), out _);
            }
            SecureRecordHeaderCodec.Write(record, new SecureRecordHeader
            {
                Length = record.Length - RecordHeaderSize,
                Counter = counter,
            }, validate: false);
        }
        return record;
    }

    /// <summary>解开一条记录（记录载荷 → 明文整帧）。</summary>
    /// <param name="header">记录头（[长度 4B][计数器 8B]——长度域由调用方切流后传入校验）。</param>
    /// <param name="payload">记录载荷（长度域声明的字节）。</param>
    /// <returns>明文整帧。</returns>
    /// <exception cref="CryptographicException">认证失败 / 计数器回退（重放防御）。</exception>
    public byte[] Open(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) throw new CryptographicException("空记录载荷。");
        lock (_recvLock)
        {
            var counter = SecureRecordHeaderCodec.Read(header).Counter;
            if (counter <= _recvCounter)
                throw new CryptographicException($"记录计数器回退（{counter} ≤ {_recvCounter}——重放/篡改，断连）。");
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteInt64BigEndian(nonce[4..], counter);

            byte[] frame;
            if (_protection == FrameProtection.Aead)
            {
                if (payload.Length < AeadTagSize) throw new CryptographicException("密文短于 AEAD 标签。");
                var tag = payload[^AeadTagSize..];
                frame = new byte[payload.Length - AeadTagSize];
                _recvAead!.Decrypt(nonce, payload[..^AeadTagSize], tag, frame);
            }
            else
            {
                if (payload.Length < MacTagSize) throw new CryptographicException("明文短于 HMAC 标签。");
                var body = payload[..^MacTagSize];
                frame = body.ToArray();
                Span<byte> expected = stackalloc byte[MacTagSize];
                _recvMac!.TryComputeHash(body, expected, out _);
                if (!CryptographicOperations.FixedTimeEquals(expected, payload[^MacTagSize..]))
                    throw new CryptographicException("MAC 校验失败（篡改/错键——断连）。");
            }
            _recvCounter = counter;
            return frame;
        }
    }

    /// <summary>销毁密码件（独立持有的密钥副本一并清零）。</summary>
    public void Dispose()
    {
        _sendAead?.Dispose();
        _recvAead?.Dispose();
        _sendMac?.Dispose();
        _recvMac?.Dispose();
        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_recvKey);
    }
}
