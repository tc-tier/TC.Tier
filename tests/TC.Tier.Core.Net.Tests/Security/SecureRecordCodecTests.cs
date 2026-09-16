using System.Security.Cryptography;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Tests.Security;

/// <summary>
/// SecureRecordCodec 契约测试（spec-12 §3.4 记录层——AEAD/MAC-only 两粒度）：
/// 往返（Seal→Open 还原整帧）、密文不可读（AEAD）、篡改拒、错方向键拒、
/// 计数器回退/重放拒、多记录顺序推进、真实帧封装往返。
/// </summary>
public class SecureRecordCodecTests : IDisposable
{
    private readonly NodeKeyPair _iStatic = NodeKeyPair.Generate();
    private readonly NodeKeyPair _rStatic = NodeKeyPair.Generate();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _iStatic.Dispose();
        _rStatic.Dispose();
    }

    /// <summary>AEAD 往返：双侧 codec（同一握手）Seal→Open 还原帧，计数器顺序推进。</summary>
    [Theory]
    [InlineData(FrameProtection.Aead)]
    [InlineData(FrameProtection.MacOnly)]
    public void SealOpen_RoundTrip(FrameProtection protection)
    {
        var (send, recv) = CreatePair(protection); using var _s = send; using var _r = recv;
        {
            var frame = new byte[100];
            Random.Shared.NextBytes(frame);
            var record = send.Seal(frame);
            record.Length.Should().Be(SecureRecordCodec.RecordHeaderSize + frame.Length
                + (protection == FrameProtection.Aead ? SecureRecordCodec.AeadTagSize : SecureRecordCodec.MacTagSize));
            OpenRecord(recv, record).Should().Equal(frame);

            // 顺序多记录
            var frame2 = new byte[50];
            OpenRecord(recv, send.Seal(frame2)).Should().Equal(frame2);
        }
    }

    /// <summary>AEAD 机密性：密文随机化（全帧加密——全 0xAB 帧的密文中 0xAB 密度应接近 1/256）。
    /// 阈值 0.05（判例 2026-09-02）：272 字节窗口下 0.02 阈值被 6/272=0.022 的纯随机波动击穿
    /// （P(≥6)≈5e-4/轮——混跑 40 轮必现）；0.05 对应 P(≥14)≈3e-12，仍对明文透出（密度≈1）零漏判。</summary>
    [Fact]
    public void Aead_PayloadNotPlaintext()
    {
        var (send, recv) = CreatePair(FrameProtection.Aead); using var _s = send; using var _r = recv;
        {
            var frame = new byte[256];
            frame.AsSpan().Fill(0xAB);
            var record = send.Seal(frame);
            var payload = record.AsSpan(SecureRecordCodec.RecordHeaderSize);
            var abCount = 0;
            foreach (var b in payload) if (b == 0xAB) abCount++;
            (abCount / (double)payload.Length).Should().BeLessThan(0.05,
                "全帧加密——0xAB 密度应接近随机 1/256（明文透出 = 加密失效）");
        }
    }

    /// <summary>篡改拒：载荷一字节翻转 → AEAD/MAC 验证失败抛（断连路径）。</summary>
    [Theory]
    [InlineData(FrameProtection.Aead)]
    [InlineData(FrameProtection.MacOnly)]
    public void TamperedPayload_Rejected(FrameProtection protection)
    {
        var (send, recv) = CreatePair(protection); using var _s = send; using var _r = recv;
        {
            var record = send.Seal(new byte[80]);
            record[^7] ^= 0xFF;
            var act = () => OpenRecord(recv, record);
            act.Should().Throw<CryptographicException>();
        }
    }

    /// <summary>计数器回退拒（重放）：同一记录重放 → 第二次抛。</summary>
    [Theory]
    [InlineData(FrameProtection.Aead)]
    [InlineData(FrameProtection.MacOnly)]
    public void Replay_Rejected(FrameProtection protection)
    {
        var (send, recv) = CreatePair(protection); using var _s = send; using var _r = recv;
        {
            var record = send.Seal(new byte[40]);
            OpenRecord(recv, record);   // 第一次通过
            var act = () => OpenRecord(recv, record);
            act.Should().Throw<CryptographicException>("计数器回退 = 重放——断连");
        }
    }

    /// <summary>乱序拒（跳前记录再收旧记录）：计数器必须严格递增。</summary>
    [Fact]
    public void OutOfOrder_Rejected()
    {
        var (send, recv) = CreatePair(FrameProtection.Aead); using var _s = send; using var _r = recv;
        {
            var r1 = send.Seal(new byte[10]);
            var r2 = send.Seal(new byte[10]);
            OpenRecord(recv, r2);   // 跳过 r1 直收 r2（计数器 1）
            var act = () => OpenRecord(recv, r1);   // 旧计数器 0
            act.Should().Throw<CryptographicException>();
        }
    }

    /// <summary>错方向键拒：A 的发送键开 A 自己的记录（方向键分离——自反攻击）。</summary>
    [Fact]
    public void WrongDirectionKey_Rejected()
    {
        var (send, recv) = CreatePair(FrameProtection.Aead); using var _s = send; using var _r = recv;
        {
            var record = send.Seal(new byte[30]);
            var act = () => OpenRecord(send, record);   // 自己开自己的（recv 键 ≠ 对端 send 键）
            act.Should().Throw<CryptographicException>();
        }
    }

    /// <summary>真实帧封装往返：FrameCodec.Encode 产物整帧过记录层，对端解出后帧头/载荷完整。</summary>
    [Fact]
    public void RealFrame_RoundTrip()
    {
        var (send, recv) = CreatePair(FrameProtection.Aead); using var _s = send; using var _r = recv;
        {
            var payload = new byte[64];
            Random.Shared.NextBytes(payload);
            var frame = new byte[FrameCodec.HeaderSize + payload.Length];
            FrameCodec.Encode(FrameKind.Request, ChannelIds.Datagram, 0x60, payload, frame);
            var opened = OpenRecord(recv, send.Seal(frame));
            FrameCodec.TryDecode(opened, out var header, out var decoded).Should().BeTrue("解包后 = 标准帧");
            header.Kind.Should().Be(FrameKind.Request);
            decoded.ToArray().Should().Equal(payload);
        }
    }

    /// <summary>并发 Seal 线程安全（发送计数器互斥——计数器不重用）。</summary>
    [Fact]
    public async Task ConcurrentSeal_DistinctCounters()
    {
        var (send, recv) = CreatePair(FrameProtection.Aead); using var _s = send; using var _r = recv;
        {
            var records = new byte[64][];
            await Task.WhenAll(Enumerable.Range(0, 64).Select(async i =>
            {
                await Task.Yield();
                records[i] = send.Seal(new byte[16]);
            }));
            records.DistinctBy(r => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(r.AsSpan(4)))
                .Should().HaveCount(64, "并发下计数器不重用（nonce 重用 = GCM 灾难）");
        }
    }

    private static byte[] OpenRecord(SecureRecordCodec codec, byte[] record)
        => codec.Open(record.AsSpan(0, SecureRecordCodec.RecordHeaderSize),
            record.AsSpan(SecureRecordCodec.RecordHeaderSize));

    private (SecureRecordCodec Sender, SecureRecordCodec Receiver) CreatePair(FrameProtection protection)
    {
        // 双侧握手（S2 测试同款路径）→ 各自建 codec
        var initNonce = (ulong)Random.Shared.NextInt64();
        using var initEph = NodeKeyPair.Generate();
        using var respEph = NodeKeyPair.Generate();
        var respNonce = (ulong)Random.Shared.NextInt64();
        var initExt = new byte[SecureSession.InitExtensionSize];
        SecureSession.EncodeInitExtension(_iStatic, initEph, initNonce, initExt);
        var (_, ackExt, finish) = SecureSession.StartAsResponder(
            _rStatic, initExt, initNonce, protection);
        var ack = ackExt(respEph, respNonce);
        using var respSession = finish(respEph, respNonce);
        var initSession = SecureSession.FinishAsInitiator(
            _iStatic, initEph, initNonce, ack, protection).Session;
        return (new SecureRecordCodec(initSession), new SecureRecordCodec(respSession));
    }
}
