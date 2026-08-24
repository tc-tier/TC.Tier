namespace TC.Tier.Core.Tests.Primitives;

public sealed class KeyComparerTests
{
    private readonly KeyComparer<int> _cmp = new();

    [Fact]
    public void GetHashCode64_SameKey_SameHash()
    {
        var h1 = _cmp.GetHashCode64(42);
        var h2 = _cmp.GetHashCode64(42);
        h1.Should().Be(h2);
    }

    [Fact]
    public void GetHashCode64_DifferentKeys_UsuallyDifferent()
    {
        var h1 = _cmp.GetHashCode64(1);
        var h2 = _cmp.GetHashCode64(99999);
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void GetHashCode64_ZeroKey_ReturnsNonZero()
        => _cmp.GetHashCode64(0).Should().NotBe(0u);

    [Fact]
    public void Equals_SameKey_ReturnsTrue()
        => _cmp.Equals(7, 7).Should().BeTrue();

    [Fact]
    public void Equals_DifferentKey_ReturnsFalse()
        => _cmp.Equals(1, 2).Should().BeFalse();

    [Fact]
    public void Compare_Greater_ReturnsPositive()
        => _cmp.Compare(10, 5).Should().BePositive();

    [Fact]
    public void Compare_Lesser_ReturnsNegative()
        => _cmp.Compare(5, 10).Should().BeNegative();

    [Fact]
    public void Compare_Equal_ReturnsZero()
        => _cmp.Compare(42, 42).Should().Be(0);

    [Fact]
    public void Compare_NegativeValues_Works()
    {
        _cmp.Compare(-1, -2).Should().BePositive();
        _cmp.Compare(-2, -1).Should().BeNegative();
        _cmp.Compare(-5, -5).Should().Be(0);
    }

    [Fact]
    public void HashCode_DifferentTypes_ByKeyOnly()
    {
        var cmpInt = new KeyComparer<int>();
        var cmpLong = new KeyComparer<long>();

        // XxHash64 of same byte-layout might differ since types have different sizes
        var h1 = cmpInt.GetHashCode64(1);
        var h2 = cmpLong.GetHashCode64(1L);
        // Same value, different byte sizes — usually different hashes
        h1.Should().NotBe(h2);
    }
}
