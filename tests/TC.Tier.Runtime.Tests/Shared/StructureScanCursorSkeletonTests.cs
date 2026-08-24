namespace TC.Tier.Runtime.Tests.Shared;

/// <summary>
/// Shared IStructureScanCursor / StructureScanCursorBase 骨架验证——
/// Direction 存储 + MoveNextAsync 默认委托同步 MoveNext。
/// </summary>
public sealed class StructureScanCursorSkeletonTests
{
    private sealed class FakeCursor(ReadDirection dir) : StructureScanCursorBase(dir)
    {
        public int MoveNextCalls;

        public override bool MoveNext()
        {
            MoveNextCalls++;
            return MoveNextCalls < 3;
        }

        public override void Dispose() { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAsyncCursor : StructureScanCursorBase
    {
        public int AsyncCalls;
        public FakeAsyncCursor(ReadDirection dir) : base(dir) { }

        public override bool MoveNext() => false;

        public override async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
        {
            AsyncCalls++;
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            return AsyncCalls < 2;
        }

        public override void Dispose() { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task MoveNextAsync_DelegatesToMoveNext_ByDefault()
    {
        var c = new FakeCursor(ReadDirection.Forward);
        (await c.MoveNextAsync()).Should().BeTrue();
        c.MoveNextCalls.Should().Be(1);
        (await c.MoveNextAsync()).Should().BeTrue();
        (await c.MoveNextAsync()).Should().BeFalse();
    }

    [Fact]
    public void Direction_StoredFromCtor()
    {
        new FakeCursor(ReadDirection.Backward).Direction.Should().Be(ReadDirection.Backward);
        new FakeCursor(ReadDirection.Forward).Direction.Should().Be(ReadDirection.Forward);
    }

    [Fact]
    public async Task MoveNextAsync_CanBeOverridden()
    {
        // 子类可 override MoveNextAsync 用真异步 I/O
        var c = new FakeAsyncCursor(ReadDirection.Forward);
        (await c.MoveNextAsync()).Should().BeTrue();
        (await c.MoveNextAsync()).Should().BeFalse();
        c.AsyncCalls.Should().Be(2);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var c = new FakeCursor(ReadDirection.Forward);
        c.Dispose();
        c.Dispose();   // 不抛
    }
}
