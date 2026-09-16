using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.P2P;

namespace TC.Tier.Core.Net.Tests.P2P;

/// <summary>
/// HyParView membership options contract tests (spec-06 §2 defaults × spec-12 §4.5
/// runtime parameters fully overridable — defaults serve zero-config start only).
/// </summary>
public class PeerOptionsTests
{
    [Fact]
    public void Defaults_MatchSpec06()
    {
        var o = PeerOptions.Default;
        o.ActiveViewSize.Should().Be(4, "active view k");
        o.PassiveViewSize.Should().Be(20, "passive view bound");
        o.JoinTtl.Should().Be(6, "join random walk TTL");
        o.JoinWaitTimeout.Should().Be(TimeSpan.FromSeconds(5), "bounded join wait");
        o.ShuffleInterval.Should().Be(TimeSpan.FromSeconds(60));
        o.HeartbeatInterval.Should().Be(TimeSpan.FromSeconds(5));
        o.PhiThreshold.Should().Be(8, "false-positive rate negligible");
        o.Arwl.Should().Be(3, "active random walk length");
        o.Prwl.Should().Be(6, "passive random walk length");
        o.Random.Should().BeNull("null = Random.Shared");
    }

    [Fact]
    public void WithChain_ReturnsNewInstance_OriginalUntouched()
    {
        var original = PeerOptions.Default;
        var modified = original
            .WithActiveViewSize(8)
            .WithPassiveViewSize(40)
            .WithJoinTtl(2)
            .WithJoinWaitTimeout(TimeSpan.FromMilliseconds(200))
            .WithShuffleInterval(TimeSpan.FromSeconds(1))
            .WithHeartbeatInterval(TimeSpan.FromMilliseconds(50))
            .WithPhiThreshold(16)
            .WithRandom(new Random(42));

        modified.ActiveViewSize.Should().Be(8);
        modified.PassiveViewSize.Should().Be(40);
        modified.JoinTtl.Should().Be(2);
        modified.JoinWaitTimeout.Should().Be(TimeSpan.FromMilliseconds(200));
        modified.ShuffleInterval.Should().Be(TimeSpan.FromSeconds(1));
        modified.HeartbeatInterval.Should().Be(TimeSpan.FromMilliseconds(50));
        modified.PhiThreshold.Should().Be(16);
        modified.Random.Should().NotBeNull();

        ReferenceEquals(original, modified).Should().BeFalse("records are immutable — With returns a new instance");
        original.ActiveViewSize.Should().Be(4, "original untouched");
    }
}
