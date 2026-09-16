using System.Text;
using FluentAssertions;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Wire;

/// <summary>
/// TransportError thin-layer tests (spec-12 §3.2——[Code][Count][Detail] via the generated
/// ErrorPayloadCodec; UTF-8 boundary conversion and length capping live in the thin layer).
/// </summary>
public class TransportErrorTests
{
    [Fact]
    public void Encode_TryDecode_RoundTripsCodeAndDetail()
    {
        var payload = TransportError.Encode(TransportError.VersionRejected, "no version overlap");

        TransportError.TryDecode(payload, out var code, out var detail).Should().BeTrue();
        code.Should().Be(TransportError.VersionRejected);
        detail.Should().Be("no version overlap");
    }

    [Fact]
    public void Encode_NullDetail_CountZero()
    {
        var payload = TransportError.Encode(TransportError.CrcMismatch);

        TransportError.TryDecode(payload, out var code, out var detail).Should().BeTrue();
        code.Should().Be(TransportError.CrcMismatch);
        detail.Should().BeNull("empty block decodes as absent detail");
        payload[0].Should().Be(TransportError.CrcMismatch, "Code stays at offset 0");
    }

    [Fact]
    public void Encode_LongDetail_TruncatedToMaxDetailLength()
    {
        var payload = TransportError.Encode(TransportError.HandshakeViolation, new string('x', 1000));

        TransportError.TryDecode(payload, out _, out var detail).Should().BeTrue();
        Encoding.UTF8.GetByteCount(detail!).Should().Be(TransportError.MaxDetailLength,
            "detail is truncated at the wire byte bound, never fails");
    }

    [Fact]
    public void TryDecode_Truncated_ReturnsFalse()
    {
        var payload = TransportError.Encode(TransportError.CrcMismatch, "boom");
        var act = () => TransportError.TryDecode(payload.AsSpan(..^1), out _, out _);
        act.Should().NotThrow();
        TransportError.TryDecode(payload.AsSpan(..^1), out _, out _).Should().BeFalse("truncated frame is rejected");
    }

    [Fact]
    public void TryDecode_CountOverLimit_ReturnsFalse()
    {
        // [Code 1B][Count 4B = MaxCount+1] — forged count must be rejected by the generated bound
        var forged = new byte[1 + 4];
        forged[0] = TransportError.CrcMismatch;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(forged.AsSpan(1), TransportError.MaxDetailLength + 1);
        TransportError.TryDecode(forged, out _, out _).Should().BeFalse();
    }
}
