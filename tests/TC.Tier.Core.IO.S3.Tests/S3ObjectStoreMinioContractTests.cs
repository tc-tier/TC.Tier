using TC.Tier.Core.IO;
using TC.Tier.Core.IO.S3;
using TC.Tier.Core.Tests.IO;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.IO.S3.Tests;

/// <summary>
/// S3ObjectStore 契约平权套——真实 S3 协议端点（MinIO 容器 / 真 S3）。
/// <para>★ 门禁纪律（§7.4）：环境变量 <c>TIER_S3_TEST_ENDPOINT</c> 未设置时整套跳过（显式 Skip——
///   免费层假服务器 + 黄金向量全绿是接入真端点的前置门禁；跳过项在报告中可见，非静默隐藏）。</para>
/// <para>★ dev_su 运行：<c>scripts/run-minio-tests.sh</c>（起 MinIO 容器 → 导出环境变量 → dotnet test）。</para>
/// <para>★ 独立司法鉴定意义：MinIO（Go 实现）接受我们的签名 = SigV4 互操作性的真实验证。</para>
/// </summary>
public sealed class S3ObjectStoreMinioContractTests : ObjectStoreContractTests
{
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("TIER_S3_TEST_ENDPOINT");
    private static readonly string AccessKey = Environment.GetEnvironmentVariable("TIER_S3_TEST_ACCESS_KEY") ?? "minioadmin";
    private static readonly string SecretKey = Environment.GetEnvironmentVariable("TIER_S3_TEST_SECRET_KEY") ?? "minioadmin";
    private static readonly string Bucket = Environment.GetEnvironmentVariable("TIER_S3_TEST_BUCKET") ?? "tier-test";

    protected override IObjectStore CreateStore()
    {
        if (Endpoint is null)
            throw new Xunit.SkipException(
                "TIER_S3_TEST_ENDPOINT 未设置——真端点契约套按 §7.4 门禁纪律跳过（免费层假服务器+黄金向量先行全绿）。");
        var store = S3ObjectStore.Create(new S3ClientOptions
        {
            Endpoint = Endpoint!,
            Bucket = Bucket,
            Region = "us-east-1",
            Credentials = new StaticCredentials(AccessKey, SecretKey),
            Timeout = TimeSpan.FromSeconds(60),
            SigningHost = Environment.GetEnvironmentVariable("TIER_S3_TEST_SIGNING_HOST"),   // 反代解耦（cos.mytzz.top → COS 原生域名）
            UseVirtualHostAddressing = Environment.GetEnvironmentVariable("TIER_S3_TEST_VHOST") == "1",   // COS/R2 寻址模式
        });
        // 桶内唯一前缀：并行测试类键空间隔离
        return new PrefixedObjectStore(store, $"t{Guid.NewGuid():N}/");
    }
}

/// <summary>桶内前缀装饰器——键空间隔离（测试并行互不干扰）。裸键先过契约校验再拼前缀（校验不被前缀掩盖）。</summary>
internal sealed class PrefixedObjectStore(IObjectStore inner, string prefix) : IObjectStore
{
    private string K(string key)
    {
        ObjectKeyValidator.Validate(key);
        return prefix + key;
    }

    public ObjectStoreCapabilities Capabilities => inner.Capabilities;

    public ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, ObjectMetadata? metadata = null,
                              PutCondition? condition = null, CancellationToken ct = default)
        => inner.PutAsync(K(key), data, metadata, condition, ct);

    public ValueTask PutAsync(string key, Stream data, long length, ObjectMetadata? metadata = null,
                              PutCondition? condition = null, CancellationToken ct = default)
        => inner.PutAsync(K(key), data, length, metadata, condition, ct);

    public ValueTask<int> GetAsync(string key, long offset, Memory<byte> destination, CancellationToken ct = default)
        => inner.GetAsync(K(key), offset, destination, ct);

    public ValueTask<ObjectInfo?> HeadAsync(string key, CancellationToken ct = default)
        => inner.HeadAsync(K(key), ct);

    public ValueTask DeleteAsync(string key, DeleteCondition? condition = null, CancellationToken ct = default)
        => inner.DeleteAsync(K(key), condition, ct);

    public async ValueTask<IReadOnlyList<ObjectEntry>> ListAsync(string? innerPrefix = null, CancellationToken ct = default)
    {
        // 返回键剥前缀——命名空间视图对称（进出同键形）
        var entries = await inner.ListAsync(innerPrefix is null ? prefix : prefix + innerPrefix, ct);
        return entries.Select(e => new ObjectEntry(e.Key[prefix.Length..], e.Size)).ToArray();
    }

    public ValueTask CopyAsync(string sourceKey, string destKey, CopyMetadata? metadata = null,
                               CancellationToken ct = default)
        => inner.CopyAsync(K(sourceKey), K(destKey), metadata, ct);

    public ValueTask<ObjectMetadata> CopyMetadataAsync(string sourceKey, ObjectMetadata? replace = null,
                                                       CancellationToken ct = default)
        => inner.CopyMetadataAsync(K(sourceKey), replace, ct);

    public IMultipartUpload CreateMultipartUpload(string key, ObjectMetadata? metadata = null)
        => new PrefixedMultipartUpload(inner.CreateMultipartUpload(K(key), metadata), this);

    public async ValueTask<IReadOnlyList<MultipartUploadSession>> ListMultipartUploadsAsync(CancellationToken ct = default)
    {
        // 键空间视图对称：过滤本前缀 + 剥前缀
        var sessions = await inner.ListMultipartUploadsAsync(ct);
        return sessions.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(x => x with { Key = x.Key[prefix.Length..] })
            .ToArray();
    }

    public ValueTask AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default)
        => inner.AbortMultipartUploadAsync(K(key), uploadId, ct);

    public ValueTask<long> CopyRangeAsync(string sourceKey, string destKey, long sourceOffset, long length,
                                          CopyMetadata? metadata = null, CancellationToken ct = default)
        => inner.CopyRangeAsync(K(sourceKey), K(destKey), sourceOffset, length, metadata, ct);

    public void Dispose()
        => inner.Dispose();

    /// <summary>multipart 会话包装——UploadPartCopyAsync 的 sourceKey 也须进前缀空间。</summary>
    private sealed class PrefixedMultipartUpload(IMultipartUpload inner, PrefixedObjectStore owner) : IMultipartUpload
    {
        public ValueTask<UploadPartResult> UploadPartAsync(int partNumber, ReadOnlyMemory<byte> data,
                                                           CancellationToken ct = default)
            => inner.UploadPartAsync(partNumber, data, ct);

        public ValueTask<UploadPartResult> UploadPartCopyAsync(int partNumber, string sourceKey,
                                                               long sourceOffset, long length,
                                                               CancellationToken ct = default)
            => inner.UploadPartCopyAsync(partNumber, owner.K(sourceKey), sourceOffset, length, ct);

        public ValueTask CompleteAsync(IReadOnlyList<UploadPartResult> parts, CancellationToken ct = default)
            => inner.CompleteAsync(parts, ct);

        public ValueTask AbortAsync(CancellationToken ct = default)
            => inner.AbortAsync(ct);

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();
    }
}
