using System.Net;

namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// DNS stub resolver 选项（#498——零外部依赖叶子位的全部旋钮；缺省值即 K8s CoreDNS 兼容形态）。
/// <para>★ 名字即全部：全部具名属性、无选项回调；<c>null</c> = 操作系统自举（resolv.conf / 网卡配置，
/// 见 <see cref="DnsStubResolver"/>）。</para>
/// </summary>
public sealed record DnsStubResolverOptions
{
    /// <summary>DNS 服务器（null = OS 自举；非空 = 显式全量替换，轮询容错按列表序循环）。</summary>
    public IReadOnlyList<IPEndPoint>? Servers { get; init; }

    /// <summary>单次收发超时（缺省 2s；重试间指数退避自此推导）。</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>额外重试次数（缺省 2——总尝试 = 1 + <see cref="Retries"/>；每次尝试轮换下一 server）。</summary>
    public int Retries { get; init; } = 2;

    /// <summary>TTL 缓存容量（LRU 条目上限，缺省 4096；0 = 关缓存）。</summary>
    public int CacheCapacity { get; init; } = 4096;

    /// <summary>负缓存 TTL（NXDOMAIN——缺省 30s；RFC 2308 名义上取 SOA MINIMUM，v1 固定值）。</summary>
    public TimeSpan NegativeCacheTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>EDNS0 声明接收载荷（RFC 6891，缺省 4096——SRV 多实例应答常超经典 512B）。</summary>
    public int EdnsPayloadSize { get; init; } = 4096;

    /// <summary>请求置 DO 位（RFC 4035 §4.3——需要 DNSSEC 记录面时置；AD 结论消费方另按需采信）。</summary>
    public bool RequestDnsSecOk { get; init; }

    /// <summary>search 域列表（null = OS 自举——resolv.conf search/domain / Windows 主 DNS 后缀）。</summary>
    public IReadOnlyList<string>? SearchDomains { get; init; }

    /// <summary>ndots 阈值（null = OS 自举；缺省 1——相对名点数 ≥ 阈值先绝对后 search，否则先 search 后绝对）。</summary>
    public int? NdotsOverride { get; init; }

    /// <summary>组合合法性校验——非法抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public void Validate()
    {
        if (QueryTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(QueryTimeout), QueryTimeout, "QueryTimeout 必须 > 0");
        if ((uint)Retries > 8)
            throw new ArgumentOutOfRangeException(nameof(Retries), Retries, "Retries 必须在 [0,8]");
        if ((uint)CacheCapacity > 1 << 20)
            throw new ArgumentOutOfRangeException(nameof(CacheCapacity), CacheCapacity, "CacheCapacity 必须在 [0,2^20]");
        if ((uint)EdnsPayloadSize is < 512 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(EdnsPayloadSize), EdnsPayloadSize, "EdnsPayloadSize 必须在 [512,65535]");
        if (NdotsOverride is { } n && n is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(NdotsOverride), n, "NdotsOverride 必须在 [0,15]");
    }
}
