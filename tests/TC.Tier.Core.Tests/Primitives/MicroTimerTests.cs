
namespace TC.Tier.Core.Tests.Primitives;

public sealed class MicroTimerTests
{
    [Fact]
    public void Start_Active_IsActiveTrue()
    {
        var timer = MicroTimer.Start(true);
        timer.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Start_Inactive_IsActiveFalse()
    {
        var timer = MicroTimer.Start(false);
        timer.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Start_Default_IsActiveTrue()
    {
        var timer = MicroTimer.Start(); // 默认 active=true
        timer.IsActive.Should().BeTrue();
    }

    [Fact]
    public void ElapsedMicros_Active_ReturnsPositive()
    {
        var timer = MicroTimer.Start(true);
        Thread.Sleep(1); // 至少 1ms = 1000μs
        timer.ElapsedMicros().Should().BePositive();
    }

    [Fact]
    public void ElapsedMicros_Inactive_ReturnsZero()
    {
        var timer = MicroTimer.Start(false);
        Thread.Sleep(1);
        timer.ElapsedMicros().Should().Be(0);
    }

    [Fact]
    public void ElapsedMillis_Active_ReturnsNonNegative()
    {
        var timer = MicroTimer.Start(true);
        timer.ElapsedMillis().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ElapsedMillis_Inactive_ReturnsZero()
    {
        var timer = MicroTimer.Start(false);
        timer.ElapsedMillis().Should().Be(0);
    }

    [Fact]
    public void ElapsedTicks_Active_ReturnsPositive()
    {
        var timer = MicroTimer.Start(true);
        Thread.Sleep(1);
        timer.ElapsedTicks().Should().BePositive();
    }

    [Fact]
    public void ElapsedTicks_Inactive_ReturnsZero()
    {
        var timer = MicroTimer.Start(false);
        timer.ElapsedTicks().Should().Be(0);
    }

    [Fact]
    public void ElapsedReadable_Active_ReturnsNonEmptyString()
    {
        var timer = MicroTimer.Start(true);
        var s = timer.ElapsedReadable();
        s.Should().NotBeNullOrEmpty();
        s.Should().NotBe("0");
    }

    [Fact]
    public void ElapsedReadable_Inactive_ReturnsZero()
    {
        var timer = MicroTimer.Start(false);
        timer.ElapsedReadable().Should().Be("0");
    }

    [Fact]
    public void TryFormat_Active_WritesToSpan()
    {
        var timer = MicroTimer.Start(true);
        Span<char> dest = stackalloc char[32];
        timer.TryFormat(dest, out int written).Should().BeTrue();
        written.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TryFormat_Inactive_ReturnsFalse()
    {
        var timer = MicroTimer.Start(false);
        Span<char> dest = stackalloc char[32];
        timer.TryFormat(dest, out int written).Should().BeFalse();
        written.Should().Be(0);
    }

    [Fact]
    public void Default_IsInactive()
    {
        MicroTimer @default = default;
        @default.IsActive.Should().BeFalse();
        @default.ElapsedMicros().Should().Be(0);
    }
}
