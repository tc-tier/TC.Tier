using System.Security.Cryptography;
using FluentAssertions;
using TC.Tier.Core.Net.Wire;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Wire;

/// <summary>
/// 流内压缩包络（二期-E1——快照流逐帧"可选压缩"）：
/// 可压数据走 DEFLATE（更小）、不可压/小帧 raw 直通、往返逐字节一致、畸形包络防御。
/// </summary>
public class PayloadCompressionTests
{
    /// <summary>E1：可压数据——deflate 包络（更小）+ 往返逐字节一致。</summary>
    [Fact]
    public void Deflate_Compressible_RoundTripsSmaller()
    {
        var source = new byte[4096];
        Array.Fill(source, (byte)0x5A);

        var envelope = PayloadCompression.Deflate(source);

        envelope[0].Should().Be(PayloadCompression.KindDeflate, "可压数据采用 deflate");
        envelope.Length.Should().BeLessThan(source.Length, "压缩收益");
        var restored = PayloadCompression.Inflate(envelope);
        restored.Should().Equal(source, "往返逐字节一致");
    }

    /// <summary>E1：不可压数据（密码学随机 ≥ 阈值）——raw 直通（压缩只亏不赚时不采用）。</summary>
    [Fact]
    public void Deflate_Incompressible_FallsBackToRaw()
    {
        var source = RandomNumberGenerator.GetBytes(1024);

        var envelope = PayloadCompression.Deflate(source);

        envelope[0].Should().Be(PayloadCompression.KindRaw, "压缩无效则 raw 直通");
        envelope.Length.Should().Be(PayloadCompression.EnvelopeHeaderSize + source.Length);
        var restored = PayloadCompression.Inflate(envelope);
        restored.Should().Equal(source, "raw 往返逐字节一致");
    }

    /// <summary>E1：小帧（< 阈值）——raw 直通（压缩开销/收益不匹配）。</summary>
    [Fact]
    public void Deflate_SmallFrame_RawPassthrough()
    {
        var source = new byte[] { 0x01, 0x02, 0x03 };

        var envelope = PayloadCompression.Deflate(source);

        envelope[0].Should().Be(PayloadCompression.KindRaw, "低于起压阈值——raw 直通");
        PayloadCompression.Inflate(envelope).Should().Equal(source);
    }

    /// <summary>E1：畸形包络防御——截断抛、长度失配抛、种类未知抛。</summary>
    [Fact]
    public void Inflate_Malformed_Throws()
    {
        var shortEnvelope = new byte[] { PayloadCompression.KindRaw, 0x00 };
        var actShort = () => PayloadCompression.Inflate(shortEnvelope);
        actShort.Should().Throw<InvalidOperationException>("包络截断");

        var badLen = new byte[] { PayloadCompression.KindRaw, 0x02, 0x00, 0x00, 0x00, 0x01 };
        var actLen = () => PayloadCompression.Inflate(badLen);
        actLen.Should().Throw<InvalidOperationException>("长度失配");

        var badKind = new byte[] { 0x7F, 0x00, 0x00, 0x00, 0x00 };
        var actKind = () => PayloadCompression.Inflate(badKind);
        actKind.Should().Throw<InvalidOperationException>("种类未知");
    }
}
