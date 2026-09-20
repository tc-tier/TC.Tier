namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// DNS 资源记录类型（RFC 1035 §3.2.2 + RFC 2782/6891——#498 客户端面子集）。
/// </summary>
public enum DnsRecordType : ushort
{
    /// <summary>IPv4 地址（RFC 1035 §3.4.1）。</summary>
    A = 1,

    /// <summary>别名（RFC 1035 §3.3.1——解析跟随，深度上限 8）。</summary>
    CNAME = 5,

    /// <summary>权威区起点（RFC 1035 §3.3.13——负缓存 TTL 参考面，v1 不外显）。</summary>
    SOA = 6,

    /// <summary>指针（反向解析）。</summary>
    PTR = 12,

    /// <summary>文本记录。</summary>
    TXT = 16,

    /// <summary>IPv6 地址（RFC 3596）。</summary>
    AAAA = 28,

    /// <summary>服务定位（RFC 2782——priority/weight/port/target，服务发现主形态）。</summary>
    SRV = 33,

    /// <summary>EDNS0 伪记录（RFC 6891——载荷协商 + DO 位；不入记录集，仅编解码面）。</summary>
    OPT = 41,
}
