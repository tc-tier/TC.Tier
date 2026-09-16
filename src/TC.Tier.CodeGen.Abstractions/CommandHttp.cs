namespace TC.Tier.CodeGen;

/// <summary>
/// 传输中立的命令 HTTP 请求（#435 HttpApi 执行代理入口契约）——宿主适配归消费方
/// （Core.Net 无 HTTP 服务面；挂载 = 宿主把自有监听面翻译为本契约）。
/// </summary>
public readonly struct CommandHttpRequest
{
    /// <summary>HTTP 方法（已大写规范化："GET"/"POST"）。</summary>
    public string Method { get; init; }

    /// <summary>原始路径（分段解码由生成代码处理）。</summary>
    public string EncodedPath { get; init; }

    /// <summary>原始 query 串（? 后内容，可空）。</summary>
    public string? Query { get; init; }

    /// <summary>请求体字节（GET 命令禁 body）。</summary>
    public byte[] Body { get; init; }
}

/// <summary>传输中立的命令 HTTP 应答。</summary>
public readonly struct CommandHttpResponse
{
    /// <summary>HTTP 状态码。</summary>
    public int Status { get; init; }

    /// <summary>Content-Type（可空）。</summary>
    public string? ContentType { get; init; }

    /// <summary>应答体字节。</summary>
    public byte[] Body { get; init; }
}
