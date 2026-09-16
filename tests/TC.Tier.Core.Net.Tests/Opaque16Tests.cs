using FluentAssertions;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

/// <summary>
/// Opaque16 contract tests (16B opaque byte string container — verbatim byte order,
/// hex32 text form, byte-lexicographic total order; NodeId composes it as sole layout source).
/// </summary>
public class Opaque16Tests
{
    [Fact]
    public void Ctor_CopyTo_RoundTripsInOriginalByteOrder()
    {
        Span<byte> bytes = stackalloc byte[Opaque16.Size];
        for (int i = 0; i < Opaque16.Size; i++)
            bytes[i] = (byte)(i * 0x11);

        var value = new Opaque16(bytes);

        Span<byte> roundTripped = stackalloc byte[Opaque16.Size];
        value.CopyTo(roundTripped);
        roundTripped.ToArray().Should().Equal(bytes.ToArray(), "layout is a verbatim byte copy — no endianness interpretation");
    }

    [Fact]
    public void Ctor_WrongLength_ThrowsArgumentException()
    {
        var tooShort = () => new Opaque16(new byte[Opaque16.Size - 1]);
        tooShort.Should().Throw<ArgumentException>();
        var tooLong = () => new Opaque16(new byte[Opaque16.Size + 1]);
        tooLong.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ToString_RoundTrips_CaseInsensitive()
    {
        var bytes = new byte[Opaque16.Size];
        Random.Shared.NextBytes(bytes);
        var value = new Opaque16(bytes);

        value.ToString().Should().MatchRegex("^[0-9a-f]{32}$");
        Opaque16.Parse(value.ToString()).Should().Be(value);
        Opaque16.Parse(value.ToString().ToUpperInvariant()).Should().Be(value);
        Opaque16.Parse(new string('0', 32)).Should().Be(Opaque16.Empty);
    }

    [Fact]
    public void CompareTo_IsByteLexicographic()
    {
        var bytes = new byte[Opaque16.Size];
        for (int i = 0; i < Opaque16.Size; i++)
            bytes[i] = (byte)i;
        var a = new Opaque16(bytes);

        a.CompareTo(a).Should().Be(0);
        new Opaque16([0x01, .. bytes[1..]]).CompareTo(a).Should().BePositive("byte[0] is the most significant position");
        new Opaque16([bytes[0], 0xFF, .. bytes[2..]]).CompareTo(a).Should().BePositive("second byte decides on equal lead");
        new Opaque16([.. bytes[..^1], (byte)(bytes[^1] + 1)]).CompareTo(a).Should().BePositive("last byte decides last");
    }

    [Fact]
    public void NodeId_Composition_SharesTheSameBytesAndOrder()
    {
        // NodeId 组合 Opaque16：身份语义包装与通用容器承载同一字节串——同序同值
        var id = NodeId.NewRandom();
        Span<byte> viaNodeId = stackalloc byte[NodeId.Size];
        id.CopyTo(viaNodeId);

        var opaque = new Opaque16(viaNodeId);
        Span<byte> viaOpaque = stackalloc byte[Opaque16.Size];
        opaque.CopyTo(viaOpaque);

        viaOpaque.ToArray().Should().Equal(viaNodeId.ToArray());
        opaque.ToString().Should().Be(id.ToString());
    }
}
