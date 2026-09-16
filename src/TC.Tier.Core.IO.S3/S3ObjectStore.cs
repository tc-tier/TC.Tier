using System.Globalization;
using System.Net;
using System.Text;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.IO.S3;

/// <summary>
/// S3 兼容对象存储——<see cref="IObjectStore"/> 默认实现（SigV4 自写、零外部依赖）。
/// <para>★ 一个实现覆盖全部 S3 兼容云（S3 / OSS(S3 兼容端点) / MinIO / R2 / B2——换 endpoint/credentials 即达）；
///   COS 不承诺（V5 签名差异大，独立实现另议——设计 §7.5）。</para>
/// <para>★ 寻址：path-style（<c>{endpoint}/{bucket}/{key}</c>）——MinIO/自建端点标准形态。
///   canonical URI 与请求 URL 同编码器生成（S3 特例：不归一化不二次编码）。</para>
/// <para>★ 重试矩阵（§9.9）：幂等操作（GET/HEAD/PUT/DELETE/List/UploadPart）对 5xx/429/网络抖动指数退避重试；
///   CreateMultipartUpload 不重试（可能双开会话）；Complete 重放安全（NoSuchUpload → NotFound，桥层回读校验）。</para>
/// <para>★ 线程安全：HttpClient 并发共用（连接池复用）；multipart 会话独立无共享态。</para>
/// </summary>
public sealed class S3ObjectStore : IObjectStore
{
    private const string Service = "s3";
    private const string MetadataHeaderPrefix = "x-amz-meta-";

    private readonly S3ClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private int _disposed;

    /// <summary>构造——按 options 创建内部 HttpClient（连接池 10min 复用）。</summary>
    /// <param name="options">S3 客户端配置（endpoint/credentials/桶/超时/重试等——立即调用 Validate 校验）。</param>
    /// <returns>自管 HttpClient 的新实例（Dispose 时连带释放 handler 与连接池）。</returns>
    /// <exception cref="ArgumentException">options 字段不合法（Bucket/Endpoint/Credentials 任一缺失或不合规）。</exception>
    public static S3ObjectStore Create(S3ClientOptions options)
    {
        options.Validate();
        var handler = new SocketsHttpHandler
        {
            // ★ 连接池双防线（防死连接复用——Aliyun OSS 服务端 60-90s 断开空闲连接；老 SDK
            //   lifetime=0 永不回收 = 周期性 SSL 抖动根因）：寿命到期重建 + 空闲提前主动回收
            //   （默认 60s < 服务端阈值，池中不留死连接——.NET 8 复用失败另有自动重建兜底）
            PooledConnectionLifetime = options.PooledConnectionLifetime ?? TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = options.PooledConnectionIdleTimeout ?? TimeSpan.FromSeconds(60),
            AutomaticDecompression = DecompressionMethods.None,   // 响应体不压缩——避免签名/编码歧义
            UseProxy = false,
        };
        return new S3ObjectStore(options, new HttpClient(handler, disposeHandler: true), ownsHttp: true);
    }

    /// <summary>构造——注入外部 HttpClient（测试假服务器/共享连接池场景）。</summary>
    /// <param name="options">S3 客户端配置（endpoint/credentials/桶/超时等——立即调用 Validate 校验）。</param>
    /// <param name="http">外部注入的 HttpClient（调用方管理生命周期——本实例 Dispose 不释放）。</param>
    /// <returns>复用外部 HttpClient 的新实例（不持有 handler 所有权）。</returns>
    /// <exception cref="ArgumentException">options 字段不合法（Bucket/Endpoint/Credentials 任一缺失或不合规）。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> 为 null。</exception>
    public static S3ObjectStore Create(S3ClientOptions options, HttpClient http)
    {
        options.Validate();
        ArgumentNullException.ThrowIfNull(http);
        return new S3ObjectStore(options, http, ownsHttp: false);
    }

    private S3ObjectStore(S3ClientOptions options, HttpClient http, bool ownsHttp)
    {
        _options = options;
        _http = http;
        _ownsHttp = ownsHttp;
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 超时按请求粒度管（大对象上传 ≠ 小请求超时）
    }

    /// <inheritdoc/>
    public ObjectStoreCapabilities Capabilities
    {
        get
        {
            var caps = ObjectStoreCapabilities.ServerSideCopy
                       | ObjectStoreCapabilities.Multipart
                       | ObjectStoreCapabilities.RangeGet;
            if (_options.SupportsConditionalPut) caps |= ObjectStoreCapabilities.ConditionalPut;
            if (_options.SupportsConditionalDelete) caps |= ObjectStoreCapabilities.ConditionalDelete;
            if (_options.SupportsStrongList) caps |= ObjectStoreCapabilities.StrongList;
            return caps;
        }
    }

    // ═════════════════════════════ 六件套 ═════════════════════════════

