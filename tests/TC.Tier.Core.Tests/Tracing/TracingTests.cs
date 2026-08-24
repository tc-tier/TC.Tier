using TC.Tier.Core.Tracing;

namespace TC.Tier.Core.Tests.Tracing;

public sealed class NullTracerTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        NullTracer.Instance.Should().BeSameAs(NullTracer.Instance);
    }

    [Fact]
    public void IsEnabled_ReturnsFalse()
    {
        NullTracer.Instance.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void BeginSpan_ReturnsNullSpan()
    {
        var span = NullTracer.Instance.BeginSpan("test");
        span.Should().BeSameAs(NullSpan.Instance);
    }

    [Fact]
    public void BeginSpan_WithKind_ReturnsNullSpan()
    {
        var span = NullTracer.Instance.BeginSpan("test", SpanKind.Client);
        span.Should().BeSameAs(NullSpan.Instance);
    }

    [Fact]
    public void Current_ReturnsNull()
    {
        NullTracer.Instance.Current.Should().BeNull();
    }
}

public sealed class NullSpanTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        NullSpan.Instance.Should().BeSameAs(NullSpan.Instance);
    }

    [Fact]
    public void SetTag_String_DoesNotThrow()
    {
        Action act = () => NullSpan.Instance.SetTag("key", "value");
        act.Should().NotThrow();
    }

    [Fact]
    public void SetTag_Long_DoesNotThrow()
    {
        Action act = () => NullSpan.Instance.SetTag("size", 1024L);
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordException_DoesNotThrow()
    {
        Action act = () => NullSpan.Instance.RecordException(new Exception("test"));
        act.Should().NotThrow();
    }

    [Fact]
    public void AddEvent_DoesNotThrow()
    {
        Action act = () => NullSpan.Instance.AddEvent("event");
        act.Should().NotThrow();
    }

    [Fact]
    public void SetStatus_DoesNotThrow()
    {
        Action act = () => NullSpan.Instance.SetStatus(SpanStatus.Error);
        act.Should().NotThrow();
    }

    [Fact]
    public void SetStatus_WithDescription_DoesNotThrow()
    {
        Action act = () => NullSpan.Instance.SetStatus(SpanStatus.Ok, "done");
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // 多次调用也不抛异常
        NullSpan.Instance.Dispose();
        NullSpan.Instance.Dispose();
    }
}

public sealed class SpanKindTests
{
    [Fact]
    public void Enum_HasExpectedValues()
    {
        ((int)SpanKind.Internal).Should().Be(0);
        ((int)SpanKind.Server).Should().Be(1);
        ((int)SpanKind.Client).Should().Be(2);
        ((int)SpanKind.Producer).Should().Be(3);
        ((int)SpanKind.Consumer).Should().Be(4);
    }
}

public sealed class SpanStatusTests
{
    [Fact]
    public void Enum_HasExpectedValues()
    {
        ((int)SpanStatus.Ok).Should().Be(0);
        ((int)SpanStatus.Error).Should().Be(1);
    }
}

public sealed class TracingConfigTests
{
    [Fact]
    public void Default_EnabledIsFalse()
    {
        new TracingConfig().Enabled.Should().BeFalse();
    }

    [Fact]
    public void Default_SampleRateIs100()
    {
        new TracingConfig().SampleRate.Should().Be(100);
    }

    [Fact]
    public void SetEnabled_ToTrue()
    {
        var config = new TracingConfig { Enabled = true };
        config.Enabled.Should().BeTrue();
    }

    [Fact]
    public void SetSampleRate_To50()
    {
        var config = new TracingConfig { SampleRate = 50 };
        config.SampleRate.Should().Be(50);
    }

    [Fact]
    public void SetSampleRate_To0()
    {
        var config = new TracingConfig { SampleRate = 0 };
        config.SampleRate.Should().Be(0);
    }
}
