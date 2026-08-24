using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TC.Tier.Core.IO.S3;

/// <summary>
/// SigV4 签名核心——canonical request 构造 + HMAC-SHA256 签名链（零外部依赖，System.Security.Cryptography 全覆盖）。
/// <para>★ S3 特例：canonical URI <b>不归一化、不二次编码</b>（其他 AWS 服务二次编码）——本实现用同一编码器
///   生成"实际请求 URL"与"canonical URI"，两者恒一致（对齐 S3 事实标准）。</para>
/// <para>★ 正确性验证三层：AWS 官方文档黄金向量（SigV4GoldenVectorTests）/ 进程内假 S3 服务器 /
///   MinIO 真协议终验（认证我们的签名 = 独立实现的司法鉴定）。</para>
/// <para>★ 首版整段签名（payload 哈希逐段计算）；chunked 流式签名为演进项（§7.2）。</para>
/// </summary>
internal static class SigV4
{
    internal const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>空 body 的 SHA-256 hex（文档常量——高频复用）。</summary>
    internal const string EmptyPayloadHash =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>RFC 3986 unreserved 字符集之外的字符全部 %XX（大写 hex）——S3 路径与查询值同一编码器。</summary>
    /// <param name="value">待编码文本。</param>
    /// <param name="encodeSlash">true = '/' 也编码（查询键值）；false = '/' 直通（canonical URI 路径段）。</param>
    internal static string UriEncode(string value, bool encodeSlash = true)
    {
        // 预估容量：最坏全编码 3 倍
        var sb = new StringBuilder(value.Length * 3);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~')
            {
                sb.Append(c);
            }
            else if (c == '/' && !encodeSlash)
            {
                sb.Append('/');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));   // 大写 hex（AWS 规范）
            }
        }
        return sb.ToString();
    }

    /// <summary>canonical query string——键排序（Ordinal），键值各自 UriEncode，&amp; 连接。</summary>
    internal static string CanonicalQueryString(IEnumerable<(string Name, string Value)> query)
        => string.Join("&", query
            .OrderBy(static kv => kv.Name, StringComparer.Ordinal)
            .ThenBy(static kv => kv.Value, StringComparer.Ordinal)
            .Select(static kv => $"{UriEncode(kv.Name)}={UriEncode(kv.Value)}"));

    /// <summary>canonical request 六行体（method / URI / query / headers / signedHeaders / payloadHash）。</summary>
    internal static string BuildCanonicalRequest(string method, string canonicalUri, string canonicalQuery,
                                                 IReadOnlyList<(string Name, string Value)> headers, string payloadHash)
    {
        // headers 须已按 name 小写排序（调用方责任——SignRequest 内部统一处理）
        var sb = new StringBuilder(256);
        sb.Append(method).Append('\n')
          .Append(canonicalUri).Append('\n')
          .Append(canonicalQuery).Append('\n');
        foreach (var (name, value) in headers)
            sb.Append(name).Append(':').Append(value.Trim()).Append('\n');
        sb.Append('\n')
          .Append(string.Join(";", headers.Select(static h => h.Name))).Append('\n')
          .Append(payloadHash);
        return sb.ToString();
    }

    /// <summary>string to sign 四行体（算法 / 时间戳 / credential scope / canonical request 哈希）。</summary>
    internal static string BuildStringToSign(string amzDate, string scope, string canonicalRequestHash)
        => $"{Algorithm}\n{amzDate}\n{scope}\n{canonicalRequestHash}";

    /// <summary>签名链派生：kSecret → kDate → kRegion → kService → kSigning。</summary>
    internal static byte[] DeriveSigningKey(string secretAccessKey, string date, string region, string service)
    {
        var kDate = Hmac(Encoding.ASCII.GetBytes("AWS4" + secretAccessKey), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        return Hmac(kService, "aws4_request");
    }

    /// <summary>最终签名 = hex(HMAC-SHA256(signingKey, stringToSign))。</summary>
    internal static string ComputeSignature(byte[] signingKey, string stringToSign)
        => Convert.ToHexString(Hmac(signingKey, stringToSign)).ToLowerInvariant();

    internal static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.ASCII.GetBytes(data));
    }

    internal static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>流式 SHA-256 hex（staging spill 的流式 PUT——单遍哈希后回卷再传，零整驻内存）。</summary>
    internal static string Sha256Hex(Stream stream)
    {
        var position = stream.Position;
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        stream.Position = position;   // 回卷（须可寻——调用方保证）
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>x-amz-date 格式（ISO 8601 basic）。</summary>
    internal static string AmzDate(DateTimeOffset utc)
        => utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>scope 日期段（yyyyMMdd）。</summary>
    internal static string ScopeDate(DateTimeOffset utc)
        => utc.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // ═══════════════ chunked 流式签名（STREAMING-AWS4-HMAC-SHA256-PAYLOAD）═══════════════

    /// <summary>流式签名的内容哈希标识（canonical request 尾行与 x-amz-content-sha256 头同值）。</summary>
    internal const string StreamingContentSha256 = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD";

    /// <summary>chunk 签名算法行。</summary>
    internal const string ChunkAlgorithm = "AWS4-HMAC-SHA256-PAYLOAD";

    /// <summary>
    /// chunk 级 string-to-sign：算法行 / amzDate / scope / <b>前一签名</b>（首 chunk = seed 签名）/
    /// 空串哈希（headers 槽位）/ chunk 数据哈希。
    /// </summary>
    internal static string BuildChunkStringToSign(string amzDate, string scope, string previousSignature,
                                                  string chunkDataHashHex)
        => $"{ChunkAlgorithm}\n{amzDate}\n{scope}\n{previousSignature}\n{EmptyPayloadHash}\n{chunkDataHashHex}";

    /// <summary>chunk 签名 = hex(HMAC(signingKey, chunkStringToSign))——成为下一 chunk 的"前一签名"。</summary>
    internal static string SignChunk(byte[] signingKey, string chunkStringToSign)
        => ComputeSignature(signingKey, chunkStringToSign);

#if DEBUG
    /// <summary>最近一次签名诊断（BuildRequest 侧 canonical + stringToSign——假服务器 diff 用；测试仪器）。</summary>
    internal static string? LastCanonical { get; private set; }
    internal static string? LastStringToSign { get; private set; }

    internal static void RecordDiagnostics(string canonical, string stringToSign)
    {
        LastCanonical = canonical;
        LastStringToSign = stringToSign;
    }
#endif
}
