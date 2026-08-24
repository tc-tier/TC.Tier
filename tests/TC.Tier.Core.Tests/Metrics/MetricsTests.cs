using TC.Tier.Core.Metrics;

namespace TC.Tier.Core.Tests.Metrics;

public sealed class MetricsConfigTests
{
    [Fact]
    public void Default_EnabledIsFalse()
    {
        new MetricsConfig().Enabled.Should().BeFalse();
    }

    [Fact]
    public void Default_SampleRateIs100()
    {
        new MetricsConfig().SampleRate.Should().Be(100);
    }

    [Fact]
    public void Default_EnableStorageMetricsIsTrue()
    {
        new MetricsConfig().EnableStorageMetrics.Should().BeTrue();
    }

    [Fact]
    public void Default_EnableLogMetricsIsTrue()
    {
        new MetricsConfig().EnableLogMetrics.Should().BeTrue();
    }

    [Fact]
    public void Default_EnableIndexMetricsIsTrue()
    {
        new MetricsConfig().EnableIndexMetrics.Should().BeTrue();
    }

    [Fact]
    public void Default_EnableSegmentAllocatorMetricsIsFalse()
    {
        new MetricsConfig().EnableSegmentAllocatorMetrics.Should().BeFalse();
    }

    [Fact]
    public void SetEnabled_ToTrue()
    {
        var config = new MetricsConfig { Enabled = true };
        config.Enabled.Should().BeTrue();
    }

    [Fact]
    public void SetSampleRate_To10()
    {
        var config = new MetricsConfig { SampleRate = 10 };
        config.SampleRate.Should().Be(10);
    }

    [Fact]
    public void SetSampleRate_To0()
    {
        var config = new MetricsConfig { SampleRate = 0 };
        config.SampleRate.Should().Be(0);
    }

    [Fact]
    public void DisableStorageMetrics()
    {
        var config = new MetricsConfig { EnableStorageMetrics = false };
        config.EnableStorageMetrics.Should().BeFalse();
    }

    [Fact]
    public void EnableSegmentAllocatorMetrics()
    {
        var config = new MetricsConfig { EnableSegmentAllocatorMetrics = true };
        config.EnableSegmentAllocatorMetrics.Should().BeTrue();
    }
}

public sealed class NullMetricsSinkTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        NullMetricsSink.Instance.Should().BeSameAs(NullMetricsSink.Instance);
    }

    [Fact]
    public void IsEnabled_ReturnsFalse()
    {
        NullMetricsSink.Instance.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Counter_DoesNotThrow()
    {
        Action act = () => NullMetricsSink.Instance.Counter("test", ReadOnlySpan<KeyValuePair<string, string>>.Empty);
        act.Should().NotThrow();
    }

    [Fact]
    public void Histogram_DoesNotThrow()
    {
        Action act = () => NullMetricsSink.Instance.Histogram("test", 1.5, ReadOnlySpan<KeyValuePair<string, string>>.Empty);
        act.Should().NotThrow();
    }

    [Fact]
    public void Gauge_DoesNotThrow()
    {
        Action act = () => NullMetricsSink.Instance.Gauge("test", 42.0, ReadOnlySpan<KeyValuePair<string, string>>.Empty);
        act.Should().NotThrow();
    }
}
