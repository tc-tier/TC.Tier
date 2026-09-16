using FluentAssertions;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

/// <summary>
/// Opaque4 contract tests (4B opaque byte string container — verbatim byte order, hex8 text form).
/// </summary>
public class Opaque4Tests
{
    [Fact]
    public void Ctor_CopyTo_RoundTripsInOriginalByteOrder()
    {
        var value = new Opaque4([0xC0, 0xA8, 0x0A, 0x2A]);

        Span<byte> roundTripped = stackalloc byte[Opaque4.Size];
        value.CopyTo(roundTripped);
        roundTripped.ToArray().Should().Equal([0xC0, 0xA8, 0x0A, 0x2A], "layout is a verbatim byte copy");
    }

    [Fact]
    public void Ctor_WrongLength_ThrowsArgumentException()
    {
        var act = () => new Opaque4(new byte[Opaque4.Size + 1]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_EmitsLowercaseHex8()
    {
        new Opaque4([0xC0, 0xA8, 0x0A, 0x2A]).ToString().Should().Be("c0a80a2a");
        Opaque4.Empty.ToString().Should().Be("00000000");
    }

    [Fact]
    public void EqualityContract()
    {
        var a = new Opaque4([1, 2, 3, 4]);
        var b = new Opaque4([1, 2, 3, 4]);
        var different = new Opaque4([1, 2, 3, 5]);

        (a == b).Should().BeTrue();
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a == different).Should().BeFalse();
    }
}
