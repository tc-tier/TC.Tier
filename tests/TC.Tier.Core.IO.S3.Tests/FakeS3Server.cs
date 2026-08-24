using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TC.Tier.Core.IO.S3.Tests;

/// <summary>
/// 进程内假 S3 服务器（测试基础设施）——走真实 HTTP + SigV4 + XML 全路径的离线验证。
/// <para>★ 签名校验：服务端重算 canonical request + 签名，不匹配 → 403 SignatureDoesNotMatch
///   （结构性验证：编码/头/查询/哈希的任一不一致都会被抓——加密核心由黄金向量独立保证，
///   协议互操作由 MinIO 终验三层分工）。</para>
/// <para>★ 实现面：对象 CRUD / 条件写 / Range GET（416 语义）/ ListV2 分页（max-keys 可配——分页归一验证）/
///   multipart 全族（uploads/partNumber/uploadId）/ CopyObject / 故障注入（第 N 次请求 503——重试验证）。</para>
/// </summary>
public sealed class FakeS3Server : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _sync = new();

    private readonly Dictionary<string, Obj> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UploadState> _sessions = new(StringComparer.Ordinal);

    private const string CRLF = "\r\n";

    private sealed record UploadState(string Key, DateTimeOffset InitiatedUtc)
    {
        public readonly Dictionary<int, byte[]> Parts = new();
    }

    /// <summary>list 分页宽度（默认 3——强制多页归一）。</summary>
    public int MaxKeys { get; set; } = 3;

    /// <summary>故障注入：第 N 次（1 基）请求回 503（重试矩阵验证）；0 = 不注入。</summary>
    public int FailNthRequest { get; set; }

    /// <summary>累计请求数（重试断言）。</summary>
    public int RequestCount;

    /// <summary>签名校验失败次数（意外 403 的观测点——测试断言恒 0）。</summary>
    public int SignatureFailures;

    /// <summary>最近一次请求的原始路径（编码形态断言）。</summary>
    public string? LastRawPath;

    /// <summary>接受的请求清单（形态断言）。</summary>
    public ConcurrentBag<(string Method, string Path, string Query)> Requests { get; } = [];

    private sealed record Obj(byte[] Data, Dictionary<string, string> Meta, DateTimeOffset CreatedAtUtc);

    public string Endpoint { get; }

    public FakeS3Server()
    {
        var port = GetFreePort();
        Endpoint = $"http://localhost:{port}";
        _listener.Prefixes.Add($"{Endpoint}/");
        _listener.Start();
        _loop = Task.Run(() => ListenAsync(_cts.Token));
    }

    private static int GetFreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;   // Stop() 后
            }
            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var n = Interlocked.Increment(ref RequestCount);
            if (FailNthRequest > 0 && n == FailNthRequest)
            {
                await WriteErrorAsync(ctx, 503, "ServiceUnavailable", "injected").ConfigureAwait(false);
                return;
            }
            // body 先读全（签名哈希校验 + 路由复用；chunked 流式签名时解码为真实体）
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            if (ctx.Request.Headers["x-amz-content-sha256"] == "STREAMING-AWS4-HMAC-SHA256-PAYLOAD")
            {
                if (!TryDecodeChunked(ctx, body, out var decoded))
                {
                    Interlocked.Increment(ref SignatureFailures);
                    await WriteErrorAsync(ctx, 403, "SignatureDoesNotMatch", "chunked 链校验失败").ConfigureAwait(false);
                    return;
                }
                body = decoded;
            }
            if (!VerifySignature(ctx, body))
            {
                Interlocked.Increment(ref SignatureFailures);
                await WriteErrorAsync(ctx, 403, "SignatureDoesNotMatch", "computed != provided").ConfigureAwait(false);
                return;
            }
            await RouteAsync(ctx, body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { await WriteErrorAsync(ctx, 500, "InternalError", ex.Message).ConfigureAwait(false); }
            catch { /* 已写响应 */ }
        }
    }

    /// <summary>
    /// chunked 流式签名解码与链校验：帧头行 = size-hex;chunk-signature=sig，数据后接 CRLF（终帧 size=0）。
    /// 服务端以 Authorization 的 seed 签名起链独立重算——与客户端链任何一环不符即失败。
    /// </summary>
    private bool TryDecodeChunked(HttpListenerContext ctx, byte[] framed, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        var auth = ctx.Request.Headers["Authorization"]!;
        var parts = auth["AWS4-HMAC-SHA256 ".Length..].Split(", ", StringSplitOptions.TrimEntries);
        var sigPart = parts.FirstOrDefault(p => p.StartsWith("Signature=", StringComparison.Ordinal));
        var scopePart = parts.FirstOrDefault(p => p.StartsWith("Credential=", StringComparison.Ordinal));
        if (sigPart is null || scopePart is null) return false;
        var seedSignature = sigPart["Signature=".Length..];
        var scopeSegs = scopePart["Credential=".Length..].Split('/');
        if (scopeSegs.Length != 5) return false;
        var signingKey = SigV4.DeriveSigningKey(Secret!, scopeSegs[1], scopeSegs[2], scopeSegs[3]);
        var amzDate = ctx.Request.Headers["x-amz-date"]!;
        var scope = $"{scopeSegs[1]}/{scopeSegs[2]}/{scopeSegs[3]}/aws4_request";
        var prev = seedSignature;

        var ms = new MemoryStream();
        var pos = 0;
        while (true)
        {
            // 读帧头行
            var lineEnd = IndexOf(framed, CRLF, pos);
            if (lineEnd < 0) return false;
            var header = Encoding.ASCII.GetString(framed, pos, lineEnd - pos);
            var semi = header.IndexOf(';');
            if (semi < 0) return false;
            var sizeHex = header[..semi];
            var sigIdx = header.IndexOf("chunk-signature=", StringComparison.Ordinal);
            if (sigIdx < 0) return false;
            var chunkSig = header[(sigIdx + "chunk-signature=".Length)..];
            if (!int.TryParse(sizeHex, System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                return false;
            var dataStart = lineEnd + 2;
            // 链校验：重算本 chunk 期望签名
            var hash = size == 0 ? SigV4.EmptyPayloadHash : SigV4.Sha256Hex(framed.AsSpan(dataStart, size));
            var sts = SigV4.BuildChunkStringToSign(amzDate, scope, prev, hash);
            var expected = SigV4.SignChunk(signingKey, sts);
            if (!string.Equals(expected, chunkSig, StringComparison.Ordinal)) return false;
            prev = chunkSig;
            if (size == 0)
            {
                // 终帧头行后余量应恰为空行 CRLF（AWS 规范：0;sig CRLF CRLF）
                decoded = ms.ToArray();
                return framed.Length - dataStart == 2;
            }
            ms.Write(framed, dataStart, size);
            pos = dataStart + size + 2;   // 跳数据 + CRLF


        }
    }

    private static int IndexOf(byte[] haystack, string needle, int start)
    {
        var span = haystack.AsSpan(start);
        for (var i = 0; i <= span.Length - needle.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
                if (span[i + j] != needle[j]) { hit = false; break; }
            if (hit) return start + i;
        }
        return -1;
    }

    // ═════════════════════════════ 签名校验 ═════════════════════════════

    private bool VerifySignature(HttpListenerContext ctx, byte[] body)
    {
        var req = ctx.Request;
        var auth = req.Headers["Authorization"];
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
            return false;

        // Credential=AKID/date/region/service/aws4_request, SignedHeaders=..., Signature=...
        var parts = auth["AWS4-HMAC-SHA256 ".Length..].Split(", ", StringSplitOptions.TrimEntries);
        string? scope = null, signedHeaders = null, signature = null;
        foreach (var p in parts)
        {
            if (p.StartsWith("Credential=", StringComparison.Ordinal)) scope = p["Credential=".Length..];
            else if (p.StartsWith("SignedHeaders=", StringComparison.Ordinal)) signedHeaders = p["SignedHeaders=".Length..];
            else if (p.StartsWith("Signature=", StringComparison.Ordinal)) signature = p["Signature=".Length..];
        }
        if (scope is null || signedHeaders is null || signature is null) return false;
        var scopeSegs = scope.Split('/');
        if (scopeSegs.Length != 5) return false;   // AKID/date/region/service/aws4_request
        var date = scopeSegs[1];
        var region = scopeSegs[2];
        var service = scopeSegs[3];
        if (scopeSegs[4] != "aws4_request") return false;
        var scopeForSts = $"{date}/{region}/{service}/aws4_request";   // ★ scope 不含 AKID（Credential 值才含）

        // canonical headers：按 SignedHeaders 列表取值（host 经 UserHostName——Headers 不含受限头）
        var names = signedHeaders.Split(';');
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            var value = name == "host" ? req.UserHostName : req.Headers[name];
            sb.Append(name).Append(':').Append((value ?? "").Trim()).Append('\n');
        }

        var payloadHash = req.Headers["x-amz-content-sha256"] ?? SigV4.Sha256Hex(body);
        // 哈希一致性：真实 body 哈希 == 声明值（流式签名体经 chunk 链密码学覆盖——声明值非哈希，豁免直比；
        //   链完整性由 TryDecodeChunked 逐 chunk 重算保证）
        if (payloadHash != "UNSIGNED-PAYLOAD" && payloadHash != "STREAMING-AWS4-HMAC-SHA256-PAYLOAD"
            && payloadHash != SigV4.Sha256Hex(body))
            return false;

        var canonicalUri = req.Url!.AbsolutePath;
        var canonicalQuery = req.Url.Query.StartsWith('?') ? req.Url.Query[1..] : string.Empty;
        var canonical = $"{req.HttpMethod}\n{canonicalUri}\n{canonicalQuery}\n{sb}\n{signedHeaders}\n{payloadHash}";

        var canonicalHash = SigV4.Sha256Hex(Encoding.ASCII.GetBytes(canonical));
        var amzDate = req.Headers["x-amz-date"]!;
        var stringToSign = SigV4.BuildStringToSign(amzDate, scopeForSts, canonicalHash);
        var signingKey = SigV4.DeriveSigningKey(Secret!, date, region, service);
        var expected = SigV4.ComputeSignature(signingKey, stringToSign);
        if (!string.Equals(expected, signature, StringComparison.Ordinal))
            _lastSignatureDiag = $"canonical=[{canonical}] sts=[{stringToSign}] expectedSig={expected} got={signature} path={req.Url.AbsolutePath} query={req.Url.Query}";
        return string.Equals(expected, signature, StringComparison.Ordinal);
    }

    /// <summary>最近一次签名失配诊断（canonical 请求 + 双方签名值）。</summary>
    public string? LastSignatureDiag => _lastSignatureDiag;
    private string? _lastSignatureDiag;

    // 测试固定密钥（与客户端 options 一致）
    internal const string AccessKey = "TESTACCESSKEYID";
    internal const string Secret = "test/Secret+Key=Parts";

    // ═════════════════════════════ 路由 ═════════════════════════════

    private async Task RouteAsync(HttpListenerContext ctx, byte[] body)
    {
        var req = ctx.Request;
        var raw = req.Url!;
        LastRawPath = raw.AbsolutePath;
        Requests.Add((req.HttpMethod, raw.AbsolutePath, raw.Query));

        // /{bucket} 或 /{bucket}/{key...}
        var path = Uri.UnescapeDataString(raw.AbsolutePath);
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        if (slash < 0)
        {
            var bucketQuery = ParseQuery(raw.Query);
            if (bucketQuery.ContainsKey("uploads"))
                await HandleListUploadsAsync(ctx).ConfigureAwait(false);   // GET /{bucket}?uploads
            else
                await HandleListAsync(ctx).ConfigureAwait(false);          // GET /{bucket}?list-type=2
            return;
        }
        var key = trimmed[(slash + 1)..];
        var query = ParseQuery(raw.Query);

        switch (req.HttpMethod)
        {
            case "PUT" when query.ContainsKey("uploads"):
            case "POST" when query.ContainsKey("uploads"):
                await HandleCreateUploadAsync(ctx, key).ConfigureAwait(false);
                return;
            case "PUT" when query.ContainsKey("partNumber") && query.ContainsKey("uploadId"):
                await HandleUploadPartAsync(ctx, key, query, req, body).ConfigureAwait(false);
                return;
            case "POST" when query.ContainsKey("uploadId"):
                await HandleCompleteUploadAsync(ctx, key, query["uploadId"], req, body).ConfigureAwait(false);
                return;
            case "DELETE" when query.ContainsKey("uploadId"):
                lock (_sync) _sessions.Remove(query["uploadId"]);
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            case "PUT":
                await HandlePutAsync(ctx, key, req, body).ConfigureAwait(false);
                return;
            case "GET":
                await HandleGetAsync(ctx, key, req).ConfigureAwait(false);
                return;
            case "HEAD":
                HandleHead(ctx, key);
                return;
            case "DELETE":
                await HandleDeleteAsync(ctx, key, req).ConfigureAwait(false);
                return;
            default:
                await WriteErrorAsync(ctx, 400, "MethodNotAllowed", req.HttpMethod).ConfigureAwait(false);
                return;
        }
    }

    private static Dictionary<string, string> ParseQuery(string q)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(q) || q == "?") return result;
        foreach (var kv in q[1..].Split('&'))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) result[Uri.UnescapeDataString(kv)] = string.Empty;
            else result[Uri.UnescapeDataString(kv[..eq])] = Uri.UnescapeDataString(kv[(eq + 1)..]);
        }
        return result;
    }

    private async Task HandlePutAsync(HttpListenerContext ctx, string key, HttpListenerRequest req, byte[] body)
    {

        // CopyObject（x-amz-copy-source）——普通或 part 均可
        if (req.Headers["x-amz-copy-source"] is { } copySource)
        {
            var src = copySource.TrimStart('/').Split('/', 2);
            if (src.Length < 2 || !TryGet(src[1], out var srcObj))
            {
                await WriteErrorAsync(ctx, 404, "NoSuchKey", copySource).ConfigureAwait(false);
                return;
            }
            var range = req.Headers["x-amz-copy-source-range"];
            byte[] data = srcObj.Data;
            if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var bounds = range["bytes=".Length..].Split('-');
                var start = long.Parse(bounds[0]);
                var end = long.Parse(bounds[1]);
                data = srcObj.Data[(int)start..(int)(end + 1)];
            }
            var directive = req.Headers["x-amz-metadata-directive"] ?? "COPY";
            var meta = directive == "REPLACE" ? ReadMeta(req) : srcObj.Meta;
            lock (_sync) _objects[key] = new Obj(data, meta, DateTimeOffset.UtcNow);
            await WriteXmlAsync(ctx, 200, $"<CopyObjectResult><ETag>\"{EtagOf(data)}\"</ETag></CopyObjectResult>").ConfigureAwait(false);
            return;
        }

        // 条件写
        var ifMatch = req.Headers["If-Match"];
        var ifNoneMatch = req.Headers["If-None-Match"];
        lock (_sync)
        {
            _objects.TryGetValue(key, out var existing);
            if (ifMatch is not null)
            {
                if (existing is null)
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                if (ifMatch.Trim('"') != EtagOf(existing.Data))
                {
                    ctx.Response.StatusCode = 412;
                    ctx.Response.Close();
                    return;
                }
            }
            if (ifNoneMatch is not null
                && (ifNoneMatch == "*" ? existing is not null : existing is not null && ifNoneMatch.Trim('"') == EtagOf(existing.Data)))
            {
                ctx.Response.StatusCode = 412;
                ctx.Response.Close();
                return;
            }
            _objects[key] = new Obj(body, ReadMeta(req), DateTimeOffset.UtcNow);
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["ETag"] = $"\"{EtagOf(body)}\"";
        ctx.Response.Close();
    }

    private async Task HandleGetAsync(HttpListenerContext ctx, string key, HttpListenerRequest req)
    {
        if (!TryGet(key, out var obj))
        {
            await WriteErrorAsync(ctx, 404, "NoSuchKey", key).ConfigureAwait(false);
            return;
        }
        if (req.Headers["Range"] is { } range && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var bounds = range["bytes=".Length..].Split('-');
            var start = long.Parse(bounds[0]);
            var end = bounds.Length > 1 && bounds[1].Length > 0 ? long.Parse(bounds[1]) : obj.Data.LongLength - 1;
            if (start >= obj.Data.LongLength)
            {
                ctx.Response.StatusCode = 416;   // InvalidRange → 客户端归一 0
                ctx.Response.Close();
                return;
            }
            end = Math.Min(end, obj.Data.LongLength - 1);
            var len = (int)(end - start + 1);
            ctx.Response.StatusCode = 206;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.Headers["ETag"] = $"\"{EtagOf(obj.Data)}\"";
            await ctx.Response.OutputStream.WriteAsync(obj.Data.AsMemory((int)start, len)).ConfigureAwait(false);
            ctx.Response.Close();
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers["ETag"] = $"\"{EtagOf(obj.Data)}\"";
        foreach (var (k, v) in obj.Meta)
            ctx.Response.Headers[$"x-amz-meta-{k}"] = v;
        await ctx.Response.OutputStream.WriteAsync(obj.Data).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private void HandleHead(HttpListenerContext ctx, string key)
    {
        if (!TryGet(key, out var obj))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["ETag"] = $"\"{EtagOf(obj.Data)}\"";
        ctx.Response.Headers["Last-Modified"] = obj.CreatedAtUtc.ToString("R");
        ctx.Response.ContentLength64 = obj.Data.LongLength;
        foreach (var (k, v) in obj.Meta)
            ctx.Response.Headers[$"x-amz-meta-{k}"] = v;
        ctx.Response.Close();   // HEAD 无 body
    }

    private Task HandleDeleteAsync(HttpListenerContext ctx, string key, HttpListenerRequest req)
    {
        if (req.Headers["If-Match"] is { } ifMatch)
        {
            lock (_sync)
            {
                if (!_objects.TryGetValue(key, out var existing) || ifMatch.Trim('"') != EtagOf(existing.Data))
                {
                    ctx.Response.StatusCode = _objects.ContainsKey(key) ? 412 : 404;
                    ctx.Response.Close();
                    return Task.CompletedTask;
                }
            }
        }
        lock (_sync) _objects.Remove(key);
        ctx.Response.StatusCode = 204;
        ctx.Response.Close();
        return Task.CompletedTask;
    }

    private async Task HandleListUploadsAsync(HttpListenerContext ctx)
    {
        var sb = new StringBuilder("<ListMultipartUploadsResult>");
        List<(string Key, string Id, DateTimeOffset Initiated)> snapshot;
        lock (_sync)
            snapshot = _sessions.Select(kv => (kv.Value.Key, kv.Key, kv.Value.InitiatedUtc))
                .OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
        foreach (var (k, id, initiated) in snapshot)
            sb.Append($"<Upload><Key>{k}</Key><UploadId>{id}</UploadId>" +
                      $"<Initiated>{initiated:yyyy-MM-ddTHH:mm:ss.fffZ}</Initiated></Upload>");
        sb.Append("<IsTruncated>false</IsTruncated></ListMultipartUploadsResult>");
        await WriteXmlAsync(ctx, 200, sb.ToString()).ConfigureAwait(false);
    }

    private async Task HandleListAsync(HttpListenerContext ctx)
    {
        var query = ParseQuery(ctx.Request.Url!.Query);
        if (query.GetValueOrDefault("list-type") != "2")
        {
            await WriteErrorAsync(ctx, 400, "InvalidRequest", "list-type=2 required").ConfigureAwait(false);
            return;
        }
        var prefix = query.GetValueOrDefault("prefix") ?? string.Empty;
        var delimiter = query.GetValueOrDefault("delimiter");   // ListDelimited（CommonPrefix 服务端聚合）
        var token = query.GetValueOrDefault("continuation-token");

        List<string> keys;
        lock (_sync) keys = _objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(static k => k, StringComparer.Ordinal).ToList();

        // 合并结果流（S3 语义）：无 delimiter = 纯对象键；有 delimiter = 键在 prefix 后首个分隔符处截断
        // 聚合为 CommonPrefix（去重），未截断键保持 Contents——两流按 Ordinal 交错分页
        List<(string Item, long Size, bool IsPrefix)> merged = new();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in keys)
        {
            var cut = delimiter is null ? -1 : k.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
            if (cut < 0)
            {
                long size;
                lock (_sync) size = _objects[k].Data.LongLength;
                merged.Add((k, size, false));
            }
            else
            {
                var cp = k[..(cut + delimiter!.Length)];
                if (seenPrefixes.Add(cp))
                    merged.Add((cp, 0, true));
            }
        }

        // 分页：token = 上页末项（键或前缀均可比较）
        var start = token is null ? 0 : merged.FindIndex(m => string.CompareOrdinal(m.Item, token) > 0);
        if (start < 0) start = merged.Count;
        var page = merged.Skip(start).Take(MaxKeys).ToList();
        var truncated = start + page.Count < merged.Count;

        var sb = new StringBuilder("<ListBucketResult>");
        sb.Append($"<IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>");
        if (truncated) sb.Append($"<NextContinuationToken>{page[^1].Item}</NextContinuationToken>");
        foreach (var (item, size, isPrefix) in page)
        {
            if (isPrefix)
                sb.Append($"<CommonPrefixes><Prefix>{item}</Prefix></CommonPrefixes>");
            else
            {
                DateTimeOffset lm;
                lock (_sync) lm = _objects[item].CreatedAtUtc;
                sb.Append($"<Contents><Key>{item}</Key><LastModified>{lm:yyyy-MM-ddTHH:mm:ss.fffZ}</LastModified>"
                          + $"<Size>{size}</Size></Contents>");
            }
        }
        sb.Append("</ListBucketResult>");
        await WriteXmlAsync(ctx, 200, sb.ToString()).ConfigureAwait(false);
    }

    // ═════════════════════════════ multipart ═════════════════════════════

    private async Task HandleCreateUploadAsync(HttpListenerContext ctx, string key)
    {
        var uploadId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        lock (_sync) _sessions[uploadId] = new UploadState(key, DateTimeOffset.UtcNow);
        await WriteXmlAsync(ctx, 200,
            $"<InitiateMultipartUploadResult><UploadId>{uploadId}</UploadId></InitiateMultipartUploadResult>").ConfigureAwait(false);
    }

    private async Task HandleUploadPartAsync(HttpListenerContext ctx, string key,
                                             Dictionary<string, string> query, HttpListenerRequest req, byte[] body)
    {
        var uploadId = query["uploadId"];
        var partNumber = int.Parse(query["partNumber"]);
        byte[]? data = null;
        int? failureStatus = null;
        string failureCode = "NoSuchUpload", failureMessage = uploadId;
        lock (_sync)
        {
            if (_sessions.TryGetValue(uploadId, out var state))
            {
                var parts = state.Parts;
                if (req.Headers["x-amz-copy-source"] is { } copySource)
                {
                    var src = copySource.TrimStart('/').Split('/', 2);
                    if (src.Length == 2 && _objects.TryGetValue(src[1], out var srcObj))
                    {
                        var range = req.Headers["x-amz-copy-source-range"];
                        data = range is not null
                            ? srcObj.Data[RangeOf(range)]
                            : srcObj.Data;
                    }
                    else
                    {
                        failureStatus = 404;
                        failureCode = "NoSuchKey";
                        failureMessage = copySource;
                    }
                }
                else data = body;

                if (data is not null)
                    parts[partNumber] = data;
                else if (failureStatus is null)
                    failureStatus = 500;
            }
        }
        if (failureStatus is { } status || data is null)
        {
            await WriteErrorAsync(ctx, failureStatus ?? 500, failureCode, failureMessage).ConfigureAwait(false);
            return;
        }
        // CopyPart 真实形态：200 + CopyPartResult XML；普通 part：200 + ETag 头
        if (req.Headers["x-amz-copy-source"] is not null)
        {
            await WriteXmlAsync(ctx, 200,
                $"<CopyPartResult><ETag>\"{EtagOf(data)}\"</ETag></CopyPartResult>").ConfigureAwait(false);
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["ETag"] = $"\"{EtagOf(data)}\"";
        ctx.Response.Close();
    }

    private static Range RangeOf(string rangeHeader)
    {
        var bounds = rangeHeader["bytes=".Length..].Split('-');
        var start = int.Parse(bounds[0]);
        var end = int.Parse(bounds[1]);
        return start..(end + 1);
    }

    private async Task HandleCompleteUploadAsync(HttpListenerContext ctx, string key, string uploadId, HttpListenerRequest req, byte[] body)
    {
        var xml = Encoding.UTF8.GetString(body);
        UploadState? state;
        lock (_sync) _sessions.Remove(uploadId, out state);
        if (state is null)
        {
            await WriteErrorAsync(ctx, 404, "NoSuchUpload", uploadId).ConfigureAwait(false);
            return;
        }
        var parts = state.Parts;
        // CompleteMultipartUpload XML：<Part><PartNumber>n</PartNumber><ETag>..</ETag></Part>
        var assembled = new List<byte>();
        var partNumbers = new List<int>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(xml, "<PartNumber>(\\d+)</PartNumber>"))
            partNumbers.Add(int.Parse(m.Groups[1].Value));
        partNumbers.Sort();
        foreach (var pn in partNumbers)
        {
            if (!parts.TryGetValue(pn, out var data))
            {
                await WriteErrorAsync(ctx, 400, "InvalidPart", pn.ToString()).ConfigureAwait(false);
                return;
            }
            assembled.AddRange(data);
        }
        var result = assembled.ToArray();
        lock (_sync) _objects[key] = new Obj(result, ReadMeta(req), DateTimeOffset.UtcNow);
        await WriteXmlAsync(ctx, 200,
            $"<CompleteMultipartUploadResult><ETag>\"{EtagOf(result)}\"</ETag></CompleteMultipartUploadResult>").ConfigureAwait(false);
    }

    // ═════════════════════════════ 公共 ═════════════════════════════

    private static Dictionary<string, string> ReadMeta(HttpListenerRequest req)
    {
        var dict = new Dictionary<string, string>();
        foreach (var h in req.Headers.AllKeys)
        {
            if (h is not null && h.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
                dict[h["x-amz-meta-".Length..]] = req.Headers[h]!;
        }
        return dict;
    }

    private bool TryGet(string key, out Obj obj)
    {
        lock (_sync)
        {
            if (_objects.TryGetValue(key, out obj!)) return true;
            obj = null!;
            return false;
        }
    }

    private static string EtagOf(byte[] data)
        => Convert.ToHexString(XxHash128.Hash(data)).ToLowerInvariant();

    private static async Task WriteXmlAsync(HttpListenerContext ctx, int status, string xml)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/xml";
        var bytes = Encoding.UTF8.GetBytes(xml);
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static async Task WriteErrorAsync(HttpListenerContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        var xml = $"<Error><Code>{code}</Code><Message>{message}</Message></Error>";
        var bytes = Encoding.UTF8.GetBytes(xml);
        ctx.Response.ContentType = "application/xml";
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* 已停 */ }
        try { _listener.Close(); } catch { /* 已关 */ }
        _loop.Wait(TimeSpan.FromSeconds(2));
        GC.SuppressFinalize(this);
    }
}
