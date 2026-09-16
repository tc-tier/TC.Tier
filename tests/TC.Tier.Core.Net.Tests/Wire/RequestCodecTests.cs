using FluentAssertions;
using TC.Tier.Core.Net.Wire;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Wire;

/// <summary>
/// RequestCodec 契约测试（spec-12 §5.2——[CorrId 8B][payload] 前缀拆装；布局知识在
/// [BinaryLayout] 声明 CorrelationPrefix，本层只验证边界与往返）。
/// </summary>
public class RequestCodecTests
{
    [Fact]
    public void 编码解码_往返一致()
    {
        var payload = new byte[] { 1, 2, 3, 0xFF, 0x10 };
        var buffer = new byte[RequestCodec.PrefixSize + payload.Length];

        int written = RequestCodec.EncodeInto(0x0123_4567_89AB_CDEFUL, payload, buffer);
        written.Should().Be(RequestCodec.PrefixSize + payload.Length);

        RequestCodec.TryRead(buffer, out var corrId, out var decoded).Should().BeTrue();
        corrId.Should().Be(0x0123_4567_89AB_CDEFUL);
        decoded.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void 编码_空载荷_仅前缀()
    {
        var buffer = new byte[RequestCodec.PrefixSize];
        RequestCodec.EncodeInto(42, ReadOnlySpan<byte>.Empty, buffer);
        RequestCodec.TryRead(buffer, out var corrId, out var decoded).Should().BeTrue();
        corrId.Should().Be(42);
        decoded.Length.Should().Be(0, "空应答/空请求合法——长度由帧头 PayloadLen 权威");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void 解码_载荷短于前缀_false(int length)
    {
        RequestCodec.TryRead(new byte[length], out _, out _).Should().BeFalse("违规帧——丢弃计数不致命");
    }

    [Fact]
    public void 前缀尺寸_与布局声明同源()
    {
        RequestCodec.PrefixSize.Should().Be(CorrelationPrefixCodec.StructSize);
        RequestCodec.PrefixSize.Should().Be(8, "spec-12 §5.2 [CorrId 8B]");
    }

    [Fact]
    public void 编码_小端字节序锁定()
    {
        var buffer = new byte[RequestCodec.PrefixSize];
        RequestCodec.EncodeInto(0x1122334455667788UL, ReadOnlySpan<byte>.Empty, buffer);
        // 小端：最低有效字节在前（spec-12 §3.1 全小端）
        buffer.Should().Equal(0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11);
    }
}
