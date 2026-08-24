using TC.Tier.Core.IO;
using Xunit;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// IObjectStore 契约平权测试套（§7.1）——同一套断言跑 MemoryObjectStore / S3ObjectStore(MinIO) /
/// S3ObjectStore(真 S3，可选)："三介质平权"哲学在对象层的投影。
/// <para>★ 公开抽象（public）：跨测试工程复用（TC.Tier.Core.IO.S3.Tests 继承本类跑同套断言）。</para>
/// <para>★ 断言纪律：只依赖 IObjectStore 表面与文档化语义——不断言实现细节（ETag 具体格式等）；
///   ETag 只断言"内容变化即变化 + IfMatch 回路可用"。</para>
/// </summary>
public abstract class ObjectStoreContractTests : IDisposable
{
    /// <summary>创建受测 store（子类提供实现——每测试新实例）。</summary>
    protected abstract IObjectStore CreateStore();

    private IObjectStore? _store;

    /// <summary>受测 store（lazy——首个测试访问时创建；门控实现在此抛 SkipException 落在测试方法内）。</summary>
    protected IObjectStore Store => _store ??= CreateStore();

    public virtual void Dispose()
    {
        _store?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static byte[] Bytes(int len, byte seed = 0xAB)
    {
        var b = new byte[len];
        Random.Shared.NextBytes(b.AsSpan());
        b[0] = seed;
        return b;
    }

    // ═════════════════════════════ 六件套语义 ═════════════════════════════

    [SkippableFact]
    public async Task Put_IsIdempotentReplace()
    {
        await Store.PutAsync("a", new byte[] { 1, 2, 3 });
        await Store.PutAsync("a", new byte[] { 4 });
        var info = await Store.HeadAsync("a");
        info.Should().NotBeNull();
        info!.Size.Should().Be(1);
        var buf = new byte[8];
        (await Store.GetAsync("a", 0, buf)).Should().Be(1);
        buf[0].Should().Be(4);
    }

    [SkippableFact]
    public async Task Put_EmptyObject_RoundTrips()
    {
        await Store.PutAsync("empty", ReadOnlyMemory<byte>.Empty);
        var info = await Store.HeadAsync("empty");
        info!.Size.Should().Be(0);
        (await Store.GetAsync("empty", 0, new byte[4])).Should().Be(0);   // EOF → 0
    }

    [SkippableFact]
    public async Task Put_MetadataRoundTrips()
    {
        var meta = ObjectMetadata.Create(new Dictionary<string, string> { ["engine-meta"] = "v1" });
        await Store.PutAsync("m", new byte[] { 1 }, meta);
        (await Store.HeadAsync("m"))!.Metadata.UserMetadata["engine-meta"].Should().Be("v1");

        // 元数据随 PUT 原子替换：无 metadata 的 PUT 清空
        await Store.PutAsync("m", new byte[] { 2 });
        (await Store.HeadAsync("m"))!.Metadata.UserMetadata.Count.Should().Be(0);
    }

    [SkippableFact]
    public async Task Put_StreamLengthKnown_ReadsExactly()
    {
        using var ms = new MemoryStream(new byte[] { 9, 8, 7, 6 });
        await Store.PutAsync("s", ms, 4);
        (await Store.HeadAsync("s"))!.Size.Should().Be(4);
    }

    [SkippableFact]
    public async Task Put_StreamTooShort_Throws()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2 });
        var act = () => Store.PutAsync("s2", ms, 5).AsTask();
        await act.Should().ThrowAsync<Exception>();   // 长度已知流契约——流不足即失败
    }

    [SkippableFact]
    public async Task Get_RangeBoundaries()
    {
        var data = Bytes(1000, 0x5A);
        await Store.PutAsync("r", data);
        var buf = new byte[400];

        // 中段
        (await Store.GetAsync("r", 300, buf)).Should().Be(400);
        buf.ToArray().Should().Equal(data.AsSpan(300, 400).ToArray());

        // 越尾截断（pread EOF 语义）
        (await Store.GetAsync("r", 900, buf)).Should().Be(100);
        buf.AsSpan(0, 100).ToArray().Should().Equal(data.AsSpan(900).ToArray());

        // 恰在 EOF / 越过 EOF → 0（416 归一 0，不抛）
        (await Store.GetAsync("r", 1000, buf)).Should().Be(0);
        (await Store.GetAsync("r", 5000, buf)).Should().Be(0);

        // offset 0 全量
        (await Store.GetAsync("r", 0, new byte[1000])).Should().Be(1000);
    }

    [SkippableFact]
    public async Task Get_MissingKey_ThrowsNotFound()
    {
        var store = Store;
        var act = () => store.GetAsync("nope", 0, new byte[4]).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.NotFound);
    }

    [SkippableFact]
    public async Task Head_Missing_ReturnsNull()
    {
        (await Store.HeadAsync("ghost")).Should().BeNull();
    }

    [SkippableFact]
    public async Task Delete_Idempotent_MissingStillSucceeds()
    {
        await Store.PutAsync("d", new byte[] { 1 });
        await Store.DeleteAsync("d");
        (await Store.HeadAsync("d")).Should().BeNull();
        var act = () => Store.DeleteAsync("d").AsTask();   // 再删不抛（POSIX unlink 对齐）
        await act.Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task List_PrefixFilter_AndLargeKeySpace()
    {
        // >1000 键：S3 侧触发分页归一（max-keys=1000 循环），Memory 侧直接全量——契约同为完整
        const int count = 1050;
        for (var i = 0; i < count; i++)
            await Store.PutAsync($"bulk/{i:D5}", new byte[] { (byte)i });

        var all = await Store.ListAsync("bulk/");
        all.Count.Should().Be(count);

        var slice = await Store.ListAsync("bulk/0009");
        slice.Count.Should().Be(10);   // 00090–00099
        slice.All(e => e.Key.StartsWith("bulk/0009", StringComparison.Ordinal)).Should().BeTrue();
    }

    // ═════════════════════════════ 分隔符列举（目录模拟底座）═════════════════════════════

    private static readonly string[] DlAllKeys = ["dl/a", "dl/b/c"];
    private static readonly string[] DlRootObjects = ["top"];
    private static readonly string[] DlRootPrefixes = ["a/", "b/"];
    private static readonly string[] DlSubObjects = ["a/1", "a/2"];
    private static readonly string[] DlSubPrefixes = ["a/deep/"];

    [SkippableFact]
    public async Task ListDelimited_NoDelimiter_EquivalentToList()
    {
        await Store.PutAsync("dl/a", new byte[] { 1 });
        await Store.PutAsync("dl/b/c", new byte[] { 2 });
        var listing = await Store.ListDelimitedAsync("dl/");
        listing.CommonPrefixes.Count.Should().Be(0);
        listing.Objects.Select(e => e.Key).Should().BeEquivalentTo(DlAllKeys);
    }

    [SkippableFact]
    public async Task ListDelimited_ObjectsAndCommonPrefixes()
    {
        // 布局：top、a/1、a/2、a/deep/3、b/4 —— delimiter="/" 根层视角
        await Store.PutAsync("top", new byte[] { 1 });
        await Store.PutAsync("a/1", new byte[] { 2 });
        await Store.PutAsync("a/2", new byte[] { 3 });
        await Store.PutAsync("a/deep/3", new byte[] { 4 });
        await Store.PutAsync("b/4", new byte[] { 5 });

        var root = await Store.ListDelimitedAsync(prefix: null, delimiter: "/");
        root.Objects.Select(e => e.Key).Should().BeEquivalentTo(DlRootObjects);
        root.CommonPrefixes.Should().BeEquivalentTo(DlRootPrefixes);

        // 子前缀视角：a/ 下——对象 a/1、a/2 + 前缀 a/deep/
        var sub = await Store.ListDelimitedAsync("a/", "/");
        sub.Objects.Select(e => e.Key).Should().BeEquivalentTo(DlSubObjects);
        sub.CommonPrefixes.Should().BeEquivalentTo(DlSubPrefixes);
    }

    [SkippableFact]
    public async Task HeadAndList_ReportLastModified()
    {
        await Store.PutAsync("lm-obj", new byte[] { 1 });
        var info = await Store.HeadAsync("lm-obj");
        info!.LastModified.Should().NotBeNull("HeadObject 必报 LastModified（桥接 FsEntry 时间戳）");
        info.LastModified!.Value.Should().BeOnOrAfter(DateTimeOffset.UtcNow.AddMinutes(-5));

        var entry = (await Store.ListAsync("lm-obj")).Single();
        entry.LastModified.Should().NotBeNull("ListObjectsV2 条目天然携带 LastModified");
        entry.LastModified!.Value.Should().BeOnOrAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [SkippableFact]
    public async Task ListDelimited_EmptyPrefix_NoKeys_NoResults()
    {
        var listing = await Store.ListDelimitedAsync("no-such-prefix-", "/");
        listing.Objects.Count.Should().Be(0);
        listing.CommonPrefixes.Count.Should().Be(0);
    }

    [SkippableFact]
    public async Task Copy_IsIndependent_DeepCopy()
    {
        var data = Bytes(64, 0x77);
        var meta = ObjectMetadata.Create(new Dictionary<string, string> { ["k"] = "v" });
        await Store.PutAsync("src", data, meta);
        await Store.CopyAsync("src", "dst");

        // 拷贝后改源——目标内容/元数据不受影响
        await Store.PutAsync("src", new byte[] { 1 });
        var buf = new byte[64];
        (await Store.GetAsync("dst", 0, buf)).Should().Be(64);
        buf.ToArray().Should().Equal(data);

        // 默认复制源元数据；CopyMetadata 替换
        (await Store.HeadAsync("dst"))!.Metadata.UserMetadata["k"].Should().Be("v");
        await Store.CopyAsync("src", "dst2", new CopyMetadata(ObjectMetadata.Create(null)));
        (await Store.HeadAsync("dst2"))!.Metadata.UserMetadata.Count.Should().Be(0);
    }

    [SkippableFact]
    public async Task Copy_MissingSource_ThrowsNotFound()
    {
        var store = Store;
        var act = () => store.CopyAsync("nope", "dst").AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.NotFound);
    }

    [SkippableFact]
    public async Task CopyMetadata_ReplacesOrPreserves()
    {
        var meta1 = ObjectMetadata.Create(new Dictionary<string, string> { ["gen"] = "1" });
        await Store.PutAsync("cm", new byte[] { 1, 2 }, meta1);

        // 保留（replace=null）
        var kept = await Store.CopyMetadataAsync("cm");
        kept.UserMetadata["gen"].Should().Be("1");

        // 替换
        var meta2 = ObjectMetadata.Create(new Dictionary<string, string> { ["gen"] = "2" });
        var replaced = await Store.CopyMetadataAsync("cm", meta2);
        replaced.UserMetadata["gen"].Should().Be("2");
        (await Store.HeadAsync("cm"))!.Metadata.UserMetadata["gen"].Should().Be("2");

        // 内容不受扰动
        (await Store.HeadAsync("cm"))!.Size.Should().Be(2);
    }

    // ═════════════════════════════ 条件写矩阵（fencing 底座）═════════════════════════════

    [SkippableFact]
    public async Task ConditionalPut_IfNoneMatchStar_抢建Semantics()
    {
        // 空位抢建成功
        await Store.PutAsync("lock", new byte[] { 1 }, condition: new PutCondition(IfMatch: null, IfNoneMatch: "*"));

        // 已存在 → PreconditionFailed（对象不被修改）
        var act = () => Store.PutAsync("lock", new byte[] { 2 },
            condition: new PutCondition(IfMatch: null, IfNoneMatch: "*")).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.PreconditionFailed);
        (await Store.GetAsync("lock", 0, new byte[1])).Should().Be(1);
        var buf = new byte[1];
        await Store.GetAsync("lock", 0, buf);
        buf[0].Should().Be(1);
    }

    [SkippableFact]
    public async Task ConditionalPut_IfMatch_CASReplace()
    {
        await Store.PutAsync("cas", new byte[] { 1 });
        var etag = (await Store.HeadAsync("cas"))!.ETag!;

        // 命中 → 替换成功
        await Store.PutAsync("cas", new byte[] { 2 }, condition: new PutCondition(etag, null));

        // 失配（ETag 已变）→ PreconditionFailed
        var act = () => Store.PutAsync("cas", new byte[] { 3 },
            condition: new PutCondition(etag, null)).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.PreconditionFailed);
        var buf = new byte[1];
        await Store.GetAsync("cas", 0, buf);
        buf[0].Should().Be(2);
    }

    [SkippableFact]
    public async Task ConditionalPut_IfMatch_MissingObject_ThrowsNotFound()
    {
        var store = Store;
        var act = () => store.PutAsync("absent", new byte[] { 1 },
            condition: new PutCondition("whatever-etag", null)).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.NotFound);
    }

    [SkippableFact]
    public async Task ConditionalDelete_IfMatch_TokenGuard()
    {
        await Store.PutAsync("l", new byte[] { 1 });
        var etag = (await Store.HeadAsync("l"))!.ETag!;

        // 他人已替换（ETag 变化）→ 拒绝误删
        await Store.PutAsync("l", new byte[] { 2 });
        var act = () => Store.DeleteAsync("l", new DeleteCondition(etag)).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.PreconditionFailed);
        (await Store.HeadAsync("l")).Should().NotBeNull();

        // 持有者（当前 ETag）删除成功
        var current = (await Store.HeadAsync("l"))!.ETag!;
        await Store.DeleteAsync("l", new DeleteCondition(current));
        (await Store.HeadAsync("l")).Should().BeNull();
    }

    // ═════════════════════════════ multipart 原语族 ═════════════════════════════

    [SkippableFact]
    public async Task Multipart_UploadsJoinInPartNumberOrder()
    {
        // ★ 真实 S3 约束：非末位 part ≥5MiB（EntityTooSmall）——契约测试按真实尺寸（桥层 PartSize 亦 ≥5MB）
        const int partSize = 5 * 1024 * 1024;
        var p1Data = new byte[partSize];
        p1Data.AsSpan().Fill(0x11);
        var p2Data = new byte[partSize];
        p2Data.AsSpan().Fill(0x22);
        var session = Store.CreateMultipartUpload("mp");
        var p3 = await session.UploadPartAsync(3, new byte[] { 0x33, 0x33 });   // 末位 part 无尺寸下限
        var p1 = await session.UploadPartAsync(1, p1Data);                      // 乱序上传
        var p2 = await session.UploadPartAsync(2, p2Data);

        await session.CompleteAsync([p1, p2, p3]);
        (await Store.HeadAsync("mp"))!.Size.Should().Be(2L * partSize + 2);
        var buf = new byte[partSize + 2];
        (await Store.GetAsync("mp", 0, buf)).Should().Be(partSize + 2);
        buf[0].Should().Be((byte)0x11);                                          // part1 起头
        buf[partSize - 1].Should().Be((byte)0x11);
        buf[partSize].Should().Be((byte)0x22);                                   // part2 紧随（PartNumber 序）
        var tail = new byte[2];
        (await Store.GetAsync("mp", 2L * partSize, tail)).Should().Be(2);
        tail.Should().Equal((byte)0x33, (byte)0x33);                             // part3 收尾（非上传顺序）
    }

    [SkippableFact]
    public async Task Multipart_Abort_DiscardsParts()
    {
        var session = Store.CreateMultipartUpload("aborted");
        var p = await session.UploadPartAsync(1, new byte[] { 1 });
        await session.AbortAsync();
        (await Store.HeadAsync("aborted")).Should().BeNull();

        // 会话已终结：后续操作 NoSuchUpload（NotFound 归一——桥层视为"已 complete 过"回读校验）
        var act = () => session.CompleteAsync([p]).AsTask();
        (await act.Should().ThrowAsync<FileIOException>()).Which.Error.Should().Be(IOError.NotFound);
    }

    [SkippableFact]
    public async Task Multipart_CompleteReplacesExistingObject()
    {
        await Store.PutAsync("mp2", new byte[] { 0xFF, 0xFF });
        var session = Store.CreateMultipartUpload("mp2");
        var p1 = await session.UploadPartAsync(1, new byte[] { 1, 2, 3 });
        await session.CompleteAsync([p1]);
        (await Store.HeadAsync("mp2"))!.Size.Should().Be(3);
    }

    [SkippableFact]
    public async Task Multipart_MetadataAttachedOnComplete()
    {
        var meta = ObjectMetadata.Create(new Dictionary<string, string> { ["who"] = "mp" });
        var session = Store.CreateMultipartUpload("mpm", meta);
        var p1 = await session.UploadPartAsync(1, new byte[] { 1 });
        await session.CompleteAsync([p1]);
        (await Store.HeadAsync("mpm"))!.Metadata.UserMetadata["who"].Should().Be("mp");
    }

    // ═════════════════════════════ CopyRange 原语 ═════════════════════════════

    [SkippableFact]
    public async Task CopyRange_CreatesNewDestFromSourceSlice()
    {
        var data = Bytes(256, 0x3C);
        await Store.PutAsync("cr-src", data);
        var copied = await Store.CopyRangeAsync("cr-src", "cr-dst", 100, 50);
        copied.Should().Be(50);
        var buf = new byte[64];
        (await Store.GetAsync("cr-dst", 0, buf)).Should().Be(50);
        buf.AsSpan(0, 50).ToArray().Should().Equal(data.AsSpan(100, 50).ToArray());
    }

    [SkippableFact]
    public async Task CopyRange_ClampsAtSourceEof()
    {
        await Store.PutAsync("cr-small", new byte[10]);
        var copied = await Store.CopyRangeAsync("cr-small", "cr-out", 5, 100);
        copied.Should().Be(5);   // 返回实际拷贝长度
        (await Store.HeadAsync("cr-out"))!.Size.Should().Be(5);
    }

    // ═════════════════════════════ 键校验（契约冻结项）═════════════════════════════

    [SkippableFact]
    public async Task Operations_WithIllegalKeys_ThrowArgument()
    {
        var store = Store;
        foreach (var key in new[] { "", "a\0b" })
        {
            await store.Invoking(s => s.PutAsync(key, new byte[] { 1 }).AsTask()).Should().ThrowAsync<ArgumentException>();
            await store.Invoking(s => s.GetAsync(key, 0, new byte[1]).AsTask()).Should().ThrowAsync<ArgumentException>();
            await store.Invoking(s => s.HeadAsync(key).AsTask()).Should().ThrowAsync<ArgumentException>();
        }
    }

    [SkippableFact]
    public async Task Put_StreamUnknownLength_RoundTrips()
    {
        var store = Store;
        var data = new byte[300 * 1024];   // > 单 chunk 尺寸——多 chunk 场景
        new Random(31).NextBytes(data);
        await store.PutAsync("ulk", new NonSeekableStream(new MemoryStream(data)), length: -1);
        (await store.HeadAsync("ulk"))!.Size.Should().Be(data.Length);
        var buf = new byte[data.Length];
        (await store.GetAsync("ulk", 0, buf)).Should().Be(data.Length);
        buf.Should().Equal(data);
    }

    /// <summary>不可寻包装（CanSeek=false——逼出 chunked 流式路径）。</summary>
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

    // ═════════════════════════════ 会话治理原语（增补设计 §2）═════════════════════════════

    [SkippableFact]
    public async Task MultipartSessionGovernance_ListAndAbortById()
    {
        var store = Store;
        // 孤儿会话：创建 + 上传 part 但不 complete（崩溃残留形态）
        var session = store.CreateMultipartUpload("orphan", null);
        await session.UploadPartAsync(1, new byte[] { 1 });

        var sessions = await store.ListMultipartUploadsAsync();
        var found = sessions.FirstOrDefault(s => s.Key == "orphan");
        found.Should().NotBeNull();
        found!.UploadId.Should().NotBeNullOrEmpty();
        found.InitiatedUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        // 定向清理（非会话句柄路径——孤儿扫描的同款原语）
        await store.AbortMultipartUploadAsync("orphan", found.UploadId);
        (await store.ListMultipartUploadsAsync()).Should().NotContain(s => s.Key == "orphan");
        (await store.HeadAsync("orphan")).Should().BeNull();

        // 幂等：NoSuchUpload 归一——再 abort 不抛
        var act = () => store.AbortMultipartUploadAsync("orphan", found.UploadId).AsTask();
        await act.Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task StreamingList_YieldsSameAsBulk()
    {
        var store = Store;
        for (var i = 0; i < 5; i++)
            await store.PutAsync($"stream/{i:D2}", new byte[] { (byte)i });
        var streamed = new List<ObjectEntry>();
        await foreach (var e in store.ListStreamingAsync("stream/"))
            streamed.Add(e);
        streamed.Count.Should().Be(5);
        var bulk = await store.ListAsync("stream/");
        streamed.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key)
            .Should().Equal(bulk.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key).ToArray());
    }

    // ═════════════════════════════ 同步便捷包装 ═════════════════════════════

    [SkippableFact]
    public void SyncWrappers_Work()
    {
        Store.Put("sync", new byte[] { 7, 7, 7 });
        Store.Head("sync")!.Size.Should().Be(3);
        var buf = new byte[2];
        Store.Get("sync", 1, buf).Should().Be(2);
        Store.List().Count.Should().BeGreaterThanOrEqualTo(1);
        Store.Copy("sync", "sync2");
        Store.Head("sync2").Should().NotBeNull();
        Store.CopyMetadata("sync2");
        Store.Delete("sync2");
        Store.Head("sync2").Should().BeNull();
    }
}
