using TC.Tier.Core.Logging;

namespace TC.Tier.Core.Tests.Logging;

/// <summary>
/// LoggerExtensions 测试——验证各 LogLevel 短路、格式化、异常传递。
/// ★ 用 spy logger 捕获调用参数，验证 LoggerExtensions 正确传递 level/message/exception/args。
/// </summary>
public sealed class LoggerExtensionsTests
{
    private sealed class SpyLogger : ILogger
    {
        public LogLevel LastLevel { get; private set; }
        public string? LastMessage { get; private set; }
        public Exception? LastException { get; private set; }
        public bool IsEnabledResult { get; set; } = true;

        public bool IsEnabled(LogLevel logLevel) => IsEnabledResult;

        public void Log(LogLevel logLevel, string message, Exception? exception = null)
        {
            LastLevel = logLevel;
            LastMessage = message;
            LastException = exception;
        }
    }

    // === LogTrace ===

    [Fact]
    public void LogTrace_NoArgs_ForwardsLevelAndMessage()
    {
        var spy = new SpyLogger();
        spy.LogTrace("hello");
        spy.LastLevel.Should().Be(LogLevel.Trace);
        spy.LastMessage.Should().Be("hello");
        spy.LastException.Should().BeNull();
    }

    [Fact]
    public void LogTrace_OneArg_FormatsCorrectly()
    {
        var spy = new SpyLogger();
        spy.LogTrace("value={0}", 42);
        spy.LastLevel.Should().Be(LogLevel.Trace);
        spy.LastMessage.Should().Be("value=42");
    }

    [Fact]
    public void LogTrace_NamedPlaceholder_IsNormalized()
    {
        var spy = new SpyLogger();
        spy.LogTrace("{Name} = {Age}", "Alice", 30);
        spy.LastMessage.Should().Be("Alice = 30");
    }

    [Fact]
    public void LogTrace_Disabled_ShortCircuits()
    {
        var spy = new SpyLogger { IsEnabledResult = false };
        spy.LogTrace("should-not-log");
        spy.LastMessage.Should().BeNull("IsEnabled=false 时应短路不调 Log");
    }

    // === LogDebug ===

    [Fact]
    public void LogDebug_ForwardsCorrectly()
    {
        var spy = new SpyLogger();
        spy.LogDebug("debug={0}", "x");
        spy.LastLevel.Should().Be(LogLevel.Debug);
        spy.LastMessage.Should().Be("debug=x");
    }

    [Fact]
    public void LogDebug_Disabled_ShortCircuits()
    {
        var spy = new SpyLogger { IsEnabledResult = false };
        spy.LogDebug("test", 1, 2);
        spy.LastMessage.Should().BeNull();
    }

    // === LogInformation ===

    [Fact]
    public void LogInformation_NoArgs_ForwardsCorrectly()
    {
        var spy = new SpyLogger();
        spy.LogInformation("info");
        spy.LastLevel.Should().Be(LogLevel.Information);
        spy.LastMessage.Should().Be("info");
    }

    [Fact]
    public void LogInformation_ThreeArgs_FormatsAll()
    {
        var spy = new SpyLogger();
        spy.LogInformation("{0}-{1}-{2}", "a", "b", "c");
        spy.LastMessage.Should().Be("a-b-c");
    }

    [Fact]
    public void LogInformation_ParamsOverload_ForwardsAll()
    {
        var spy = new SpyLogger();
        spy.LogInformation("{0}{1}{2}{3}", "a", "b", "c", "d");
        spy.LastMessage.Should().Be("abcd");
    }

    [Fact]
    public void LogInformation_Disabled_ShortCircuits()
    {
        var spy = new SpyLogger { IsEnabledResult = false };
        spy.LogInformation("test");
        spy.LastMessage.Should().BeNull();
    }

    // === LogWarning ===

