using System.Net;
using FluentAssertions;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Wire;

/// <summary>
/// Negotiate UDP endpoint announcement tests (spec-12 §4.5 bit1——V4/V6 declared layouts,
/// Tag/Family pinned by [ValidEquals] defensive completion; thin layer only dispatches and
/// converts IPAddress at the boundary).
/// </summary>
public class NegotiateCodecTests
{
    [Fact]
    public void UdpEndpointV4_EncodeDecode_RoundTrips()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("192.168.10.42"), 53123);

        var payload = NegotiateCodec.EncodeUdpEndpoint(endpoint);

        payload.Length.Should().Be(UdpEndpointV4Codec.StructSize);
        payload[0].Should().Be(NegotiateCodec.TagUdpEndpoint);
        payload[1].Should().Be(NegotiateCodec.FamilyIPv4);
        NegotiateCodec.TryReadUdpEndpoint(payload, out var decoded).Should().BeTrue();
        decoded.Should().Be(endpoint);
    }

    [Fact]
    public void UdpEndpointV6_EncodeDecode_RoundTrips()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("2001:db8::ff:1234"), 9999);

        var payload = NegotiateCodec.EncodeUdpEndpoint(endpoint);

        payload.Length.Should().Be(UdpEndpointV6Codec.StructSize);
        NegotiateCodec.TryReadUdpEndpoint(payload, out var decoded).Should().BeTrue();
        decoded.Should().Be(endpoint);
    }

    [Fact]
    public void DefensiveCompletion_WrongTagAndFamilyWrittenAsConstants()
    {
        // [ValidEquals] + Write(validate:true)：Tag/Family 是规范字段——入参传错也被强制写常量
        var payload = new UdpEndpointV4(0xFF, 0xEE, 1234, new Opaque4([1, 2, 3, 4]));
        var buffer = new byte[UdpEndpointV4Codec.StructSize];
        UdpEndpointV4Codec.Write(buffer, in payload, validate: true);

        buffer[0].Should().Be(NegotiateCodec.TagUdpEndpoint, "validate 补全规范 Tag");
        buffer[1].Should().Be(NegotiateCodec.FamilyIPv4, "validate 补全规范 Family");
    }

    [Fact]
    public void TryReadUdpEndpoint_WrongTagOrFamily_ReturnsFalse()
    {
        var payload = NegotiateCodec.EncodeUdpEndpoint(new IPEndPoint(IPAddress.Loopback, 1));
        payload[0] = 0x7F;   // 未知 Tag
        NegotiateCodec.TryReadUdpEndpoint(payload, out _).Should().BeFalse();

        var family = NegotiateCodec.EncodeUdpEndpoint(new IPEndPoint(IPAddress.Loopback, 1));
        family[1] = NegotiateCodec.FamilyIPv6;   // v4 长度 + v6 家族 = 形态不符
        NegotiateCodec.TryReadUdpEndpoint(family, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadUdpEndpoint_WrongLength_ReturnsFalse()
    {
        NegotiateCodec.TryReadUdpEndpoint(new byte[7], out _).Should().BeFalse();
        NegotiateCodec.TryReadUdpEndpoint([], out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadTag_EmptyPayload_ReturnsFalse()
    {
        NegotiateCodec.TryReadTag([], out _).Should().BeFalse();
        NegotiateCodec.TryReadTag([0x01], out var tag).Should().BeTrue();
        tag.Should().Be(NegotiateCodec.TagUdpEndpoint);
    }
}
