using TC.Tier.Core.Logging;

namespace TC.Tier.Core.Tests.Logging;

public sealed class NullLoggerTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        NullLogger.Instance.Should().BeSameAs(NullLogger.Instance);
    }

    [Fact]
    public void IsEnabled_AlwaysReturnsFalse()
    {
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
            NullLogger.Instance.IsEnabled(level).Should().BeFalse($"NullLogger.IsEnabled({level}) should be false");
    }

    [Fact]
    public void Log_DoesNotThrow_ForAnyLevel()
    {
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
        {
            Action act = () => NullLogger.Instance.Log(level, "test message");
            act.Should().NotThrow($"Log({level}) should not throw");
        }
    }

    [Fact]
    public void Log_WithException_DoesNotThrow()
    {
        var ex = new InvalidOperationException("test");
        Action act = () => NullLogger.Instance.Log(LogLevel.Error, "test", ex);
        act.Should().NotThrow();
    }

    [Fact]
    public void NullLoggerFactory_CreateLogger_ReturnsNullLoggerInstance()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger("test");
        logger.Should().BeSameAs(NullLogger.Instance);
    }

    [Fact]
    public void NullLoggerFactory_Instance_IsSingleton()
    {
        NullLoggerFactory.Instance.Should().BeSameAs(NullLoggerFactory.Instance);
    }

    [Fact]
    public void CreateLogger_DifferentCategories_ReturnsSameInstance()
    {
        var logger1 = NullLoggerFactory.Instance.CreateLogger("category1");
        var logger2 = NullLoggerFactory.Instance.CreateLogger("category2");
        logger1.Should().BeSameAs(logger2);
    }
}
