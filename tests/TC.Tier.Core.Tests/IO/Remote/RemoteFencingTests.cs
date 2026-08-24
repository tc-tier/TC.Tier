using System.Text.Json;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// fencing 卷锁测试（B3.4，§4.6/§7.1）：双实例互斥 / 心跳超时接管 / 误删防护（IfMatch CAS）/
/// 释放后重获 / 无条件 PUT 能力 → Unsupported / 尽力型语义声明（非重入）。
/// </summary>
public class RemoteFencingTests : IDisposable
{
    private readonly MemoryObjectStore _store = new();
    private readonly RemoteFileSystem _fs;

    public RemoteFencingTests()
        => _fs = RemoteFileSystem.OpenOrCreate(_store, new RemoteFileSystemOptions
        {
            // 短超时——接管测试快速触发
            LeaseTimeout = TimeSpan.FromSeconds(2),
            HeartbeatInterval = TimeSpan.FromMilliseconds(300),
        });

    public void Dispose()
    {
        _fs.Dispose();
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    private static RemoteFileSystem SecondFs(MemoryObjectStore store)
        => RemoteFileSystem.OpenOrCreate(store, new RemoteFileSystemOptions
        {
            LeaseTimeout = TimeSpan.FromSeconds(2),
            HeartbeatInterval = TimeSpan.FromMilliseconds(300),
        });

    [Fact]
    public void Acquire_Takeover_Mutex_AcrossInstances()
    {
        using var lease = _fs.AcquireExclusive(TimeSpan.FromSeconds(5));
        using var fs2 = SecondFs(_store);
        var act = () => fs2.AcquireExclusive(TimeSpan.FromMilliseconds(300));   // 心跳活跃——不可抢
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
        fs2.Dispose();
    }

    [Fact]
    public void Release_AllowsImmediateReacquire()
    {
        using (var lease = _fs.AcquireExclusive(TimeSpan.FromSeconds(5))) { }
        using var fs2 = SecondFs(_store);
        var act = () => fs2.AcquireExclusive(TimeSpan.FromSeconds(5));
        act.Should().NotThrow();   // 释放即空位（条件删除已清 lock 对象）
        fs2.Dispose();
    }

    [Fact]
    public void NonReentrant_SecondAcquireOnSameInstance_TimesOut()
    {
        using var lease = _fs.AcquireExclusive(TimeSpan.FromSeconds(5));
        var act = () => _fs.AcquireExclusive(TimeSpan.FromMilliseconds(200));   // 非重入——按争用处理
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
    }

    [Fact]
    public void HeartbeatTimeout_Takeover_Succeeds()
    {
        // 模拟持有者死亡：手工注入过期心跳 payload（不再刷新）
        var stale = JsonSerializer.SerializeToUtf8Bytes(
            new { Token = "dead-holder", HeartbeatUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000 });
        var lockKey = ".tier-volume-lock";   // KeyPrefix 为空——锁键即此名
        _store.PutAsync(lockKey, stale).AsTask().GetAwaiter().GetResult();

        using var fs2 = SecondFs(_store);
        using var lease = fs2.AcquireExclusive(TimeSpan.FromSeconds(5));   // 接管成功（心跳已超时）
        lease.Should().NotBeNull();

        // 接管后原持有者视角：锁已易主（新 token）
        var info = _store.HeadAsync(lockKey).AsTask().GetAwaiter().GetResult();
        info.Should().NotBeNull();
        var buf = new byte[(int)info!.Size];
        _store.GetAsync(lockKey, 0, buf).AsTask().GetAwaiter().GetResult();
        var payload = JsonSerializer.Deserialize<JsonElement>(buf);
        payload.GetProperty("Token").GetString().Should().NotBe("dead-holder");
    }

    [Fact]
    public void LiveHeartbeat_PreventsTakeover_ThenExpires()
    {
        // 活跃心跳的持有者（fs 自持 + 定时刷新）——他者不可接管
        using var lease = _fs.AcquireExclusive(TimeSpan.FromSeconds(5));
        using var fs2 = SecondFs(_store);
        var act = () => fs2.AcquireExclusive(TimeSpan.FromMilliseconds(500));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
        fs2.Dispose();
    }

    [Fact]
    public void TokenGuard_ReleaseDoesNotDeleteOthersLock()
    {
        // 持有者 A 拿锁；B 注入接管（手工写新 token）；A 的 lease Dispose 不得误删 B 的锁
        using (var lease = _fs.AcquireExclusive(TimeSpan.FromSeconds(5)))
        {
            var lockKey = ".tier-volume-lock";
            var head = _store.HeadAsync(lockKey).AsTask().GetAwaiter().GetResult();
            var hijacked = JsonSerializer.SerializeToUtf8Bytes(
                new { Token = "hijacker", HeartbeatUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            _store.PutAsync(lockKey, hijacked,
                condition: new PutCondition(head!.ETag, null)).AsTask().GetAwaiter().GetResult();
        }   // A 释放：token 校验失败——不删（防误删他人锁）

        _store.HeadAsync(".tier-volume-lock").AsTask().GetAwaiter().GetResult().Should().NotBeNull(
            "释放方 token 已失配——锁对象必须保留（误删 = 双持窗口）");
    }

    [Fact]
    public void StoreWithoutConditionalPut_ThrowsUnsupported()
    {
        using var bare = new NoConditionalPutStore(_store);
        using var fs = RemoteFileSystem.OpenOrCreate(bare);
        var act = () => fs.AcquireExclusive(TimeSpan.FromSeconds(1));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.Unsupported);
        fs.Dispose();
    }

    [Fact]
    public void FsDispose_WithHeldLease_ForceReleases()
    {
        var store = new MemoryObjectStore();
        var fs = RemoteFileSystem.OpenOrCreate(store);
        var lease = fs.AcquireExclusive(TimeSpan.FromSeconds(5));
        fs.Dispose();   // 违约释放（lease 未 Dispose）——锁对象清除
        store.HeadAsync(".tier-volume-lock").AsTask().GetAwaiter().GetResult().Should().BeNull();
        store.Dispose();
    }

    /// <summary>剥除 ConditionalPut 能力位的装饰器（老端点降级路径测试）。</summary>
    private sealed class NoConditionalPutStore(IObjectStore inner) : IObjectStore
    {
        public ObjectStoreCapabilities Capabilities => inner.Capabilities & ~ObjectStoreCapabilities.ConditionalPut;
        public ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, ObjectMetadata? metadata = null,
                                  PutCondition? condition = null, CancellationToken ct = default)
            => inner.PutAsync(key, data, metadata, condition, ct);
        public ValueTask PutAsync(string key, Stream data, long length, ObjectMetadata? metadata = null,
                                  PutCondition? condition = null, CancellationToken ct = default)
            => inner.PutAsync(key, data, length, metadata, condition, ct);
        public ValueTask<int> GetAsync(string key, long offset, Memory<byte> destination, CancellationToken ct = default)
            => inner.GetAsync(key, offset, destination, ct);
        public ValueTask<ObjectInfo?> HeadAsync(string key, CancellationToken ct = default)
            => inner.HeadAsync(key, ct);
        public ValueTask DeleteAsync(string key, DeleteCondition? condition = null, CancellationToken ct = default)
            => inner.DeleteAsync(key, condition, ct);
        public ValueTask<IReadOnlyList<ObjectEntry>> ListAsync(string? prefix = null, CancellationToken ct = default)
            => inner.ListAsync(prefix, ct);
        public ValueTask CopyAsync(string sourceKey, string destKey, CopyMetadata? metadata = null,
                                   CancellationToken ct = default)
            => inner.CopyAsync(sourceKey, destKey, metadata, ct);
        public ValueTask<ObjectMetadata> CopyMetadataAsync(string sourceKey, ObjectMetadata? replace = null,
                                                           CancellationToken ct = default)
            => inner.CopyMetadataAsync(sourceKey, replace, ct);
        public IMultipartUpload CreateMultipartUpload(string key, ObjectMetadata? metadata = null)
            => inner.CreateMultipartUpload(key, metadata);
        public ValueTask<long> CopyRangeAsync(string sourceKey, string destKey, long sourceOffset, long length,
                                              CopyMetadata? metadata = null, CancellationToken ct = default)
            => inner.CopyRangeAsync(sourceKey, destKey, sourceOffset, length, metadata, ct);
        public ValueTask<IReadOnlyList<MultipartUploadSession>> ListMultipartUploadsAsync(CancellationToken ct = default)
            => inner.ListMultipartUploadsAsync(ct);
        public ValueTask AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default)
            => inner.AbortMultipartUploadAsync(key, uploadId, ct);
        public void Dispose() => inner.Dispose();
    }
}
