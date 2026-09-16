using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.P2P;

namespace TC.Tier.Core.Net.Tests.P2P;

/// <summary>
/// PhiAccrual detector contract tests (spec-06 §5 — normal-distribution fit over heartbeat
/// intervals; anchor-first semantics; min-samples gate; variance floor prevents jitter
/// false positives while real silence is detected within bounded time).
/// </summary>
public class PhiAccrualDetectorTests
{
    /// <summary>可推进的注入时钟（RecordHeartbeat 与 Phi 同源——测试确定性）。</summary>
    private sealed class Clock
    {
        public long Now;
        public long Read() => Now;
    }

    private static PhiAccrualDetector WithClock(Clock clock, double threshold = 8, int windowSamples = 12, int minSamples = 3)
        => new(threshold, windowSamples, minSamples, clock.Read);

    [Fact]
    public void SteadyHeartbeats_NoFalsePositive_SilenceDetectedWithinBound()
    {
        var clock = new Clock { Now = 0 };
        var phi = WithClock(clock);

        for (var i = 0; i < 20; i++)
        {
            phi.RecordHeartbeat();
            clock.Now += 100;
            phi.Phi(clock.Now).Should().BeLessThan(8, "steady 100ms heartbeats must not trip the threshold");
            phi.IsFailed(clock.Now).Should().BeFalse();
        }
        phi.Phi(clock.Now + 100).Should().BeGreaterThan(0, "samples accumulated — φ computation is non-trivial");

        long failedAt = long.MaxValue;
        for (var t = clock.Now; t < clock.Now + 100_000; t += 50)
        {
            if (phi.IsFailed(t)) { failedAt = t; break; }
        }
        failedAt.Should().BeGreaterThan(clock.Now, "silence is actually detected (non-trivial failure)");
        failedAt.Should().BeLessThan(clock.Now + 5_000, "silence detected within bounded time (≪ 100s)");
    }

    [Fact]
    public void FirstHeartbeat_OnlyAnchors_BelowMinSamples_PhiZero()
    {
        var clock = new Clock { Now = 1_000 };
        var phi = WithClock(clock, minSamples: 3);

        phi.RecordHeartbeat();
        clock.Now += 10_000;
        phi.Phi(clock.Now).Should().Be(0, "first heartbeat anchors only — no interval samples yet");
        phi.IsFailed(clock.Now).Should().BeFalse("cold start grace");

        phi.RecordHeartbeat();
        clock.Now += 10_000;
        phi.Phi(clock.Now).Should().Be(0, "one interval sample — still below minSamples gate");
    }

    [Fact]
    public void Reset_ClearsSamplesAndAnchor_DetectionRestarts()
    {
        var clock = new Clock { Now = 0 };
        var phi = WithClock(clock);

        for (var i = 0; i < 10; i++)
        {
            phi.RecordHeartbeat();
            clock.Now += 100;
        }
        phi.Phi(clock.Now).Should().BeGreaterThan(0);

        phi.Reset();
        clock.Now += 100_000;
        phi.Phi(clock.Now).Should().Be(0, "reset clears samples and anchor — no stale detection");

        phi.RecordHeartbeat();
        phi.Phi(clock.Now).Should().Be(0, "re-anchored — cold start grace again");
    }

    [Fact]
    public void WindowBounded_AncientOutlierAgesOut()
    {
        var clock = new Clock { Now = 0 };
        var phi = WithClock(clock, windowSamples: 4, minSamples: 2);

        phi.RecordHeartbeat();
        clock.Now += 60_000;   // 远古离群间隔——进窗口
        phi.RecordHeartbeat();
        // 用稳定间隔把离群样本挤出有界窗口
        for (var i = 0; i < 6; i++)
        {
            clock.Now += 100;
            phi.RecordHeartbeat();
        }

        phi.Phi(clock.Now).Should().BeLessThan(8,
            "the ancient outlier interval has aged out of the bounded window — steady rhythm dominates");
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        var act1 = () => new PhiAccrualDetector(windowSamples: 0);
        var act2 = () => new PhiAccrualDetector(minSamples: 0);
        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }
}
