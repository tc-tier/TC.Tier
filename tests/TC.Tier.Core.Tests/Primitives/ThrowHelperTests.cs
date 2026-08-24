namespace TC.Tier.Core.Tests.Primitives;

public sealed class ThrowHelperTests
{
    [Fact]
    public void ThrowArgumentOutOfRange_Throws()
    {
        Action act = () => ThrowHelper.ThrowArgumentOutOfRange("test");
        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("test");
    }

    [Fact]
    public void ThrowArgumentOutOfRange_NullParamName_ThrowsWithEmpty()
    {
        Action act = () => ThrowHelper.ThrowArgumentOutOfRange(null);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be(string.Empty);
    }

    [Fact]
    public void ThrowArgumentOutOfRange_WithMessage_Throws()
    {
        Action act = () => ThrowHelper.ThrowArgumentOutOfRange("x", "bad value");
        var ex = act.Should().Throw<ArgumentOutOfRangeException>().And;
        ex.ParamName.Should().Be("x");
        ex.Message.Should().Contain("bad value");
    }

    [Fact]
    public void ThrowObjectDisposed_Throws()
    {
        Action act = () => ThrowHelper.ThrowObjectDisposed("MyEngine");
        act.Should().Throw<ObjectDisposedException>()
            .WithMessage("*MyEngine*");
    }

    [Fact]
    public void ThrowInvalidOperationException_Throws()
    {
        Action act = () => ThrowHelper.ThrowInvalidOperationException("not allowed");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("not allowed");
    }
}
