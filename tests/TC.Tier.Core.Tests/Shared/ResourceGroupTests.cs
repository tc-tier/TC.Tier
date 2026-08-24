using TC.Tier.Core.Shared;

namespace TC.Tier.Core.Tests.Shared;

public sealed class ResourceGroupTests
{
    private sealed class TrackedDisposable : IDisposable
    { public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }

    private sealed class TrackedAsyncDisposable : IDisposable, IAsyncDisposable
    { public bool Disposed { get; private set; } public bool AsyncDisposed { get; private set; }
      public void Dispose() => Disposed = true;
      public ValueTask DisposeAsync() { AsyncDisposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void Constructor_Null_Throws()
    {
        // ReSharper disable once AssignNullToNotNullAttribute
        Action act = () => _ = new ResourceGroup(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Empty_DoesNotThrow()
    {
        Action act = () => { using var owner = new ResourceGroup(); };
        act.Should().NotThrow();
    }

    [Fact]
    public void Resources_ReturnsItems()
    {
        var a = new TrackedDisposable();
        var b = new TrackedDisposable();
        var owner = new ResourceGroup(a, b);

        // GetResources 返回诊断快照（ResourceInfo：Name/TypeName/Ownership/AddedTimestampMs），
        // 有意不暴露资源引用——避免外部拿到引用绕过组的一致性管理。
        // 这里验证数量 + 插入序 + 命名（同类型第二个追加 -2）+ 所有权。
        var resources = owner.GetResources();
        resources.Should().HaveCount(2);
        resources[0].Name.Should().Be("TrackedDisposable");
        resources[0].TypeName.Should().Be("TrackedDisposable");
        resources[0].Ownership.Should().Be(ResourceOwnership.Owned);
        resources[1].Name.Should().Be("TrackedDisposable-2");
        resources[1].TypeName.Should().Be("TrackedDisposable");
        resources[1].Ownership.Should().Be(ResourceOwnership.Owned);
    }



    [Fact]
    public void Dispose_DisposesAllResources()
    {
        var a = new TrackedDisposable();
        var b = new TrackedDisposable();
        var owner = new ResourceGroup(a, b);
        owner.Dispose();
        a.Disposed.Should().BeTrue();
        b.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var a = new TrackedDisposable();
        var owner = new ResourceGroup(a);
        owner.Dispose();
        owner.Dispose(); // 不应抛异常
    }

    [Fact]
    public void DisposeAsync_DisposesAllSyncResources()
    {
        var a = new TrackedDisposable();
        var owner = new ResourceGroup(a);
        owner.DisposeAsync().AsTask().Wait(1000);
        a.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_DisposesAsyncResources()
    {
        var a = new TrackedAsyncDisposable();
        var owner = new ResourceGroup(a);
        await owner.DisposeAsync();
        a.AsyncDisposed.Should().BeTrue();
    }

    [Fact]
    public void ThrowIfDisposed_BeforeDispose_DoesNotThrow()
    {
        var owner = new ResourceGroup(new TrackedDisposable());
        Action act = owner.ThrowIfDisposed;
        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfDisposed_AfterDispose_Throws()
    {
        var owner = new ResourceGroup(new TrackedDisposable());
        owner.Dispose();
        Action act = owner.ThrowIfDisposed;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void DisposeAsync_CalledTwice_IsIdempotent()
    {
        var a = new TrackedDisposable();
        var owner = new ResourceGroup(a);
        owner.DisposeAsync().AsTask().Wait(1000);
        Action act = () => owner.DisposeAsync().AsTask().Wait(1000);
        act.Should().NotThrow();
    }
}
