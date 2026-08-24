namespace TC.Tier.Core.IO.S3;

/// <summary>
/// S3 客户端配置——endpoint/region/凭证/重试/超时/能力位声明。
/// <para>★ 兼容云矩阵：同一实现换 endpoint/credentials 覆盖 S3/OSS(S3 兼容)/MinIO/R2/B2；
///   COS 不承诺（独立 V5 签名，另议——remote-storage-s3 设计 §7.5）。</para>
/// <para>★ 能力位声明（Supports*）：S3 2023+/MinIO 当前版条件 PUT/DELETE 均 ✓（默认开）；
///   老兼容端点（部分 OSS 版本/老 MinIO）由部署方按实际关闭——能力位诚实表达，fencing 层据此降级。</para>
/// </summary>
public sealed class S3ClientOptions
{
    /// <summary>端点（scheme://host[:port]，如 <c>http://127.0.0.1:9000</c> / <c>https://s3.cn-north-1.amazonaws.com.cn</c>）。</summary>
    public required string Endpoint { get; init; }

    /// <summary>桶名（path-style 寻址：<c>{endpoint}/{bucket}/{key}</c>——MinIO/自建兼容端标准形态）。</summary>
    public required string Bucket { get; init; }

    /// <summary>区域（签名 scope 用；MinIO 默认 us-east-1）。</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>凭证源。</summary>
    public required ICredentialProvider Credentials { get; init; }

    /// <summary>单请求超时（默认 100s——含大对象上传）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>重试次数上限（幂等操作：5xx/429/网络抖动——指数退避 + 抖动）。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>重试基础延迟（指数退避基数，默认 200ms）。</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>服务端条件 PUT（If-Match/If-None-Match）可用（S3 2023+/MinIO 当前 ✓；缺 → fencing 降级）。</summary>
    public bool SupportsConditionalPut { get; init; } = true;

    /// <summary>服务端条件 DELETE（If-Match）可用（S3 2023+/MinIO 当前 ✓；缺 → Head+无条件删降级）。</summary>
    public bool SupportsConditionalDelete { get; init; } = true;

    /// <summary>写后立即可见（S3 2020.12+/MinIO ✓；老 OSS 最终一致——读后短重试吸收）。</summary>
    public bool SupportsStrongList { get; init; } = true;

    /// <summary>
    /// 签名 Host 解耦（增补设计 §9）：连接 Host（endpoint 派生）与<b>签名 Host</b> 分离——
    /// 经自有域名反代到云原生域名时（cos.mytzz.top → cos.region.myqcloud.com），
    /// 签云域名（SigV4 所签 = 服务端所见，反代层负责 Host 改写与 SNI）。null = endpoint 派生（默认）。
    /// </summary>
    public string? SigningHost { get; init; }

    /// <summary>
    /// 连接池寿命（连接建立起计的总年龄，与活跃无关——到期重建）。默认 10 分钟。
    /// ★ 防死连接复用（Aliyun OSS 服务端 60-90s 断开空闲连接——老 SDK lifetime=0 永不回收导致
    /// 周期性 SSL 抖动的根因）；配合 <see cref="PooledConnectionIdleTimeout"/> 双防线。
    /// </summary>
    public TimeSpan? PooledConnectionLifetime { get; init; }

    /// <summary>
    /// 连接池空闲超时（空闲即关——客户端在服务端断开前主动回收）。默认 60s（显式钉死——
    /// 须小于目标端点的服务端空闲断开阈值；OSS 实测 60-90s）。
    /// </summary>
    public TimeSpan? PooledConnectionIdleTimeout { get; init; }

    /// <summary>
    /// virtual-host 寻址（<c>{bucket}.{endpoint-host}/{key}</c>；默认 false = path-style
    /// <c>{endpoint}/{bucket}/{key}</c>）。★ COS 的 S3 兼容层要求 virtual-host（path-style
    /// 会把整段路径当 key——桶级操作与 copy 全部失真）；S3/MinIO/R2 两者皆可（R2 实际仅支持 vhost）。
    /// </summary>
    public bool UseVirtualHostAddressing { get; init; }

    /// <summary>构造校验（endpoint 形态/桶名非空）。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new ArgumentException("Endpoint 不能为空。", nameof(Endpoint));
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException($"Endpoint 须为 scheme://host[:port] 形态: {Endpoint}", nameof(Endpoint));
        if (string.IsNullOrWhiteSpace(Bucket))
            throw new ArgumentException("Bucket 不能为空。", nameof(Bucket));
        ArgumentNullException.ThrowIfNull(Credentials);
    }

    /// <summary>
    /// 签名/路由用 Host 头值：<see cref="SigningHost"/> 优先；virtual-host 寻址时 = <c>{bucket}.{endpoint-host}</c>；
    /// 否则 endpoint 派生（非默认端口含端口；默认 80/443 省略——HTTP 规范）。
    /// </summary>
    internal string HostHeader
    {
        get
        {
            var uri = new Uri(Endpoint);
            var isDefault = (uri.Scheme == "http" && uri.Port == 80)
                            || (uri.Scheme == "https" && uri.Port == 443);
            var endpointHost = isDefault || uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            if (SigningHost is { } custom) return custom;
            return UseVirtualHostAddressing ? $"{Bucket}.{endpointHost}" : endpointHost;
        }
    }

    /// <summary>请求 scheme（URL 构造用）。</summary>
    internal string SchemePrefix => new Uri(Endpoint).Scheme;

    /// <summary>请求 URL 前缀（{scheme}://{host}）。</summary>
    internal string UrlPrefix
    {
        get
        {
            var uri = new Uri(Endpoint);
            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }
    }
}
