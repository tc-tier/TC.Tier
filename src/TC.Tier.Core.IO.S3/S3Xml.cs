using System.Globalization;
using System.Text;
using System.Xml.Linq;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.IO.S3;

/// <summary>
/// S3 XML 响应解析与请求体构造（System.Xml.Linq——BCL 自带，零外部依赖）。
/// <para>★ .NET XDocument 默认禁 DTD——XXE 免疫；畸形响应抛 <see cref="IOError.IOFailure"/> 归一。</para>
/// </summary>
internal static class S3Xml
{
    /// <summary>ListObjectsV2 单页解析——条目 + 公共前缀（delimiter 聚合）+ 分页游标。</summary>
    internal static (IReadOnlyList<ObjectEntry> Entries, IReadOnlyList<string> CommonPrefixes,
                     bool IsTruncated, string? NextContinuationToken)
        ParseListPage(Stream body)
    {
        var root = Parse(body, "ListBucketResult");
        // ★ 命名空间免疫：S3 各实现（AWS/MinIO/OSS）对 xmlns 使用不一——一律 LocalName 匹配
        var entries = new List<ObjectEntry>();
        foreach (var contents in root.Elements().Where(e => e.Name.LocalName == "Contents"))
        {
            var key = (string?)LocalElement(contents, "Key");
            var size = (long?)LocalElement(contents, "Size");
            if (key is null || size is null) continue;   // 畸形条目跳过（容错——分页协议继续）
            // LastModified：ISO 8601（如 T07:21:06.000Z）——解析失败条目跳过时间不跳对象（容错）
            DateTimeOffset? lastModified = null;
            var lm = (string?)LocalElement(contents, "LastModified");
            if (lm is not null
                && DateTimeOffset.TryParse(lm, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                lastModified = parsed;
            entries.Add(new ObjectEntry(key, size.Value, lastModified));
        }
        var prefixes = new List<string>();
        foreach (var cp in root.Elements().Where(e => e.Name.LocalName == "CommonPrefixes"))
        {
            var p = (string?)LocalElement(cp, "Prefix");
            if (p is not null) prefixes.Add(p);
        }
        var truncated = (bool?)LocalElement(root, "IsTruncated") ?? false;
        var token = (string?)LocalElement(root, "NextContinuationToken");
        return (entries, prefixes, truncated, truncated ? token : null);
    }

    /// <summary>InitiateMultipartUploadResult → UploadId。</summary>
    internal static string ParseUploadId(Stream body)
        => (string?)LocalElement(Parse(body, "InitiateMultipartUploadResult"), "UploadId")
           ?? throw new FileIOException(IOError.IOFailure, "CreateMultipartUpload 响应缺 UploadId。", null, "CreateMultipartUpload");

    /// <summary>CopyPartResult / CopyObjectResult → ETag（去引号归一）。</summary>
    internal static string ParseCopyEtag(Stream body)
    {
        var root = Parse(body, null);
        var etag = (string?)LocalElement(root, "ETag");
        return etag is null
            ? throw new FileIOException(IOError.IOFailure, "Copy 响应缺 ETag。", null, "Copy")
            : etag.Trim('"');
    }

    /// <summary>CompleteMultipartUploadResult → 对象 ETag（可缺失——宽松取）。</summary>
    internal static string? ParseCompleteEtag(Stream body)
        => LocalElement(Parse(body, "CompleteMultipartUploadResult"), "ETag")?.Value?.Trim('"');

    /// <summary>Error 响应 → (Code, Message)。</summary>
    internal static (string Code, string Message) ParseError(Stream body)
    {
        try
        {
            var root = XElement.Load(body);
            if (root.Name.LocalName != "Error")
                return ("Unknown", root.Name.LocalName);
            var code = (string?)root.Element("Code") ?? "Unknown";
            var message = (string?)root.Element("Message") ?? string.Empty;
            return (code, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ("Unknown", $"非 XML 错误响应: {ex.Message}");
        }
    }

    /// <summary>
    /// ★ S3 特性检测：200 + Error body（CompleteMultipartUpload 的延迟失败形态）——
    /// 根元素为 Error 时返回 true 并给出错误元组（调用方走错误映射）。
    /// </summary>
    internal static bool TryReadErrorBody(Stream body, out (string Code, string Message) error)
    {
        try
        {
            var root = XElement.Load(body);
            if (root.Name.LocalName != "Error")
            {
                error = default;
                return false;
            }
            error = ((string?)root.Element("Code") ?? "Unknown", (string?)root.Element("Message") ?? string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ("Unknown", $"非 XML 响应: {ex.Message}");
            return false;   // 解析失败不误报——调用方按成功处理（ETag 宽松）
        }
    }

    /// <summary>ListMultipartUploads 单页解析——会话 + 分页游标（key-marker 语义）。</summary>
    internal static (IReadOnlyList<MultipartUploadSession> Sessions, bool IsTruncated,
                     string? NextKeyMarker, string? NextUploadIdMarker)
        ParseMultipartUploadsPage(Stream body)
    {
        var root = Parse(body, "ListMultipartUploadsResult");
        var sessions = new List<MultipartUploadSession>();
        foreach (var upload in root.Elements().Where(e => e.Name.LocalName == "Upload"))
        {
            var key = (string?)LocalElement(upload, "Key");
            var uploadId = (string?)LocalElement(upload, "UploadId");
            if (key is null || uploadId is null) continue;
            var initiated = DateTimeOffset.TryParse((string?)LocalElement(upload, "Initiated"),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
            sessions.Add(new MultipartUploadSession(key, uploadId, initiated));
        }
        var truncated = (bool?)LocalElement(root, "IsTruncated") ?? false;
        return (sessions, truncated,
            truncated ? (string?)LocalElement(root, "NextKeyMarker") : null,
            truncated ? (string?)LocalElement(root, "NextUploadIdMarker") : null);
    }

    /// <summary>CompleteMultipartUpload 请求体。</summary>
    internal static byte[] BuildCompleteMultipart(IReadOnlyList<UploadPartResult> parts)
    {
        var ns = XNamespace.None;
        var xml = new XDocument(new XElement(ns + "CompleteMultipartUpload",
            parts.OrderBy(static p => p.PartNumber).Select(static p =>
                new XElement("Part",
                    new XElement("PartNumber", p.PartNumber),
                    new XElement("ETag", $"\"{p.ETag.Trim('"')}\"")))));
        return Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
    }

    private static XElement Parse(Stream body, string? expectedRoot)
    {
        XElement root;
        try
        {
            root = XElement.Load(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileIOException(IOError.IOFailure, $"S3 XML 响应解析失败: {ex.Message}", null, "parse");
        }
        if (expectedRoot is not null && root.Name.LocalName != expectedRoot)
            throw new FileIOException(IOError.IOFailure,
                $"S3 XML 响应根元素异常（期望 {expectedRoot}，实得 {root.Name.LocalName}）。", null, "parse");
        return root;
    }

    /// <summary>命名空间免疫子元素读取（LocalName 匹配——xmlns 使用差异吸收）。</summary>
    private static XElement? LocalElement(XElement parent, string name)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);
}