    [Fact]
    public void LogWarning_Simple_ForwardsCorrectly()
    {
        var spy = new SpyLogger();
        spy.LogWarning("warn");
        spy.LastLevel.Should().Be(LogLevel.Warning);
        spy.LastMessage.Should().Be("warn");
    }

    [Fact]
    public void LogWarning_WithException_ForwardsException()
    {
        var spy = new SpyLogger();
        var ex = new InvalidOperationException("ops");
        spy.LogWarning(ex, "warn {0}", "msg");
        spy.LastLevel.Should().Be(LogLevel.Warning);
        spy.LastMessage.Should().Be("warn msg");
        spy.LastException.Should().BeSameAs(ex);
    }

    [Fact]
    public void LogWarning_WithException_NullLogger_DoesNotThrow()
    {
        ILogger? nullLogger = null;
        Action act = () => nullLogger.LogWarning(new Exception("e"), "test");
        act.Should().NotThrow("null logger 应安全忽略");
    }

    [Fact]
    public void LogWarning_WithException_Disabled_ShortCircuits()
    {
        var spy = new SpyLogger { IsEnabledResult = false };
        spy.LogWarning(new Exception("e"), "test");
        spy.LastMessage.Should().BeNull();
    }

    [Fact]
    public void LogWarning_ParamsOverload_ForwardsAll()
    {
        var spy = new SpyLogger();
        spy.LogWarning("{0}-{1}", "x", "y");
        spy.LastMessage.Should().Be("x-y");
    }

    // === LogError ===

    [Fact]
    public void LogError_Simple_ForwardsCorrectly()
    {
        var spy = new SpyLogger();
        spy.LogError("error");
        spy.LastLevel.Should().Be(LogLevel.Error);
        spy.LastMessage.Should().Be("error");
    }

    [Fact]
    public void LogError_WithException_NullLogger_Safe()
    {
        ILogger? nullLogger = null;
        Action act = () => nullLogger.LogError(new Exception("e"), "msg");
        act.Should().NotThrow();
    }

    [Fact]
    public void LogError_WithException_TwoArgs_FormatsCorrectly()
    {
        var spy = new SpyLogger();
        var ex = new InvalidOperationException("fail");
        spy.LogError(ex, "code={0}", 500);
        spy.LastLevel.Should().Be(LogLevel.Error);
        spy.LastMessage.Should().Be("code=500");
        spy.LastException.Should().BeSameAs(ex);
    }

    // === LogCritical ===

    [Fact]
    public void LogCritical_ForwardsCorrectly()
    {
        var spy = new SpyLogger();
        spy.LogCritical("fatal");
        spy.LastLevel.Should().Be(LogLevel.Critical);
        spy.LastMessage.Should().Be("fatal");
    }

    [Fact]
    public void LogCritical_TwoArgs_FormatsCorrectly()
    {
        var spy = new SpyLogger();
        spy.LogCritical("a={0} b={1}", "x", "y");
        spy.LastMessage.Should().Be("a=x b=y");
    }

    // === Named placeholder edge cases ===

    [Fact]
    public void NamedPlaceholder_MultipleOccurrences_EachGetsSeparateIndex()
    {
        var spy = new SpyLogger();
        spy.LogInformation("{First} then {Second}", "hello", "world");
        spy.LastMessage.Should().Be("hello then world");
    }

    [Fact]
    public void NamedPlaceholder_NoNamedPlaceholder_UnchangedFormat()
    {
        var spy = new SpyLogger();
        spy.LogInformation("plain {0} message", "test");
        spy.LastMessage.Should().Be("plain test message");
    }

    [Fact]
    public void AllLogLevels_Disabled_AllShortCircuit()
    {
        var spy = new SpyLogger { IsEnabledResult = false };
        spy.LogTrace("t");
        spy.LogDebug("d");
        spy.LogInformation("i");
        spy.LogWarning("w");
        spy.LogError("e");
        spy.LogCritical("c");
        spy.LastMessage.Should().BeNull("所有级别都应短路");
    }
}
