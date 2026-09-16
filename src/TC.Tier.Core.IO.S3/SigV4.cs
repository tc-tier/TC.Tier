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
    /// <param name="query">查询参数列表（名称/值元组）。</param>
    /// <returns>规范化查询字符串（&amp; 连接的 key=value 序列，按名称 Ordinal 后再按值排序）。</returns>
    internal static string CanonicalQueryString(IEnumerable<(string Name, string Value)> query)
        => string.Join("&", query
            .OrderBy(static kv => kv.Name, StringComparer.Ordinal)
            .ThenBy(static kv => kv.Value, StringComparer.Ordinal)
            .Select(static kv => $"{UriEncode(kv.Name)}={UriEncode(kv.Value)}"));

    /// <summary>canonical request 六行体（method / URI / query / headers / signedHeaders / payloadHash）。</summary>
    /// <param name="method">HTTP 方法（大写）。</param>
    /// <param name="canonicalUri">已 UriEncode 的 canonical URI（路径段，'/' 直通）。</param>
    /// <param name="canonicalQuery">规范化查询字符串（经 CanonicalQueryString 产出）。</param>
    /// <param name="headers">请求头列表（名称须小写，按名称 Ordinal 排序——调用方责任）。</param>
    /// <param name="payloadHash">载荷 SHA-256 hex（空 body = EmptyPayloadHash；流式 = StreamingContentSha256）。</param>
    /// <returns>canonical request 文本（换行连接的六行体）。</returns>
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
    /// <param name="amzDate">x-amz-date（ISO 8601 basic，yyyyMMddTHHmmssZ）。</param>
    /// <param name="scope">credential scope（date/region/service/aws4_request）。</param>
    /// <param name="canonicalRequestHash">canonical request 的 SHA-256 hex。</param>
    /// <returns>string-to-sign 文本（换行连接的四行体）。</returns>
    internal static string BuildStringToSign(string amzDate, string scope, string canonicalRequestHash)
        => $"{Algorithm}\n{amzDate}\n{scope}\n{canonicalRequestHash}";

    /// <summary>签名链派生：kSecret → kDate → kRegion → kService → kSigning。</summary>
    /// <param name="secretAccessKey">AWS Secret Access Key（前缀 "AWS4"）。</param>
    /// <param name="date">scope 日期段（yyyyMMdd）。</param>
    /// <param name="region">区域（如 "us-east-1"）。</param>
    /// <param name="service">服务名（S3 = "s3"）。</param>
    /// <returns>派生签名密钥（32 字节 HMAC-SHA256 输出）。</returns>
    internal static byte[] DeriveSigningKey(string secretAccessKey, string date, string region, string service)
    {
        var kDate = Hmac(Encoding.ASCII.GetBytes("AWS4" + secretAccessKey), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        return Hmac(kService, "aws4_request");
    }

    /// <summary>最终签名 = hex(HMAC-SHA256(signingKey, stringToSign))。</summary>
    /// <param name="signingKey">派生签名密钥（DeriveSigningKey 产出）。</param>
    /// <param name="stringToSign">待签名的 string-to-sign 文本。</param>
    /// <returns>小写 hex 签名串（64 字符）。</returns>
    internal static string ComputeSignature(byte[] signingKey, string stringToSign)
        => Convert.ToHexString(Hmac(signingKey, stringToSign)).ToLowerInvariant();

    /// <summary>HMAC-SHA256 原语。</summary>
    /// <param name="key">HMAC 密钥。</param>
    /// <param name="data">待认证文本（ASCII 编码后哈希）。</param>
    /// <returns>32 字节 HMAC 输出。</returns>
    internal static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.ASCII.GetBytes(data));
    }

    /// <summary>计算字节 span 的 SHA-256 hex（载荷哈希/块哈希）。</summary>
    /// <param name="data">待哈希数据。</param>
    /// <returns>小写 hex 哈希串（64 字符）。</returns>
    internal static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>流式 SHA-256 hex（staging spill 的流式 PUT——单遍哈希后回卷再传，零整驻内存）。</summary>
    /// <param name="stream">可寻流（哈希后回卷——调用方保证 Position 可复）。</param>
    /// <returns>小写 hex 哈希串（64 字符）。</returns>
    internal static string Sha256Hex(Stream stream)
    {
        var position = stream.Position;
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        stream.Position = position;   // 回卷（须可寻——调用方保证）
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>x-amz-date 格式（ISO 8601 basic）。</summary>
    /// <param name="utc">UTC 时间。</param>
    /// <returns>yyyyMMddTHHmmssZ 格式时间戳。</returns>
    internal static string AmzDate(DateTimeOffset utc)
        => utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>scope 日期段（yyyyMMdd）。</summary>
    /// <param name="utc">UTC 时间。</param>
    /// <returns>yyyyMMdd 格式日期串。</returns>
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
    /// <param name="amzDate">x-amz-date 时间戳。</param>
    /// <param name="scope">credential scope。</param>
    /// <param name="previousSignature">前一签名（首 chunk = seed 签名）。</param>
    /// <param name="chunkDataHashHex">本 chunk 原始数据的 SHA-256 hex。</param>
    /// <returns>chunk 级 string-to-sign 文本。</returns>
    internal static string BuildChunkStringToSign(string amzDate, string scope, string previousSignature,
                                                  string chunkDataHashHex)
        => $"{ChunkAlgorithm}\n{amzDate}\n{scope}\n{previousSignature}\n{EmptyPayloadHash}\n{chunkDataHashHex}";

    /// <summary>chunk 签名 = hex(HMAC(signingKey, chunkStringToSign))——成为下一 chunk 的"前一签名"。</summary>
    /// <param name="signingKey">派生签名密钥。</param>
    /// <param name="chunkStringToSign">chunk 级 string-to-sign。</param>
    /// <returns>小写 hex 签名串（64 字符）。</returns>
    internal static string SignChunk(byte[] signingKey, string chunkStringToSign)
        => ComputeSignature(signingKey, chunkStringToSign);

#if DEBUG
    /// <summary>最近一次签名诊断（BuildRequest 侧 canonical + stringToSign——假服务器 diff 用；测试仪器）。</summary>
    internal static string? LastCanonical { get; private set; }
    internal static string? LastStringToSign { get; private set; }

    /// <summary>记录最近一次签名诊断（假服务器 diff 用——仅 DEBUG）。</summary>
    /// <param name="canonical">canonical request 文本。</param>
    /// <param name="stringToSign">string-to-sign 文本。</param>
    internal static void RecordDiagnostics(string canonical, string stringToSign)
    {
        LastCanonical = canonical;
        LastStringToSign = stringToSign;
    }
#endif
}
