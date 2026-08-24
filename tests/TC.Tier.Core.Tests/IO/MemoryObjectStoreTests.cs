using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Testing;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// MemoryObjectStore 契约平权套（B3.1）——全量契约断言 + 替身特有仪器断言（计数器/活跃会话数/能力位）。
/// </summary>
public class MemoryObjectStoreContractTests : ObjectStoreContractTests
{
    protected override IObjectStore CreateStore()
        => new MemoryObjectStore();
}

/// <summary>替身特有仪器与并发行为（不属平权契约面——Memory 实现专属）。</summary>
public class MemoryObjectStoreInstrumentationTests : IDisposable
{
    private readonly MemoryObjectStore _store = new();

    [Fact]
    public void Capabilities_AllBitsSet()
    {
        _store.Capabilities.Should().Be(
            ObjectStoreCapabilities.ConditionalPut
            | ObjectStoreCapabilities.ConditionalDelete
            | ObjectStoreCapabilities.ServerSideCopy
            | ObjectStoreCapabilities.StrongList
            | ObjectStoreCapabilities.Multipart
            | ObjectStoreCapabilities.RangeGet);
    }

    [Fact]
    public async Task Counters_TrackOperations()
    {
        await _store.PutAsync("k", new byte[] { 1, 2, 3 });
        await _store.HeadAsync("k");
        await _store.GetAsync("k", 0, new byte[2]);
        await _store.ListAsync();
        await _store.DeleteAsync("k");

        Volatile.Read(ref _store.Counters.Puts).Should().Be(1);
        Volatile.Read(ref _store.Counters.Heads).Should().Be(1);
        Volatile.Read(ref _store.Counters.Gets).Should().Be(1);
        Volatile.Read(ref _store.Counters.Lists).Should().Be(1);
        Volatile.Read(ref _store.Counters.Deletes).Should().Be(1);
        Volatile.Read(ref _store.Counters.PutBytes).Should().Be(3);
        Volatile.Read(ref _store.Counters.GetBytes).Should().Be(2);
    }

    [Fact]
    public async Task ActiveUploadSessions_TrackLifecycle()
    {
        _store.ActiveUploadSessions.Should().Be(0);
        var s1 = _store.CreateMultipartUpload("a");
        var s2 = _store.CreateMultipartUpload("b");
        _store.ActiveUploadSessions.Should().Be(2);

        await s1.AbortAsync();
        _store.ActiveUploadSessions.Should().Be(1);

        var p = await s2.UploadPartAsync(1, new byte[] { 1 });
        await s2.CompleteAsync([p]);
        _store.ActiveUploadSessions.Should().Be(0);   // 碎片回收断言的依据
    }

    [Fact]
    public async Task MultipartSession_DisposeAsync_AbortsAsFallback()
    {
        var session = _store.CreateMultipartUpload("x");
        await using (session)
        {
            await session.UploadPartAsync(1, new byte[] { 1 });
        }   // DisposeAsync ≡ Abort——异常安全兜底
        _store.ActiveUploadSessions.Should().Be(0);
        (await _store.HeadAsync("x")).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentConditionalPut_ExactlyOneWinner()
    {
        const int contenders = 16;
        var results = new bool[contenders];
        await Parallel.ForAsync(0, contenders, async (i, ct) =>
        {
            try
            {
                await _store.PutAsync($"lock-{i & 1}", new byte[] { (byte)i },
                    condition: new PutCondition(null, "*"), ct: ct);
                results[i] = true;
            }
            catch (FileIOException ex) when (ex.Error == IOError.PreconditionFailed)
            {
                results[i] = false;
            }
        });

        // 两个键各恰好一个抢建成功
        results.Count(r => r).Should().Be(2);
        (await _store.ListAsync("lock-")).Count.Should().Be(2);
    }

    [Fact]
    public void Disposed_Throws()
    {
        _store.Dispose();
        var act = () => _store.PutAsync("k", new byte[] { 1 });
        act.Should().Throw<ObjectDisposedException>();
    }

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }
}
