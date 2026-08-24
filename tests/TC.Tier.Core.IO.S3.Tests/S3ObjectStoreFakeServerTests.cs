using TC.Tier.Core.IO;
using TC.Tier.Core.IO.S3;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.IO.S3.Tests;

/// <summary>
/// 假 S3 服务器全路径测试——真实 HTTP + SigV4 + XML：签名被服务端校验接受（结构性正确）、
/// 请求形态（path-style/查询编码/metadata 头）、错误映射（404/412/416）、重试矩阵（503 指数退避）。
/// </summary>
public class S3ObjectStoreFakeServerTests : IDisposable
{
    private readonly FakeS3Server _server = new();
    private readonly S3ObjectStore _store;

    public S3ObjectStoreFakeServerTests()
    {
        _store = S3ObjectStore.Create(new S3ClientOptions
        {
            Endpoint = _server.Endpoint,
            Bucket = "test-bucket",
            Region = "us-east-1",
            Credentials = new StaticCredentials(FakeS3Server.AccessKey, FakeS3Server.Secret),
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        });
    }

    public void Dispose()
    {
        _store.Dispose();
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    // ═══════════════ 分隔符列举（原生 delimiter——ListObjectsV2 + CommonPrefixes）═══════════════

    private static readonly string[] DelimitedKeys =
        ["top", "a/1", "a/2", "a/deep/3", "b/4", "b/deeper/x"];

    private async Task SeedDelimitedAsync()
    {
        foreach (var k in DelimitedKeys)
            await _store.PutAsync(k, new byte[] { 1 });
    }

    [Fact]
    public async Task ListDelimited_RootLevel_ObjectsAndCommonPrefixes()
    {
        await SeedDelimitedAsync();
        var root = await _store.ListDelimitedAsync(prefix: null, delimiter: "/");
        root.Objects.Select(e => e.Key).Should().Equal("top");
        root.CommonPrefixes.Should().Equal("a/", "b/");
        _server.SignatureFailures.Should().Be(0);
    }

    [Fact]
    public async Task ListDelimited_SubPrefix_NestedCommonPrefixes()
    {
        await SeedDelimitedAsync();
        var sub = await _store.ListDelimitedAsync("a/", "/");
        sub.Objects.Select(e => e.Key).Should().Equal("a/1", "a/2");
        sub.CommonPrefixes.Should().Equal("a/deep/");
    }

    [Fact]
    public async Task ListDelimited_NoDelimiter_EquivalentToList()
    {
        await SeedDelimitedAsync();
        var listing = await _store.ListDelimitedAsync(prefix: null, delimiter: null);
        listing.CommonPrefixes.Should().BeEmpty();
        listing.Objects.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal)
            .Should().Equal(DelimitedKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListDelimited_EmptyPrefix_NoResults()
    {
        await SeedDelimitedAsync();
        var listing = await _store.ListDelimitedAsync("zz/", "/");
        listing.Objects.Should().BeEmpty();
        listing.CommonPrefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task SixPieceRoundTrip_SignatureAccepted()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        try { await _store.PutAsync("hello", data); }
        catch (FileIOException ex)
        {
#if DEBUG
            var diagPath = Path.Combine(Path.GetTempPath(), "sigdiag");
            Directory.CreateDirectory(diagPath);
            var nl = Environment.NewLine;
            File.WriteAllText(Path.Combine(diagPath, "client.txt"),
                (SigV4.LastStringToSign ?? "<null>") + nl + "===" + nl + (SigV4.LastCanonical ?? "<null>"));
            File.WriteAllText(Path.Combine(diagPath, "server.txt"), _server.LastSignatureDiag ?? "<null>");
            throw new Xunit.Sdk.XunitException($"{ex.Message} diag written to {diagPath}");
#else
            throw new Xunit.Sdk.XunitException(ex.Message);
#endif
        }
        _server.SignatureFailures.Should().Be(0);   // ★ 每个请求签名均被服务端重算接受

        (await _store.HeadAsync("hello"))!.Size.Should().Be(5);

        var buf = new byte[3];
        (await _store.GetAsync("hello", 1, buf)).Should().Be(3);
        buf.Should().Equal(2, 3, 4);

        (await _store.ListAsync()).Count.Should().Be(1);
        await _store.DeleteAsync("hello");
        (await _store.HeadAsync("hello")).Should().BeNull();
        _server.SignatureFailures.Should().Be(0);
    }

    [Fact]
    public async Task SpecialKey_EncodesPathCorrectly()
    {
        // 空格 + 中文 + 保留字符——编码路径必须与服务端解码往返一致
        var key = "seg ment/键=k&+q";
        await _store.PutAsync(key, new byte[] { 9 });
        (await _store.HeadAsync(key))!.Size.Should().Be(1);

        // 原始请求路径是编码形态（%20/%E9%94%AE——大写 hex，'/' 直通）
        _server.LastRawPath.Should().Contain("%20");
        _server.LastRawPath.Should().Contain("%E9%94%AE");
        _server.LastRawPath.Should().Contain("%3D");
        _server.LastRawPath.Should().NotContainAny([" ", "键"]);
    }

    [Fact]
    public async Task ConditionalPut_PreconditionFailed_Mapped()
    {
        await _store.PutAsync("c", new byte[] { 1 });
        var etag = (await _store.HeadAsync("c"))!.ETag!;

        var act = () => _store.PutAsync("c", new byte[] { 2 },
            condition: new PutCondition(IfMatch: "wrong-etag", IfNoneMatch: null)).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.PreconditionFailed);
    }

    [Fact]
    public async Task Get_OutOfRange_ReturnsZero_NotThrows()
    {
        await _store.PutAsync("small", new byte[] { 1, 2 });
        (await _store.GetAsync("small", 100, new byte[4])).Should().Be(0);   // 416 → 0 归一
    }

    [Fact]
    public async Task Get_MissingKey_MapsNotFound()
    {
        var act = () => _store.GetAsync("absent", 0, new byte[4]).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public async Task Metadata_RoundTripsViaHeaders()
    {
        var meta = ObjectMetadata.Create(new Dictionary<string, string> { ["engine-meta"] = "gen-42" });
        await _store.PutAsync("m", new byte[] { 1 }, meta);
        var info = await _store.HeadAsync("m");
        info!.Metadata.UserMetadata["engine-meta"].Should().Be("gen-42");
    }

    [Fact]
    public async Task Retry_On503_EventuallySucceeds()
    {
        _server.FailNthRequest = 1;   // 第 1 次注入 503——客户端应重试成功
        await _store.PutAsync("retry", new byte[] { 7 });
        _server.RequestCount.Should().BeGreaterThanOrEqualTo(2);   // 至少 1 次重试
        _server.SignatureFailures.Should().Be(0);
    }

    [Fact]
    public async Task Multipart_FullLifecycle_OverHttp()
    {
        var session = _store.CreateMultipartUpload("mp", null);
        var p1 = await session.UploadPartAsync(1, new byte[] { 1, 2 });
        var p2 = await session.UploadPartAsync(2, new byte[] { 3 });
        await session.CompleteAsync([p1, p2]);

        var buf = new byte[3];
        (await _store.GetAsync("mp", 0, buf)).Should().Be(3);
        buf.Should().Equal(1, 2, 3);

        // 会话终结后再 complete → NoSuchUpload → NotFound（契约归一）
        var act = () => session.CompleteAsync([p1, p2]).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public async Task Multipart_UploadPartCopy_ServerSide()
    {
        await _store.PutAsync("src", new byte[] { 0x21, 0x22, 0x23 });
        var session = _store.CreateMultipartUpload("mp-copy", null);
        var p1 = await session.UploadPartAsync(1, new byte[] { 0x11 });
        var p2 = await session.UploadPartCopyAsync(2, "src", 1, 2);
        await session.CompleteAsync([p1, p2]);
        var buf = new byte[3];
        (await _store.GetAsync("mp-copy", 0, buf)).Should().Be(3);
        buf.Should().Equal((byte)0x11, 0x22, 0x23);
    }

    [Fact]
    public async Task Multipart_Abort_Discards()
    {
        var session = _store.CreateMultipartUpload("ab");
        await session.UploadPartAsync(1, new byte[] { 1 });
        await session.AbortAsync();
        (await _store.HeadAsync("ab")).Should().BeNull();
    }

    [Fact]
    public async Task Copy_ServerSide_SourceMissing404()
    {
        var act = () => _store.CopyAsync("no-such", "dst").AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public async Task CopyRange_MultipartOrchestration()
    {
        var data = new byte[1000];
        new Random(42).NextBytes(data);
        await _store.PutAsync("cr-src", data);
        var copied = await _store.CopyRangeAsync("cr-src", "cr-dst", 100, 500);
        copied.Should().Be(500);
        var buf = new byte[500];
        (await _store.GetAsync("cr-dst", 0, buf)).Should().Be(500);
        buf.ToArray().Should().Equal(data.AsSpan(100, 500).ToArray());
    }

    [Fact]
    public async Task List_PaginationIsNormalized()
    {
        _server.MaxKeys = 3;
        for (var i = 0; i < 10; i++)
            await _store.PutAsync($"p/{i:D2}", new byte[] { (byte)i });
        var all = await _store.ListAsync("p/");
        all.Count.Should().Be(10);   // 分页循环归一——消费者不可见
    }

    [Fact]
    public async Task PutStream_StreamsKnownLength()
    {
        using var ms = new MemoryStream(new byte[] { 4, 4, 4 });
        await _store.PutAsync("s", ms, 3);
        (await _store.HeadAsync("s"))!.Size.Should().Be(3);
    }

    [Fact]
    public void ChunkedSignedStream_ProducesFramedBytes()
    {
        // 隔离验证：流本体分帧正确（编码长度精确 / 首帧头形态 / 总字节可读尽）
        var data = new byte[300 * 1024];
        new Random(23).NextBytes(data);
        var key = SigV4.DeriveSigningKey("k", "20260818", "us-east-1", "s3");
        using var framed = new ChunkedSignedStream(new MemoryStream(data), key, "seed".PadLeft(64, '0'),
            "20260818T000000Z", "20260818/us-east-1/s3/aws4_request");
        var expectedTotal = ChunkedSignedStream.EncodedLength(data.Length);
        long totalRead = 0;
        var buf = new byte[64 * 1024];
        int n;
        var firstChunk = new List<byte>();
        while ((n = framed.Read(buf, 0, buf.Length)) > 0)
        {
            totalRead += n;
            if (firstChunk.Count < 64) firstChunk.AddRange(buf[..Math.Min(n, 64 - firstChunk.Count)]);
        }
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "streamdiag.txt"),
            $"totalRead={totalRead} expected={expectedTotal} firstN={firstChunk.Count} canRead={framed.CanRead} srcLen={data.Length}");
        totalRead.Should().Be(expectedTotal);
        var head = System.Text.Encoding.ASCII.GetString(firstChunk.ToArray());
        head.Should().StartWith("20000;chunk-signature=");
        head.Should().NotContain("seed");   // 链已演进（非种子原样）
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task PutChunked_NonSeekableKnownLength_ServerVerifiesChain()
    {
        // 不可寻 + 长度已知 → chunked 流式签名；服务端逐 chunk 独立重算链（任何一环不符即 403）
        var data = new byte[300 * 1024];
        new Random(17).NextBytes(data);
        await _store.PutAsync("chk", new NonSeekableStream(new MemoryStream(data)), 300 * 1024);
        _server.SignatureFailures.Should().Be(0);
        var buf = new byte[data.Length];
        (await _store.GetAsync("chk", 0, buf)).Should().Be(data.Length);
        buf.Should().Equal(data);
    }

    [Fact]
    public async Task PutChunked_UnknownLength_SpoolPath()
    {
        var data = new byte[150 * 1024];
        new Random(19).NextBytes(data);
        await _store.PutAsync("ulk", new NonSeekableStream(new MemoryStream(data)), -1);
        _server.SignatureFailures.Should().Be(0);
        (await _store.HeadAsync("ulk"))!.Size.Should().Be(data.Length);
    }

    [Fact]
    public async Task SessionGovernance_OverHttp()
    {
        var session = _store.CreateMultipartUpload("orph", null);
        await session.UploadPartAsync(1, new byte[] { 1 });

        var sessions = await _store.ListMultipartUploadsAsync();
        var found = sessions.SingleOrDefault(x => x.Key == "orph");
        found.Should().NotBeNull();
        found!.InitiatedUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        await _store.AbortMultipartUploadAsync("orph", found.UploadId);
        (await _store.ListMultipartUploadsAsync()).Should().BeEmpty();
        (await _store.HeadAsync("orph")).Should().BeNull();
    }

    [Fact]
    public async Task StreamingList_S3TrueStreaming_PaginationUnderHood()
    {
        for (var i = 0; i < 10; i++)
            await _store.PutAsync($"st/{i:D2}", new byte[] { (byte)i });
        var count = 0;
        await foreach (var e in _store.ListStreamingAsync("st/"))
        {
            e.Key.Should().StartWith("st/");
            count++;
        }
        count.Should().Be(10);
    }

    [Fact]
    public void Capabilities_FromOptions()
    {
        _store.Capabilities.Should().HaveFlag(ObjectStoreCapabilities.ConditionalPut)
            .And.HaveFlag(ObjectStoreCapabilities.ConditionalDelete)
            .And.HaveFlag(ObjectStoreCapabilities.StrongList)
            .And.HaveFlag(ObjectStoreCapabilities.ServerSideCopy)
            .And.HaveFlag(ObjectStoreCapabilities.Multipart)
            .And.HaveFlag(ObjectStoreCapabilities.RangeGet);
    }

    [Fact]
    public async Task ConditionalDelete_WhenDisabled_ThrowsUnsupported()
    {
        using var store = S3ObjectStore.Create(new S3ClientOptions
        {
            Endpoint = _server.Endpoint,
            Bucket = "test-bucket",
            Credentials = new StaticCredentials(FakeS3Server.AccessKey, FakeS3Server.Secret),
            SupportsConditionalDelete = false,
        });
        store.Capabilities.Should().NotHaveFlag(ObjectStoreCapabilities.ConditionalDelete);
        var act = () => store.DeleteAsync("any", new DeleteCondition("etag")).AsTask();
        await act.Should().ThrowAsync<FileIOException>().Where(e => e.Error == IOError.Unsupported);
    }
}
