namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// DNS 应答码（RFC 1035 §4.1.1 RCODE——#498 客户端面子集；EDNS 扩展码 v1 不消费，DNSSEC 状态码
/// 属签名验证链 Non-goal）。
/// </summary>
public enum DnsResponseCode : byte
{
    /// <summary>无错误。</summary>
    NoError = 0,

    /// <summary>格式错误（服务器无法解析查询）。</summary>
    FormErr = 1,

    /// <summary>服务器失败。</summary>
    ServFail = 2,

    /// <summary>名不存在（负缓存键）。</summary>
    NxDomain = 3,

    /// <summary>未实现。</summary>
    NotImp = 4,

    /// <summary>拒绝。</summary>
    Refused = 5,
}
