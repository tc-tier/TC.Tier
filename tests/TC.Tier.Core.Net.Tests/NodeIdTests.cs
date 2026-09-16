using System.Buffers.Binary;
using FluentAssertions;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

/// <summary>
/// NodeId contract tests (spec-12 §2 — 16B opaque byte string, byte-order verbatim,
/// hex32 text form, sentinel, generation, byte-lexicographic total order).
/// </summary>
public class NodeIdTests
{
    [Fact]
    public void Ctor_CopyTo_RoundTripsInOriginalByteOrder()
    {
        // Endianness-sensitive pattern: any reinterpretation would scramble the order
        Span<byte> bytes = stackalloc byte[NodeId.Size];
        for (int i = 0; i < NodeId.Size; i++)
            bytes[i] = (byte)(i * 0x11);

        var id = new NodeId(bytes);

        Span<byte> roundTripped = stackalloc byte[NodeId.Size];
        id.CopyTo(roundTripped);
        roundTripped.ToArray().Should().Equal(bytes.ToArray(), "layout is a verbatim byte copy — no endianness interpretation");
    }

    [Fact]
    public void Ctor_WrongLength_ThrowsArgumentException()
    {
        var tooShort = () => new NodeId(new byte[NodeId.Size - 1]);
        tooShort.Should().Throw<ArgumentException>();

        var tooLong = () => new NodeId(new byte[NodeId.Size + 1]);
        tooLong.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CopyTo_DestinationTooSmall_ThrowsArgumentException()
    {
        var id = NodeId.NewRandom();
        var act = () => id.CopyTo(new byte[NodeId.Size - 1]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_IsAllZero_AndEqualsDefault()
    {
        var bytes = new byte[NodeId.Size];
        NodeId.Empty.CopyTo(bytes);
        bytes.Should().OnlyContain(b => b == 0);

        NodeId.Empty.Should().Be(default(NodeId));
        (NodeId.Empty == default(NodeId)).Should().BeTrue();
    }

    [Fact]
    public void ToString_EmitsLowercaseHex32WithoutSeparators()
    {
        var bytes = new byte[NodeId.Size];
        for (int i = 0; i < NodeId.Size; i++)
            bytes[i] = (byte)i;

        new NodeId(bytes).ToString().Should().Be("000102030405060708090a0b0c0d0e0f");
        NodeId.Empty.ToString().Should().Be(new string('0', 32));
    }

    [Fact]
    public void Parse_ToString_RoundTrips()
    {
        var id = NodeId.NewRandom();
        var text = id.ToString();

        text.Should().MatchRegex("^[0-9a-f]{32}$");
        NodeId.Parse(text).Should().Be(id);
    }

    [Fact]
    public void Parse_IsCaseInsensitive_AndZeroFormYieldsEmpty()
    {
        var id = NodeId.NewRandom();
        var text = id.ToString();

        NodeId.Parse(text.ToUpperInvariant()).Should().Be(id, "hex32 accepts both cases");
        NodeId.Parse(new string('0', 32)).Should().Be(NodeId.Empty);
    }

    [Theory]
    [InlineData("")]                                            // empty
    [InlineData("0123456789abcdef0123456789abcde")]             // 31 digits
    [InlineData("0123456789abcdef0123456789abcdeff")]           // 33 digits
    [InlineData("0123456789abcdef0123456789abcdeg")]            // non-hex character
    [InlineData("00010203-0405-0607-0809-0a0b0c0d0e0f")]        // Guid dashed form is rejected
    [InlineData("nid:000102030405060708090a0b0c0d0e0f")]        // SAN prefix is not Parse input
    public void Parse_InvalidText_ThrowsFormatException(string text)
    {
        var act = () => NodeId.Parse(text);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void NewRandom_NeverYieldsEmpty_AndDoesNotCollide()
    {
        var seen = new HashSet<NodeId>();
        for (int i = 0; i < 1_000; i++)
        {
            var id = NodeId.NewRandom();
            id.Should().NotBe(NodeId.Empty);
            seen.Add(id).Should().BeTrue("128-bit random does not collide at thousand-scale samples");
        }
    }

    [Fact]
    public void NewSequential_TimeOrderedPrefixWithRandomTail()
    {
        var seen = new HashSet<NodeId>();
        long lastPrefix = long.MinValue;

        for (int i = 0; i < 1_000; i++)
        {
            var id = NodeId.NewSequential();
            seen.Add(id).Should().BeTrue("80-bit random tail distinguishes ids within the same millisecond");

            var bytes = new byte[NodeId.Size];
            id.CopyTo(bytes);
            // First 8 bytes big-endian, shifted right 16 bits = the 48-bit timestamp prefix (v7-style layout)
            long prefix = BinaryPrimitives.ReadInt64BigEndian(bytes) >> 16;
            prefix.Should().BeGreaterThan(1_700_000_000_000L, "unix-millisecond magnitude (post-2023)");
            prefix.Should().BeGreaterThanOrEqualTo(lastPrefix, "byte-lexicographic order = time order under a monotonic clock");
            lastPrefix = prefix;
        }
    }

    [Fact]
    public void EqualityContract_Operator_BoxedForm_HashCode()
    {
        var bytes = new byte[NodeId.Size];
        Random.Shared.NextBytes(bytes);
        var a = new NodeId(bytes);
        var b = new NodeId(bytes);
        var different = new NodeId([0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.Equals(b).Should().BeTrue();
        a.Equals((object?)b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());

        (a == different).Should().BeFalse();
        (a != different).Should().BeTrue();
        a.Equals(different).Should().BeFalse();
        a.Equals((object?)different).Should().BeFalse();
    }

    [Fact]
    public void CompareTo_IsByteLexicographicTotalOrder()
    {
        var bytes = new byte[NodeId.Size];
        for (int i = 0; i < NodeId.Size; i++)
            bytes[i] = (byte)i;
        var a = new NodeId(bytes);

        a.CompareTo(a).Should().Be(0);

        // byte[0] is the most significant position — the tail is irrelevant
        var biggerFirstByte = new NodeId([0x01, .. bytes[1..]]);
        biggerFirstByte.CompareTo(a).Should().BePositive();
        a.CompareTo(biggerFirstByte).Should().BeNegative();

        // equal leading byte: the next byte decides
        var biggerSecondByte = new NodeId([bytes[0], 0xFF, .. bytes[2..]]);
        biggerSecondByte.CompareTo(a).Should().BePositive();

        // the last byte decides only when all preceding bytes match
        var biggerTail = new NodeId([.. bytes[..^1], (byte)(bytes[^1] + 1)]);
        biggerTail.CompareTo(a).Should().BePositive();
        // NewSequential's time-ordered prefix ⇒ lexicographic non-decreasing across samples
        // (already asserted per-sample in NewSequential_TimeOrderedPrefixWithRandomTail; a bare
        // adjacent pair is NOT monotonic — same-millisecond samples order by random tail)
    }

    [Fact]
    public void NodeId_UsableAsDictionaryKey()
    {
        var map = new Dictionary<NodeId, string>();
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();

        map[a] = "a";
        map[b] = "b";

        map[a].Should().Be("a");
        map[b].Should().Be("b");
        map.Count.Should().Be(2);
    }
}