    /// <inheritdoc/>
    /// <param name="key">对象键（path-style 路径段，须满足 ObjectKeyValidator 约束）。</param>
    /// <param name="data">待上传字节数据（全量驻内存——单次 PUT 整体发送）。</param>
    /// <param name="metadata">可选对象元数据（写入 x-amz-meta-* 头；null 不写）。</param>
    /// <param name="condition">可选条件 PUT（If-Match/If-None-Match——厂商差异由本地 Head 校验兜底归一）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">key 不满足 ObjectKeyValidator 约束。</exception>
    /// <exception cref="FileIOException">条件 PUT 失配（NotFound/PreconditionFailed）；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
    /// <returns>完成时对象已上传（条件 PUT 失配/服务端错误抛）。</returns>
    public async ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, ObjectMetadata? metadata = null,
                                    PutCondition? condition = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        var payloadHash = SigV4.Sha256Hex(data.Span);
        await PutCoreAsync(key, () => new ByteArrayContent(data.ToArray()), payloadHash, metadata, condition, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>长度已知流：传（须可寻——单遍流式 SHA-256 后回卷流式上哈希后 Position 回卷；不可寻流内部缓冲）。</remarks>
    /// <param name="key">对象键（须满足 ObjectKeyValidator 约束）。</param>
    /// <param name="data">待上传流（length≥0 须可寻以回卷哈希；length&lt;0 走 spool 临时文件中转；不可寻走 chunked）。</param>
    /// <param name="length">流的已知字节长度（负值=未知长度，触发 spool 缓冲；非负且流可寻时须 ≤ 流内剩余可用字节）。</param>
    /// <param name="metadata">可选对象元数据（写入 x-amz-meta-* 头；null 不写）。</param>
    /// <param name="condition">可选条件 PUT（If-Match/If-None-Match——本地校验兜底归一厂商差异）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">key 不合规；或流可寻且可用字节不足 length。</exception>
    /// <exception cref="FileIOException">条件 PUT 失配；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
    /// <returns>完成时对象已上传（长度路径/回卷哈希路径/chunked 路径按流形态内部分派）。</returns>
    public async ValueTask PutAsync(string key, Stream data, long length, ObjectMetadata? metadata = null,
                                    PutCondition? condition = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        if (length < 0)
        {
            // 未知长度：spool 临时文件（磁盘中转——零整驻内存）后 chunked 上传
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tier-put-{Guid.NewGuid():N}");
            try
            {
                await using (var spool = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite,
                                   FileShare.None, 81920, useAsync: true))
                {
                    await data.CopyToAsync(spool, ct).ConfigureAwait(false);
                }
                await using var upload = new FileStream(tempPath, FileMode.Open, FileAccess.Read,
                    FileShare.None, 81920, useAsync: true);
                await PutChunkedAsync(key, upload, upload.Length, metadata, condition, ct).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* 清理尽力 */ }
            }
            return;
        }
        if (!data.CanSeek)
        {
            await PutChunkedAsync(key, data, length, metadata, condition, ct).ConfigureAwait(false);
            return;
        }
        if (data.Length - data.Position < length)
            throw new ArgumentException($"流内可用字节不足（需 {length}，余 {data.Length - data.Position}）。", nameof(data));
        var hash = SigV4.Sha256Hex(data);   // 单遍哈希 + 回卷（后续 StreamContent 从头流式上传）
        await PutCoreAsync(key, () =>
        {
            var content = new StreamContent(data, bufferSize: 81920);
            content.Headers.ContentLength = length;
            return content;
        }, hash, metadata, condition, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// chunked 流式签名 PUT：内容经 <see cref="ChunkedSignedStream"/> 分帧链签（seed → chunk 链 → 终帧）；
    /// HTTP 层 Transfer-Encoding: chunked（Content-Length 不设）。★ 单次发送（源不可回卷——不重试）。
    /// </summary>
    /// <param name="key">对象键（已由调用方校验）。</param>
    /// <param name="source">不可回卷的源流（chunked 签名链的原始数据源——零回卷需求）。</param>
    /// <param name="decodedLength">解码后的实际字节长度（写入 x-amz-decoded-content-length 头——服务端按此对账）。</param>
    /// <param name="metadata">可选对象元数据（写入 x-amz-meta-* 头；null 不写）。</param>
    /// <param name="condition">可选条件 PUT（经 CheckPutConditionLocalAsync 本地兜底）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="FileIOException">条件 PUT 失配；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
    private async ValueTask PutChunkedAsync(string key, Stream source, long decodedLength,
                                            ObjectMetadata? metadata, PutCondition? condition, CancellationToken ct)
    {
        await CheckPutConditionLocalAsync(key, condition, ct).ConfigureAwait(false);
        var extraHeaders = new List<(string, string)>
        {
            ("x-amz-decoded-content-length", decodedLength.ToString(CultureInfo.InvariantCulture)),
        };
        AddMetadataHeaders(extraHeaders, metadata);
        AddConditionHeaders(extraHeaders, condition);
        var (request, ctx) = BuildRequestWithContext(HttpMethod.Put, key, query: null, content: null,
            extraHeaders, SigV4.StreamingContentSha256);
        var content = new StreamContent(new ChunkedSignedStream(source, ctx.SigningKey, ctx.Signature,
            ctx.AmzDate, ctx.Scope));
        // 预计算编码长度设 Content-Length——免 HTTP 层 chunked（部分服务端对该形态请求体支持不佳）
        content.Headers.ContentLength = ChunkedSignedStream.EncodedLength(decodedLength);
        request.Content = content;
        // ★ idempotent:false——HttpContent 已绑定流，工厂不可重建（重试语义归 spool 路径）
        using var response = await SendWithRetryAsync(() => request, idempotent: false, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, key, nameof(PutAsync)).ConfigureAwait(false);
        await DrainBodyAsync(response, ct).ConfigureAwait(false);
    }


    /// <summary>
    /// ★ 厂商差异吸收（§6）：条件 PUT 强制力不一——AWS 严格 / MinIO 缺失对象静默创建 /
    /// COS 完全忽略条件头。客户端前置 Head + 本地校验兜底归一契约语义（接受极小竞态，与
    /// 条件 DELETE 同款声明——fencing 为尽力型，token/心跳校验在接管与释放路径兜底）。
    /// </summary>
    /// <param name="key">对象键（已由调用方校验）。</param>
    /// <param name="condition">条件 PUT（null 或 IfMatch/IfNoneMatch 均 null 时直接返回——无校验）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="FileIOException">NotFound——If-Match 但对象不存在（并发删除或从未创建）；PreconditionFailed——If-Match 不等于当前 ETag（对象已被并发替换），或 If-None-Match:* 撞已存在（抢占失败）。</exception>
    private async ValueTask CheckPutConditionLocalAsync(string key, PutCondition? condition, CancellationToken ct)
    {
        if (condition is not { } c || (c.IfMatch is null && c.IfNoneMatch is null)) return;
        var head = await HeadAsync(key, ct).ConfigureAwait(false);
        if (c.IfMatch is not null)
        {
            if (head is null)
                throw new FileIOException(IOError.NotFound,
                    $"对象不存在（If-Match 无从匹配——并发删除或从未创建）: {key}", key, nameof(PutAsync));
            if (!string.Equals(head.ETag?.Trim('"'), c.IfMatch.Trim('"'), StringComparison.Ordinal))
                throw new FileIOException(IOError.PreconditionFailed,
                    $"条件写失配（If-Match 不等于当前 ETag——对象已被并发替换）: {key}", key, nameof(PutAsync));
        }
        if (c.IfNoneMatch == "*" && head is not null)
            throw new FileIOException(IOError.PreconditionFailed,
                $"条件写失配（If-None-Match:* 撞已存在——抢占失败）: {key}", key, nameof(PutAsync));
    }

    /// <param name="key">对象键（已由调用方校验）。</param>
    /// <param name="contentFactory">HttpContent 工厂（每次重试重建——HttpContent 发送后不可复用）。</param>
    /// <param name="payloadHash">载荷 SHA-256 hex（空 body = SigV4.EmptyPayloadHash）。</param>
    /// <param name="metadata">可选对象元数据（写入 x-amz-meta-* 头；null 不写）。</param>
    /// <param name="condition">可选条件 PUT（经 CheckPutConditionLocalAsync 本地兜底）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="FileIOException">条件 PUT 失配；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
    private async ValueTask PutCoreAsync(string key, Func<HttpContent> contentFactory, string payloadHash,
                                         ObjectMetadata? metadata, PutCondition? condition, CancellationToken ct)
    {
        await CheckPutConditionLocalAsync(key, condition, ct).ConfigureAwait(false);
        var extraHeaders = new List<(string, string)>();
        AddMetadataHeaders(extraHeaders, metadata);
        AddConditionHeaders(extraHeaders, condition);
        using var response = await SendWithRetryAsync(
            () => BuildRequest(HttpMethod.Put, key, query: null, contentFactory(), extraHeaders, payloadHash),
            idempotent: true, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, key, nameof(PutAsync)).ConfigureAwait(false);
        await DrainBodyAsync(response, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <param name="key">对象键（须满足 ObjectKeyValidator 约束）。</param>
    /// <param name="offset">读取起始偏移（≥0；通过 Range: bytes=offset- 请求）。</param>
    /// <param name="destination">目标缓冲区（长度 0 时立即返回 0，不发请求；其余情形长度决定 Range 末字节）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际读取的字节数；offset ≥ 对象长度时返回 0（416 → EOF 语义归一，不抛）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">key 不合规；或 offset 为负。</exception>
    /// <exception cref="FileIOException">对象不存在（NotFound）；或 S3 返回非 2xx 且非 416（详见 MapError 映射）。</exception>
    public async ValueTask<int> GetAsync(string key, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (destination.Length == 0) return 0;

        var extraHeaders = new List<(string, string)> { ("range", $"bytes={offset}-{offset + destination.Length - 1}") };
        using var response = await SendWithRetryAsync(
            () => BuildRequest(HttpMethod.Get, key, query: null, content: null, extraHeaders, SigV4.EmptyPayloadHash),
            idempotent: true, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return 0;   // 416 → EOF 语义归一（契约：offset ≥ 长度 → 0，不抛）
        await EnsureSuccessAsync(response, key, nameof(GetAsync)).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            // 206 = Range 命中（body 从 offset 起）；200 = 服务器忽略 Range（body 从 0 起——跳过前缀保证正确性）
            if (response.StatusCode == HttpStatusCode.OK && offset > 0)
            {
                var toSkip = offset;
                var skipBuf = new byte[81920];
                while (toSkip > 0)
                {
                    var n = await stream.ReadAsync(skipBuf.AsMemory(0, (int)Math.Min(skipBuf.Length, toSkip)), ct).ConfigureAwait(false);
                    if (n <= 0) return 0;
                    toSkip -= n;
                }
            }
            var filled = 0;
            while (filled < destination.Length)
            {
                var n = await stream.ReadAsync(destination[filled..], ct).ConfigureAwait(false);
                if (n <= 0) break;
                filled += n;
            }
            return filled;
        }
    }

    /// <inheritdoc/>
    /// <param name="key">对象键（须满足 ObjectKeyValidator 约束）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对象元信息（key/size/ETag/元数据/Last-Modified）；对象不存在时返回 null（不抛）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">key 不合规。</exception>
    /// <exception cref="FileIOException">S3 返回非 2xx 且非 404（详见 MapError 映射）。</exception>
    public async ValueTask<ObjectInfo?> HeadAsync(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        using var response = await SendWithRetryAsync(
            () => BuildRequest(HttpMethod.Head, key, query: null, content: null, extraHeaders: null, SigV4.EmptyPayloadHash),
            idempotent: true, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await DrainBodyAsync(response, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        await EnsureSuccessAsync(response, key, nameof(HeadAsync)).ConfigureAwait(false);
        var size = response.Content.Headers.ContentLength ?? -1;
        // Last-Modified 响应头（RFC1123 HTTP-date）——桥接 FsEntry/FsEntryInfo 时间戳
        DateTimeOffset? lastModified = null;
        if (response.Content.Headers.LastModified is { } lm)
            lastModified = new DateTimeOffset(lm.UtcDateTime, TimeSpan.Zero);
        return new ObjectInfo(key, size, StripQuotes(response.Headers.ETag?.Tag), ParseMetadataHeaders(response), lastModified);
    }

    /// <inheritdoc/>
    /// <param name="key">对象键（须满足 ObjectKeyValidator 约束）。</param>
    /// <param name="condition">可选条件 DELETE（If-Match——SupportsConditionalDelete=true 时经本地 Head 校验后无条件删；开关关闭时抛 Unsupported）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">key 不合规。</exception>
    /// <exception cref="FileIOException">NotFound——条件删除时对象不存在；PreconditionFailed——If-Match 不等于当前 ETag（锁已被他人接管）；Unsupported——SupportsConditionalDelete=false 但传了 If-Match。</exception>
    /// <returns>完成时对象已删除（无条件 DELETE 对不存在对象亦为幂等成功——服务端语义）。</returns>
    public async ValueTask DeleteAsync(string key, DeleteCondition? condition = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        if (condition is { IfMatch: { } match } && _options.SupportsConditionalDelete)
        {
            // ★ 厂商差异吸收（§6 决议常态化）：服务端条件 DELETE 强制不一（AWS 2023+ ✓ / MinIO 失配不拦）——
            //   Head 校验 + 无条件删（接受极小竞态，io.md 声明），语义全厂商归一。
            var head = await HeadAsync(key, ct).ConfigureAwait(false);
            if (head is null)
                throw new FileIOException(IOError.NotFound, $"对象不存在（条件删除无从匹配）: {key}", key, nameof(DeleteAsync));
            if (!string.Equals(head.ETag?.Trim('"'), match.Trim('"'), StringComparison.Ordinal))
                throw new FileIOException(IOError.PreconditionFailed,
                    $"条件删除失配（If-Match 不等于当前 ETag——锁已被他人接管，拒绝误删）: {key}", key, nameof(DeleteAsync));
            await DeleteAsync(key, condition: null, ct).ConfigureAwait(false);
            return;
        }
        if (condition is { IfMatch: not null })
            throw new FileIOException(IOError.Unsupported,
                "条件 DELETE 不可用（SupportsConditionalDelete=false）——语义归一开关关闭。", key, nameof(DeleteAsync));
        var response = await SendWithRetryAsync(
            () => BuildRequest(HttpMethod.Delete, key, query: null, content: null, extraHeaders: null, SigV4.EmptyPayloadHash),
            idempotent: true, ct).ConfigureAwait(false);
        using (response)
        {
            await EnsureSuccessAsync(response, key, nameof(DeleteAsync)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>原生 ListObjectsV2 + delimiter（服务端聚合——大前缀省流量；无 delimiter ≡ <see cref="ListAsync"/>）。</remarks>
    /// <param name="prefix">可选键前缀过滤（服务端聚合；null 列全部）。</param>
    /// <param name="delimiter">可选分隔符（按此聚合公共前缀——典型 '/' 模拟目录层级；null 退化为 <see cref="ListAsync"/>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对象列表 + 公共前缀列表（delimiter 聚合的"目录"前缀）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="FileIOException">S3 返回非 2xx（详见 MapError 映射）。</exception>
    public async ValueTask<ObjectListing> ListDelimitedAsync(string? prefix = null, string? delimiter = null,
                                                             CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (delimiter is null)
            return new ObjectListing(await ListAsync(prefix, ct).ConfigureAwait(false), Array.Empty<string>());
        var objects = new List<ObjectEntry>();
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var continuationToken = token;
            using var response = await SendWithRetryAsync(() =>
            {
                var query = new List<(string, string)> { ("list-type", "2") };
                if (prefix is not null) query.Add(("prefix", prefix));
                query.Add(("delimiter", delimiter));
                if (continuationToken is not null) query.Add(("continuation-token", continuationToken));
                return BuildRequest(HttpMethod.Get, key: null, query, content: null, extraHeaders: null, SigV4.EmptyPayloadHash);
            }, idempotent: true, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, null, nameof(ListDelimitedAsync)).ConfigureAwait(false);
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var (entries, commonPrefixes, truncated, next) = S3Xml.ParseListPage(body);
                objects.AddRange(entries);
                foreach (var cp in commonPrefixes) prefixes.Add(cp);
                token = truncated ? next : null;
            }
        } while (token is not null);
        return new ObjectListing(objects, prefixes.ToList());
    }

    /// <inheritdoc/>
    /// <param name="prefix">可选键前缀过滤（服务端聚合；null 列全部）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对象条目列表（已分页聚合——大桶零游标暴露）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="FileIOException">S3 返回非 2xx（详见 MapError 映射）。</exception>
    public async ValueTask<IReadOnlyList<ObjectEntry>> ListAsync(string? prefix = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var result = new List<ObjectEntry>();
        string? token = null;
        do
        {
            var continuationToken = token;
            using var response = await SendWithRetryAsync(() =>
            {
                var query = new List<(string, string)> { ("list-type", "2") };
                if (prefix is not null) query.Add(("prefix", prefix));
                if (continuationToken is not null) query.Add(("continuation-token", continuationToken));
                return BuildRequest(HttpMethod.Get, key: null, query, content: null, extraHeaders: null, SigV4.EmptyPayloadHash);
            }, idempotent: true, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, null, nameof(ListAsync)).ConfigureAwait(false);
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var (entries, _, truncated, next) = S3Xml.ParseListPage(body);
                result.AddRange(entries);
                token = truncated ? next : null;
            }
        } while (token is not null);
        return result;
    }

    /// <inheritdoc/>
    /// <param name="sourceKey">源对象键（须满足 ObjectKeyValidator 约束；须存在——否则抛 NotFound）。</param>
    /// <param name="destKey">目标对象键（须满足 ObjectKeyValidator 约束）。</param>
    /// <param name="metadata">可选元数据指令（null=服务端 COPY 保留源元数据；非 null=REPLACE 写入新元数据）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">sourceKey 或 destKey 不合规。</exception>
    /// <exception cref="FileIOException">源对象不存在（NotFound）；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
    /// <returns>完成时对象已服务端复制（源对象保持不变）。</returns>
    public async ValueTask CopyAsync(string sourceKey, string destKey, CopyMetadata? metadata = null,
                                     CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(sourceKey);
        ObjectKeyValidator.Validate(destKey);
        await CopyObjectCoreAsync(sourceKey, destKey, metadata, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <param name="sourceKey">源对象键（须满足 ObjectKeyValidator 约束；须存在——否则抛 NotFound）。</param>
    /// <param name="replace">可选替换元数据（非 null 时自拷自写 REPLACE 元数据并返回该值；null 时只读 Head 返回当前元数据）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前对象元数据（replace 非 null 时为 replace 值；replace null 时为源对象元数据快照）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">sourceKey 不合规。</exception>
    /// <exception cref="FileIOException">源对象不存在（NotFound）；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
    public async ValueTask<ObjectMetadata> CopyMetadataAsync(string sourceKey, ObjectMetadata? replace = null,
                                                             CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(sourceKey);
        if (replace is not null)
        {
            await CopyObjectCoreAsync(sourceKey, sourceKey, new CopyMetadata(replace), ct).ConfigureAwait(false);
            return replace;
        }
        var info = await HeadAsync(sourceKey, ct).ConfigureAwait(false)
            ?? throw new FileIOException(IOError.NotFound, $"对象不存在: {sourceKey}", sourceKey, nameof(CopyMetadataAsync));
        return info.Metadata;
    }

    // ═════════════════════════════ multipart / 范围拷贝 ═════════════════════════════

    /// <inheritdoc/>
    /// <param name="key">对象键（须满足 ObjectKeyValidator 约束——上传完成后的最终对象路径）。</param>
    /// <param name="metadata">可选对象元数据（写入 x-amz-meta-* 头；null 不写）。</param>
    /// <returns>multipart 上传会话句柄（懒初始化——首次 UploadPart/Complete 才真正建会话）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">key 不合规。</exception>
    public IMultipartUpload CreateMultipartUpload(string key, ObjectMetadata? metadata = null)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        return new S3MultipartUpload(this, key, metadata);
    }

    /// <inheritdoc/>
    /// <remarks>通用实现 = multipart 编排（UploadPartCopy 循环 + complete）——单 part ≤5GB 约束在切分内吸收；
    ///   源不存在 → NotFound；源尾截断 → 返回实际可拷贝长度（契约）。</remarks>
    /// <param name="sourceKey">源对象键（须存在——否则抛 NotFound）。</param>
    /// <param name="destKey">目标对象键（创建新对象——不可与 sourceKey 相同语义）。</param>
    /// <param name="sourceOffset">源起始偏移（≥0；超过源尾时按 0 长度处理，创建空对象）。</param>
    /// <param name="length">期望拷贝长度（≥0；超出源尾时按实际可拷贝长度截断）。</param>
    /// <param name="metadata">可选元数据指令（null=COPY 保留；非 null=REPLACE 替换）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际拷贝字节数（源尾截断时小于 length；源完全在范围内时等于 length）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">sourceKey/destKey 不合规；或 sourceOffset/length 为负。</exception>
    /// <exception cref="FileIOException">源对象不存在（NotFound）；或上传过程中 S3 返回非 2xx（失败时已自动 Abort 会话）。</exception>
    public async ValueTask<long> CopyRangeAsync(string sourceKey, string destKey, long sourceOffset, long length,
                                                CopyMetadata? metadata = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(sourceKey);
        ObjectKeyValidator.Validate(destKey);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var source = await HeadAsync(sourceKey, ct).ConfigureAwait(false)
            ?? throw new FileIOException(IOError.NotFound, $"源对象不存在: {sourceKey}", sourceKey, nameof(CopyRangeAsync));
        var actual = Math.Min(length, Math.Max(0, source.Size - sourceOffset));

        var session = CreateMultipartUpload(destKey, metadata?.Metadata);
        try
        {
            const long maxPart = 5L * 1024 * 1024 * 1024 - 1;   // 单 part ≤5GB（S3 上限）
            var partNumber = 1;
            var remaining = actual;
            var offset = sourceOffset;
            var parts = new List<UploadPartResult>();
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, maxPart);
                parts.Add(await session.UploadPartCopyAsync(partNumber++, sourceKey, offset, chunk, ct).ConfigureAwait(false));
                offset += chunk;
                remaining -= chunk;
            }
            if (parts.Count == 0)
                parts.Add(await session.UploadPartAsync(1, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false));   // 空范围 → 空对象
            await session.CompleteAsync(parts, ct).ConfigureAwait(false);
            return actual;
        }
        catch
        {
            await TryAbortNoThrow(session).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>key-marker/upload-id-marker 分页循环归一（桥侧按 KeyPrefix 过滤）。</remarks>
    /// <param name="ct">取消令牌。</param>
    /// <returns>所有进行中的 multipart 会话列表（已分页聚合——无前缀过滤，全桶扫描）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="FileIOException">S3 返回非 2xx（详见 MapError 映射）。</exception>
    public async ValueTask<IReadOnlyList<MultipartUploadSession>> ListMultipartUploadsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var result = new List<MultipartUploadSession>();
        string? keyMarker = null, uploadIdMarker = null;
        do
        {
            var km = keyMarker;
            var um = uploadIdMarker;
            using var response = await SendWithRetryAsync(() =>
            {
                var query = new List<(string, string)> { ("uploads", string.Empty) };
                if (km is not null) query.Add(("key-marker", km));
                if (um is not null) query.Add(("upload-id-marker", um));
                return BuildRequest(HttpMethod.Get, key: null, query, content: null, extraHeaders: null, SigV4.EmptyPayloadHash);
            }, idempotent: true, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, null, nameof(ListMultipartUploadsAsync)).ConfigureAwait(false);
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var (sessions, truncated, nextKey, nextUpload) = S3Xml.ParseMultipartUploadsPage(body);
                result.AddRange(sessions);
                keyMarker = truncated ? nextKey : null;
                uploadIdMarker = truncated ? nextUpload : null;
            }
        } while (keyMarker is not null);
        return result;
    }

    /// <inheritdoc/>
    /// <param name="key">对象键（须满足 ObjectKeyValidator 约束——会话对应的目标对象路径）。</param>
    /// <param name="uploadId">会话 UploadId（由 CreateMultipartUpload 或 ListMultipartUploadsAsync 取得）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="ArgumentException">key 不合规。</exception>
    /// <exception cref="FileIOException">S3 返回非 2xx 且非 404（NoSuchUpload 视为幂等成功，不抛）。</exception>
    /// <returns>完成时会话已中止（NoSuchUpload = 已完成/已中止——幂等成功）。</returns>
    public ValueTask AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        return new ValueTask(AbortUploadByIdAsync(this, key, uploadId, ct));
    }

    /// <summary>流式枚举覆写——分页在实现内推进（大桶零整量驻留）。</summary>
    /// <param name="prefix">可选键前缀过滤（服务端聚合；null 列全部）。</param>
    /// <param name="ct">取消令牌（[EnumeratorCancellation] 标注使流式枚举可取消）。</param>
    /// <returns>异步可枚举对象条目序列（按 ListObjectsV2 分页逐项 yield——单次仅驻一页）。</returns>
    /// <exception cref="ObjectDisposedException">实例已 Dispose。</exception>
    /// <exception cref="FileIOException">S3 返回非 2xx（详见 MapError 映射）。</exception>
    public async IAsyncEnumerable<ObjectEntry> ListStreamingAsync(string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? token = null;
        while (true)
        {
            var continuationToken = token;
            using var response = await SendWithRetryAsync(() =>
            {
                var query = new List<(string, string)> { ("list-type", "2") };
                if (prefix is not null) query.Add(("prefix", prefix));
                if (continuationToken is not null) query.Add(("continuation-token", continuationToken));
                return BuildRequest(HttpMethod.Get, key: null, query, content: null, extraHeaders: null, SigV4.EmptyPayloadHash);
            }, idempotent: true, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, null, nameof(ListStreamingAsync)).ConfigureAwait(false);
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            string? next = null;
            await using (body.ConfigureAwait(false))
            {
                var (entries, _, truncated, nextToken) = S3Xml.ParseListPage(body);
                foreach (var e in entries)
                    yield return e;
                next = truncated ? nextToken : null;
            }
            if (next is null) yield break;
            token = next;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsHttp) _http.Dispose();
    }

    // ═════════════════════════════ 请求构造与签名 ═════════════════════════════

    /// <summary>
    /// 构造已签名请求（同步——凭证经同步路径取用；异步凭证源消费者自行预取后用 StaticCredentials 包装）。
    /// canonical URI 与 URL 路径同源（同编码器）——签名与实发恒一致。
    /// </summary>
    /// <param name="method">HTTP 方法（GET/HEAD/PUT/DELETE/POST）。</param>
    /// <param name="key">对象键（null = 桶级操作如 list；非 null 经 UriEncode 拼入路径段）。</param>
    /// <param name="query">查询参数列表（null 或空 = 无查询串；键值各自 UriEncode 后按 Ordinal 排序）。</param>
    /// <param name="content">请求体（null = 无 body；PUT 单 part / POST complete 时为 ByteArrayContent）。</param>
    /// <param name="extraHeaders">额外请求头（已小写——x-amz-* 元数据/条件/复制源等；null 不附加）。</param>
    /// <param name="payloadHash">载荷 SHA-256 hex（无 body=SigV4.EmptyPayloadHash；流式=SigV4.StreamingContentSha256）。</param>
    /// <returns>已注入 Authorization/x-amz-date/x-amz-content-sha256 头的 HttpRequestMessage。</returns>
    internal HttpRequestMessage BuildRequest(HttpMethod method, string? key,
        IReadOnlyList<(string Name, string Value)>? query, HttpContent? content,
        IReadOnlyList<(string Name, string Value)>? extraHeaders, string payloadHash)
        => BuildRequestCore(method, key, query, content, extraHeaders, payloadHash).Request;

    /// <summary>签名上下文（chunked 流式链的种子——签名链由此演进）。</summary>
    /// <param name="SigningKey">派生签名密钥（HMAC 链根——kSecret→kDate→kRegion→kService→kSigning）。</param>
    /// <param name="Signature">首块签名（chunked 链的 seed——ChunkedSignedStream 据此推演后续块签名）。</param>
    /// <param name="AmzDate">ISO 8601 简化日期时间（yyyyMMdd'T'HHmmss'Z'——写入 x-amz-date 头）。</param>
    /// <param name="Scope">签名作用域（{date}/{region}/{service}/aws4_request——Authorization Credential 段）。</param>
    internal sealed record SignedContext(byte[] SigningKey, string Signature, string AmzDate, string Scope);

    internal (HttpRequestMessage Request, SignedContext Ctx) BuildRequestWithContext(HttpMethod method, string? key,
        IReadOnlyList<(string Name, string Value)>? query, HttpContent? content,
        IReadOnlyList<(string Name, string Value)>? extraHeaders, string payloadHash)
        => BuildRequestCore(method, key, query, content, extraHeaders, payloadHash);

    private (HttpRequestMessage Request, SignedContext Ctx) BuildRequestCore(HttpMethod method, string? key,
        IReadOnlyList<(string Name, string Value)>? query, HttpContent? content,
        IReadOnlyList<(string Name, string Value)>? extraHeaders, string payloadHash)
    {
        // 寻址模式：path-style = /{bucket}/{key}；virtual-host = /{key}（bucket 在 Host——COS/R2 要求）
        var path = _options.UseVirtualHostAddressing ? string.Empty : "/" + _options.Bucket;
        if (key is not null)
            path += "/" + SigV4.UriEncode(key, encodeSlash: false);
        else if (path.Length == 0)
            path = "/";   // virtual-host 桶级操作（list/uploads）——canonical URI 根

#pragma warning disable TCSG137 // 设计必需：同步签名构建路径取凭证（BuildRequestCore 同步契约）
        var credential = _options.Credentials.GetCredentialsAsync(CancellationToken.None).AsTask()
            .GetAwaiter().GetResult();
#pragma warning restore TCSG137
        var now = DateTimeOffset.UtcNow;
        var amzDate = SigV4.AmzDate(now);
        var scopeDate = SigV4.ScopeDate(now);
        var scope = $"{scopeDate}/{_options.Region}/{Service}/aws4_request";

        // 签名头集合（确定性——全部显式声明，不依赖 HttpClient 隐式注入的头）
        var headers = new List<(string Name, string Value)>
        {
            ("host", _options.HostHeader),
            ("x-amz-content-sha256", payloadHash),
            ("x-amz-date", amzDate),
        };
        if (credential.SessionToken is { } sessionToken)
            headers.Add(("x-amz-security-token", sessionToken));
        if (content is not null && content.Headers.TryGetValues("Content-Type", out var ctValues))
            headers.Add(("content-type", string.Join(",", ctValues)));
        if (extraHeaders is not null)
            headers.AddRange(extraHeaders);   // 已小写（调用方约定）
        headers.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        var canonicalQuery = query is null || query.Count == 0
            ? string.Empty
            : SigV4.CanonicalQueryString(query);
        var canonicalRequest = SigV4.BuildCanonicalRequest(method.Method, path, canonicalQuery, headers, payloadHash);
        var stringToSign = SigV4.BuildStringToSign(amzDate, scope,
            SigV4.Sha256Hex(Encoding.ASCII.GetBytes(canonicalRequest)));
#if DEBUG
        SigV4.RecordDiagnostics(canonicalRequest, stringToSign);
#endif
        var signingKey = SigV4.DeriveSigningKey(credential.SecretAccessKey, scopeDate, _options.Region, Service);
        var signature = SigV4.ComputeSignature(signingKey, stringToSign);
        var signedHeaders = string.Join(";", headers.Select(static h => h.Name));
        var authorization =
            $"{SigV4.Algorithm} Credential={credential.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";

        // URL 主机：virtual-host = bucket.endpoint-host（HostHeader 已含）；path-style = endpoint
        var urlPrefix = _options.UseVirtualHostAddressing
            ? _options.SchemePrefix + "://" + _options.HostHeader
            : _options.UrlPrefix;
        var url = urlPrefix + path + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : string.Empty);
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (credential.SessionToken is { } st)
            request.Headers.TryAddWithoutValidation("x-amz-security-token", st);
        if (extraHeaders is not null)
            foreach (var (name, value) in extraHeaders)
                request.Headers.TryAddWithoutValidation(name, value);
        return (request, new SignedContext(signingKey, signature, amzDate, scope));
    }

    // ═════════════════════════════ 发送/重试/错误映射 ═════════════════════════════

    /// <summary>
    /// 发送 + 重试——★ 工厂式（HttpContent 发送后不可复用，重试须整请求重建；签名参数确定性 → 重建即同签名）。
    /// 重试条件：幂等操作 ×（5xx/429/网络抖动/超时）× 次数未满；指数退避 + 抖动。
    /// </summary>
    /// <param name="requestFactory">请求构造工厂（每次重试调用——须重建 HttpContent；幂等性由调用方按操作语义给出）。</param>
    /// <param name="idempotent">是否幂等（true=对 5xx/429/网络抖动指数退避重试；false=任何错误直通——CreateMultipartUpload 等不可重试操作）。</param>
    /// <param name="ct">取消令牌（与内部超时 cts 链接——任一触发即取消）。</param>
    /// <returns>最终响应（已 dispose 重试中间响应——成功或耗尽重试后的最终响应）。</returns>
    /// <exception cref="OperationCanceledException">ct 取消（调用方语义，直通抛出）。</exception>
    /// <exception cref="FileIOException">非幂等操作遇网络抖动；或幂等操作重试耗尽后仍网络失败（经 WrapNetwork 包装）。</exception>
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory,
                                                               bool idempotent, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.Timeout);
            try
            {
                using var request = requestFactory();
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                if (IsRetryableStatus(response.StatusCode) && idempotent && attempt < _options.MaxRetries)
                {
                    response.Dispose();
                    await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                return response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 调用方取消——直通
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // 网络抖动/超时（内部 cts 触发的 OCE 落这里）
                if (!idempotent || attempt >= _options.MaxRetries)
                    throw WrapNetwork(ex);
                await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    /// <param name="status">HTTP 状态码。</param>
    /// <returns>true = 5xx/429（可重试：InternalServerError/BadGateway/ServiceUnavailable/GatewayTimeout/TooManyRequests）；其余 false。</returns>
    private static bool IsRetryableStatus(HttpStatusCode status)
        => status is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.TooManyRequests;

    /// <param name="attempt">已失败次数（0=首次重试前——基期 ×2^attempt × [0.8,1.2) 抖动）。</param>
    /// <param name="ct">取消令牌（Delay 期间可取消）。</param>
    private async Task DelayBackoffAsync(int attempt, CancellationToken ct)
    {
        var delay = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt) * (0.8 + Random.Shared.NextDouble() * 0.4);
        await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
    }

    /// <param name="ex">原始网络异常（HttpRequestException/IOException/OperationCanceledException）。</param>
    /// <returns>归一为 <see cref="IOError.IOFailure"/> 的 FileIOException（保留原异常为 InnerException——调用方按 FileIOException 统一处理）。</returns>
    private static FileIOException WrapNetwork(Exception ex) => new(
        IOError.IOFailure, $"S3 网络故障: {ex.Message}", null, "network", ex);

    /// <param name="response">HTTP 响应消息。</param>
    /// <param name="key">对象键（写入异常 message 上下文；桶级操作可传 null）。</param>
    /// <param name="operation">调用方操作名（写入异常 message 上下文——便于溯源）。</param>
    /// <exception cref="FileIOException">响应非 2xx——按 MapError 映射到对应 IOError（NotFound/AccessDenied/PreconditionFailed 等）。</exception>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string? key, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var (code, message) = await ReadErrorAsync(response).ConfigureAwait(false);
        throw MapError(response.StatusCode, code, message, key, operation);
    }

    private static async Task<(string Code, string Message)> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
                return S3Xml.ParseError(stream);
        }
        catch
        {
            return ("Unknown", $"HTTP {(int)response.StatusCode}");
        }
    }

    /// <summary>状态码 + S3 错误码 → <see cref="FileIOException"/> 归一映射。</summary>
    /// <param name="status">HTTP 状态码（优先映射维度）。</param>
    /// <param name="code">S3 Error 响应体 Code 字段（status 未命中时按此二次映射；"Unknown"=无 XML 错误体）。</param>
    /// <param name="message">S3 Error 响应体 Message 字段（写入异常 message）。</param>
    /// <param name="key">对象键（写入异常上下文；可 null）。</param>
    /// <param name="operation">调用方操作名（写入异常上下文——便于溯源）。</param>
    /// <returns>归一后的 FileIOException（NotFound/AccessDenied/PreconditionFailed/DiskFull/Unsupported/Unknown 之一）。</returns>
    private static FileIOException MapError(HttpStatusCode status, string code, string message, string? key, string operation)
    {
        var error = status switch
        {
            HttpStatusCode.NotFound => IOError.NotFound,               // NoSuchKey / NoSuchUpload
            HttpStatusCode.Forbidden => IOError.AccessDenied,
            HttpStatusCode.PreconditionFailed => IOError.PreconditionFailed,
            HttpStatusCode.InsufficientStorage => IOError.DiskFull,
            HttpStatusCode.NotImplemented => IOError.Unsupported,
            _ => code switch
            {
                "NoSuchKey" or "NoSuchUpload" or "NotFound" => IOError.NotFound,
                "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" => IOError.AccessDenied,
                "PreconditionFailed" or "ConditionalRequestConflict" => IOError.PreconditionFailed,
                _ => IOError.Unknown,
            },
        };
        return new FileIOException(error, $"S3 {status} [{code}]: {message}", key, operation);
    }

    /// <param name="response">HTTP 响应消息（其正文需被排空以释放回连接池——典型用于 GET/HEAD 后未消费完的响应）。</param>
    /// <param name="ct">取消令牌。</param>
    private static async Task DrainBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
                await stream.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
        }
        catch
        {
            // 排水失败仅影响连接复用——吞掉
        }
    }

    /// <param name="session">multipart 会话句柄（可能未初始化——Abort 内部判空）。</param>
    private static async Task TryAbortNoThrow(IMultipartUpload session)
    {
        try { await session.AbortAsync().ConfigureAwait(false); }
        catch { /* 碎片回收失败不掩盖主异常 */ }
    }

    // ═════════════════════════════ 元数据/条件/ETag 头映射 ═════════════════════════════

    /// <param name="headers">请求头累加列表（被原地追加 x-amz-meta-* 项）。</param>
    /// <param name="metadata">对象元数据（null 不追加；UserMetadata 各项加 MetadataHeaderPrefix 前缀）。</param>
    private static void AddMetadataHeaders(List<(string, string)> headers, ObjectMetadata? metadata)
    {
        if (metadata is null) return;
        foreach (var (k, v) in metadata.UserMetadata)
            headers.Add((MetadataHeaderPrefix + k, v));   // 键已校验（[A-Za-z0-9_.-]）——HTTP token 安全
    }

    /// <param name="headers">请求头累加列表（被原地追加 if-match / if-none-match 项）。</param>
    /// <param name="condition">PUT 条件（null 不追加；IfMatch→if-match；IfNoneMatch→if-none-match，"*" 原样直通）。</param>
    private static void AddConditionHeaders(List<(string, string)> headers, PutCondition? condition)
    {
        if (condition is not { } c) return;
        if (c.IfMatch is { } ifMatch)
            headers.Add(("if-match", QuoteETag(ifMatch)));
        if (c.IfNoneMatch is { } ifNoneMatch)
            headers.Add(("if-none-match", ifNoneMatch == "*" ? "*" : QuoteETag(ifNoneMatch)));
    }

    /// <param name="response">HTTP 响应消息（从其 Headers 集合过滤 x-amz-meta-* 项）。</param>
    /// <returns>解析出的对象元数据（无 x-amz-meta-* 头时返回 <see cref="ObjectMetadata.Empty"/>；否则键已去前缀归一）。</returns>
    private static ObjectMetadata ParseMetadataHeaders(HttpResponseMessage response)
    {
        var dict = new Dictionary<string, string>();
        foreach (var h in response.Headers)
        {
            if (!h.Key.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = h.Key[MetadataHeaderPrefix.Length..];
            dict[name] = string.Join(",", h.Value);
        }
        return dict.Count == 0 ? ObjectMetadata.Empty : ObjectMetadata.Create(dict);
    }

    /// <param name="etag">原始 ETag 字符串（可能带首尾引号；null 直通）。</param>
    /// <returns>已去除首尾引号的 ETag；<paramref name="etag"/> 为 null 时返回 null。</returns>
    private static string? StripQuotes(string? etag) => etag?.Trim('"');

    /// <param name="etag">原始 ETag 字符串（非 null；可能已带引号也可能未带）。</param>
    /// <returns>已带首尾双引号的 ETag（已带引号时原样直通，避免双重引号）。</returns>
    private static string QuoteETag(string etag)
        => etag.Length > 1 && etag.StartsWith('"') && etag.EndsWith('"') ? etag : $"\"{etag}\"";

    // ═════════════════════════════ CopyObject 核心 ═════════════════════════════

    /// <param name="sourceKey">源对象键（拼入 x-amz-copy-source——须存在）。</param>
    /// <param name="destKey">目标对象键（PUT 请求的目标——覆盖既有同名对象）。</param>
    /// <param name="metadata">可选元数据指令（null=COPY 保留源元数据；非 null=REPLACE 写入新元数据）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="FileIOException">源对象不存在（NotFound）；或 S3 返回非 2xx（详见 MapError 映射）；或响应缺 ETag（IOFailure）。</exception>
    private async ValueTask CopyObjectCoreAsync(string sourceKey, string destKey, CopyMetadata? metadata,
                                                 CancellationToken ct)
    {
        var extraHeaders = new List<(string, string)>
        {
            ("x-amz-copy-source", "/" + _options.Bucket + "/" + SigV4.UriEncode(sourceKey, encodeSlash: false)),
            ("x-amz-metadata-directive", metadata is null ? "COPY" : "REPLACE"),
        };
        AddMetadataHeaders(extraHeaders, metadata?.Metadata);
        using var response = await SendWithRetryAsync(
            () => BuildRequest(HttpMethod.Put, destKey, query: null, content: null, extraHeaders, SigV4.EmptyPayloadHash),
            idempotent: true, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, destKey, "CopyObject").ConfigureAwait(false);
        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            _ = S3Xml.ParseCopyEtag(body);   // 解析失败抛 IOFailure（响应形态异常）
        }
    }

    /// <summary>按 uploadId 放弃会话（幂等：NoSuchUpload = 已终结）——session.Abort 与治理原语共享。</summary>
    /// <param name="owner">S3ObjectStore 实例（用于发送请求）。</param>
    /// <param name="key">会话对应对象键。</param>
    /// <param name="uploadId">会话 UploadId。</param>
    /// <param name="ct">取消令牌。</param>
    private static async Task AbortUploadByIdAsync(S3ObjectStore owner, string key, string uploadId, CancellationToken ct)
    {
        var query = new List<(string, string)> { ("uploadId", uploadId) };
        try
        {
            using var response = await owner.SendWithRetryAsync(
                () => owner.BuildRequest(HttpMethod.Delete, key, query, content: null, extraHeaders: null, SigV4.EmptyPayloadHash),
                idempotent: true, ct).ConfigureAwait(false);
            // NoSuchUpload = 已 complete/abort——幂等成功
            if (response.StatusCode != HttpStatusCode.NotFound)
                await EnsureSuccessAsync(response, key, "AbortMultipartUpload").ConfigureAwait(false);
        }
        catch (FileIOException ex) when (ex.Error == IOError.NotFound)
        {
            // 会话已不在服务端——幂等
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    // ═════════════════════════════ multipart 会话实现 ═════════════════════════════

    /// <param name="owner">所属 S3ObjectStore 实例（用于发送 BuildRequest 与 SendWithRetryAsync）。</param>
    /// <param name="key">目标对象键（multipart 上传完成后的最终对象路径）。</param>
    /// <param name="metadata">可选对象元数据（创建会话时写入 x-amz-meta-* 头；null 不写）。</param>
    private sealed class S3MultipartUpload(S3ObjectStore owner, string key, ObjectMetadata? metadata) : IMultipartUpload
    {
        private string? _uploadId;
        private readonly SemaphoreSlim _initGate = new(1, 1);   // 懒初始化闸门（并发首传防双开会话）

        /// <param name="ct">取消令牌。</param>
        /// <returns>当前会话的 UploadId（已存在则直返；不存在则 POST ?uploads 创建后缓存返回）。</returns>
        /// <exception cref="FileIOException">CreateMultipartUpload 响应缺 UploadId（IOFailure）；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
        private async ValueTask<string> EnsureUploadIdAsync(CancellationToken ct)
        {
            if (_uploadId is { } id) return id;
            await _initGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_uploadId is { } winner) return winner;   // 双检——并发赢家已建
                var query = new List<(string, string)> { ("uploads", string.Empty) };
                var extraHeaders = new List<(string, string)>();
                if (metadata is not null)
                    foreach (var (k, v) in metadata.UserMetadata)
                        extraHeaders.Add((MetadataHeaderPrefix + k, v));
                // ★ 不重试（idempotent:false）：POST ?uploads 响应丢失时重发会双开会话（碎片回收兜底靠 Abort）
                using var response = await owner.SendWithRetryAsync(
                    () => owner.BuildRequest(HttpMethod.Post, key, query, content: null, extraHeaders, SigV4.EmptyPayloadHash),
                    idempotent: false, ct).ConfigureAwait(false);
                await EnsureSuccessAsync(response, key, "CreateMultipartUpload").ConfigureAwait(false);
                var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    _uploadId = S3Xml.ParseUploadId(body);
                }
                return _uploadId;
            }
            finally
            {
                _initGate.Release();
            }
        }

        /// <summary>上传内存分片（懒初始化会话——首次调用才真正 CreateMultipartUpload）。</summary>
        /// <param name="partNumber">分片序号（≥1；S3 接受 1-10000——由调用方在切分时分配）。</param>
        /// <param name="data">分片字节数据（全量驻内存——单 part ≤5GB 实际受内存约束）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>分片结果（partNumber + ETag——Complete 时按序组装）。</returns>
        /// <exception cref="ArgumentException">partNumber ≤0。</exception>
        /// <exception cref="FileIOException">UploadPart 响应缺 ETag（IOFailure）；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
        public async ValueTask<UploadPartResult> UploadPartAsync(int partNumber, ReadOnlyMemory<byte> data,
                                                                 CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partNumber);
            var uploadId = await EnsureUploadIdAsync(ct).ConfigureAwait(false);
            using var response = await owner.SendWithRetryAsync(() =>
            {
                var query = new List<(string, string)>
                {
                    ("partNumber", partNumber.ToString(CultureInfo.InvariantCulture)),
                    ("uploadId", uploadId),
                };
                var content = new ByteArrayContent(data.ToArray());
                content.Headers.ContentLength = data.Length;
                return owner.BuildRequest(HttpMethod.Put, key, query, content, extraHeaders: null,
                    data.Length == 0 ? SigV4.EmptyPayloadHash : SigV4.Sha256Hex(data.Span));
            }, idempotent: true, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, key, "UploadPart").ConfigureAwait(false);
            var etag = StripQuotes(response.Headers.ETag?.Tag)
                ?? throw new FileIOException(IOError.IOFailure, "UploadPart 响应缺 ETag。", key, "UploadPart");
            return new UploadPartResult(partNumber, etag);
        }

        /// <summary>服务端拷贝上传分片（源对象范围 → 本会话分片——大对象免下载中转）。</summary>
        /// <param name="partNumber">分片序号（≥1）。</param>
        /// <param name="sourceKey">源对象键（须存在——拼入 x-amz-copy-source）。</param>
        /// <param name="sourceOffset">源起始偏移（拼入 x-amz-copy-source-range 起始字节）。</param>
        /// <param name="length">本分片期望拷贝字节数（拼入 x-amz-copy-source-range 长度）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>分片结果（partNumber + ETag——服务端按拷贝结果回填）。</returns>
        /// <exception cref="ArgumentException">partNumber ≤0；或 sourceKey 不合规。</exception>
        /// <exception cref="FileIOException">Copy 响应缺 ETag（IOFailure）；或 S3 返回非 2xx（详见 MapError 映射）。</exception>
        public async ValueTask<UploadPartResult> UploadPartCopyAsync(int partNumber, string sourceKey,
                                                                     long sourceOffset, long length,
                                                                     CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partNumber);
            ObjectKeyValidator.Validate(sourceKey);
            var uploadId = await EnsureUploadIdAsync(ct).ConfigureAwait(false);
            var query = new List<(string, string)>
            {
                ("partNumber", partNumber.ToString(CultureInfo.InvariantCulture)),
                ("uploadId", uploadId),
            };
            var extraHeaders = new List<(string, string)>
            {
                ("x-amz-copy-source", "/" + owner._options.Bucket + "/" + SigV4.UriEncode(sourceKey, encodeSlash: false)),
                ("x-amz-copy-source-range", $"bytes={sourceOffset}-{sourceOffset + length - 1}"),
            };
            using var response = await owner.SendWithRetryAsync(
                () => owner.BuildRequest(HttpMethod.Put, key, query, content: null, extraHeaders, SigV4.EmptyPayloadHash),
                idempotent: true, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, key, "UploadPartCopy").ConfigureAwait(false);
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                return new UploadPartResult(partNumber, S3Xml.ParseCopyEtag(body));
            }
        }

        /// <summary>完成 multipart 上传（按序组装分片——会话终结，对象可见）。</summary>
        /// <param name="parts">所有分片结果列表（须至少 1 项；按 partNumber 升序组装 complete 请求体）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>完成时 multipart 会话已终结、对象已组装可见（200 + Error body 延迟失败按错误映射抛）。</returns>
        /// <exception cref="ArgumentNullException">parts 为 null。</exception>
        /// <exception cref="ArgumentException">parts 为空列表。</exception>
        /// <exception cref="FileIOException">S3 返回非 2xx；或 200 + Error body（延迟失败——按 TryReadErrorBody 解析后映射）。</exception>
        public async ValueTask CompleteAsync(IReadOnlyList<UploadPartResult> parts, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(parts);
            if (parts.Count == 0) throw new ArgumentException("Complete 至少一个 part。", nameof(parts));
            var uploadId = await EnsureUploadIdAsync(ct).ConfigureAwait(false);
            var bodyBytes = S3Xml.BuildCompleteMultipart(parts);
            var payloadHash = SigV4.Sha256Hex(bodyBytes);
            using var response = await owner.SendWithRetryAsync(() =>
            {
                var query = new List<(string, string)> { ("uploadId", uploadId) };
                var content = new ByteArrayContent(bodyBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
                return owner.BuildRequest(HttpMethod.Post, key, query, content, extraHeaders: null, payloadHash);
            }, idempotent: true, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, key, "CompleteMultipartUpload").ConfigureAwait(false);
            // ★ S3 特性：200 + Error body（延迟失败）——按错误映射抛出
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                if (S3Xml.TryReadErrorBody(body, out var err))
                    throw MapError(response.StatusCode, err.Code, err.Message, key, "CompleteMultipartUpload");
            }
        }

        /// <summary>中止 multipart 会话（幂等——未初始化会话为 no-op；已完成会话由服务端 NoSuchUpload 兜底）。</summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>会话已存在时返回 Abort 请求的 ValueTask；未初始化会话立即返回 CompletedTask（无服务端调用）。</returns>
        public ValueTask AbortAsync(CancellationToken ct = default)
            => _uploadId is { } uploadId
                ? new ValueTask(AbortUploadByIdAsync(owner, key, uploadId, ct))
                : ValueTask.CompletedTask;

        /// <summary>释放会话（尽力 Abort——未完成会话的已上传分片由服务端按生命周期策略清理）。</summary>
        /// <returns>完成时 Abort 已尽力执行（异常吞——Dispose 语义）。</returns>
        public async ValueTask DisposeAsync()
        {
            try { await AbortAsync().ConfigureAwait(false); }
            catch { /* 吞——Dispose 语义 */ }
        }
    }
}
