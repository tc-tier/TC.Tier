namespace TC.Tier.Core.Tests.Epochs;

public class VersionSchemeStateTests
{
    [Fact]
    public void Default_RestPhase_VersionZero()
    {
        var s = default(VersionSchemeState);
        s.Phase.Should().Be(VersionSchemeState.Rest);
        s.Version.Should().Be(0);
    }

    [Fact]
    public void Make_SetsPhaseAndVersion()
    {
        var s = VersionSchemeState.Make(phase: 3, version: 42);
        s.Phase.Should().Be(3);
        s.Version.Should().Be(42);
    }

    [Fact]
    public void Make_RestPhase_VersionPreserved()
    {
        var s = VersionSchemeState.Make(VersionSchemeState.Rest, 100);
        s.Phase.Should().Be(VersionSchemeState.Rest);
        s.Version.Should().Be(100);
    }

    [Fact]
    public void IsIntermediate_DefaultFalse()
    {
        var s = VersionSchemeState.Make(phase: 5, version: 1);
        s.IsIntermediate().Should().BeFalse();
    }

    [Fact]
    public void MakeIntermediate_SetsIntermediateFlag()
    {
        var original = VersionSchemeState.Make(phase: 5, version: 10);
        var inter = VersionSchemeState.MakeIntermediate(original);
        inter.IsIntermediate().Should().BeTrue();
        inter.Version.Should().Be(10);
    }

    [Fact]
    public void MakeIntermediate_PreservesPhaseLowerBits()
    {
        var original = VersionSchemeState.Make(phase: 3, version: 7);
        var inter = VersionSchemeState.MakeIntermediate(original);
        // intermediate mask = 128, so phase = 3 | 128 = 131
        inter.Phase.Should().Be(3 | 128);
    }

    [Fact]
    public void RemoveIntermediate_ClearsIntermediateFlag()
    {
        var original = VersionSchemeState.Make(phase: 3, version: 7);
        var inter = VersionSchemeState.MakeIntermediate(original);
        VersionSchemeState.RemoveIntermediate(ref inter);
        inter.IsIntermediate().Should().BeFalse();
        inter.Phase.Should().Be(3);
        inter.Version.Should().Be(7);
    }

    [Fact]
    public void Equal_SameWord_True()
    {
        var s1 = VersionSchemeState.Make(phase: 2, version: 5);
        var s2 = VersionSchemeState.Make(phase: 2, version: 5);
        VersionSchemeState.Equal(s1, s2).Should().BeTrue();
    }

    [Fact]
    public void Equal_DifferentPhase_False()
    {
        var s1 = VersionSchemeState.Make(phase: 1, version: 5);
        var s2 = VersionSchemeState.Make(phase: 2, version: 5);
        VersionSchemeState.Equal(s1, s2).Should().BeFalse();
    }

    [Fact]
    public void Equal_DifferentVersion_False()
    {
        var s1 = VersionSchemeState.Make(phase: 1, version: 5);
        var s2 = VersionSchemeState.Make(phase: 1, version: 6);
        VersionSchemeState.Equal(s1, s2).Should().BeFalse();
    }

    [Fact]
    public void Copy_ProducesIdenticalWord()
    {
        var original = VersionSchemeState.Make(phase: 4, version: 99);
        var s = default(VersionSchemeState);
        s.Word = original.Word;
        s.Phase.Should().Be(original.Phase);
        s.Version.Should().Be(original.Version);
    }

    [Fact]
    public void Phase_MaxValue_DoesNotCorruptVersion()
    {
        // Phase is 8 bits (0-255); max phase should not overflow into version bits
        var s = VersionSchemeState.Make(phase: 255, version: 12345);
        s.Phase.Should().Be(255);
        s.Version.Should().Be(12345);
    }

    [Fact]
    public void Version_LargeValue_PreservedCorrectly()
    {
        // Version is 56 bits; large value should fit
        var s = VersionSchemeState.Make(phase: 0, version: long.MaxValue >> 8);
        s.Version.Should().Be(long.MaxValue >> 8);
    }

    [Fact]
    public void PhaseSetter_OverwritesPreviousPhase()
    {
        var s = VersionSchemeState.Make(phase: 5, version: 10);
        s.Phase = VersionSchemeState.Rest;
        s.Phase.Should().Be(VersionSchemeState.Rest);
        s.Version.Should().Be(10);
    }

    [Fact]
    public void ToString_Format()
    {
        var s = VersionSchemeState.Make(phase: 3, version: 7);
        s.ToString().Should().Be("[3,7]");
    }

    [Fact]
    public void EqualsOperator_SameValues_True()
    {
        var s1 = VersionSchemeState.Make(phase: 1, version: 2);
        var s2 = VersionSchemeState.Make(phase: 1, version: 2);
        (s1 == s2).Should().BeTrue();
        (s1 != s2).Should().BeFalse();
    }

    [Fact]
    public void EqualsOperator_DifferentValues_False()
    {
        var s1 = VersionSchemeState.Make(phase: 1, version: 2);
        var s2 = VersionSchemeState.Make(phase: 1, version: 3);
        (s1 == s2).Should().BeFalse();
        (s1 != s2).Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_SameWord_SameHash()
    {
        var s1 = VersionSchemeState.Make(phase: 2, version: 5);
        var s2 = VersionSchemeState.Make(phase: 2, version: 5);
        s1.GetHashCode().Should().Be(s2.GetHashCode());
    }

    [Fact]
    public void EqualsObject_Null_False()
    {
        var s = VersionSchemeState.Make(phase: 1, version: 1);
        s.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void EqualsObject_DifferentType_False()
    {
        var s = VersionSchemeState.Make(phase: 1, version: 1);
        s.Equals("not a state").Should().BeFalse();
    }
}
