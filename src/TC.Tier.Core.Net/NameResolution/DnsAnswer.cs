using System.Collections.Immutable;
using System.Net;

namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// 查询结果——记录集 + 聚合 MinTtl + 安全位结论（#498 诉求 6/7）。
/// <para>★ 聚合 MinTtl：CNAME 跟随链各跳 TTL 取最小（缓存有效期的权威口径）；记录集为空且
/// <see cref="DnsSecurityStatus.ResponseCode"/>=<see cref="DnsResponseCode.NxDomain"/> = 负缓存形态。</para>
/// <para>★ 确定性：同应答字节 → 同结果（记录保持应答节顺序，零顺序扰动）。</para>
/// </summary>
/// <param name="Name">发起查询名（原始输入名——非链中末跳）。</param>
/// <param name="Type">查询记录类型。</param>
/// <param name="SrvRecords">SRV 记录集（<see cref="DnsRecordType.SRV"/> 查询非空）。</param>
/// <param name="Addresses">地址记录集（A/AAAA 查询非空；CNAME 跟随到终点后填充）。</param>
/// <param name="Aliases">跟随链（按序——空 = 无 CNAME 跟随）。</param>
/// <param name="TimeToLive">聚合 MinTtl（链上各跳最小值）。</param>
/// <param name="Security">安全位结论。</param>
public sealed record DnsAnswer(
    string Name,
    DnsRecordType Type,
    ImmutableArray<DnsSrvRecord> SrvRecords,
    ImmutableArray<IPAddress> Addresses,
    ImmutableArray<string> Aliases,
    TimeSpan TimeToLive,
    DnsSecurityStatus Security)
{
    /// <summary>空应答工厂（负缓存/无数据形态——记录集全空，TTL = 负缓存值）。</summary>
    public static DnsAnswer Empty(string name, DnsRecordType type, TimeSpan ttl, DnsSecurityStatus security)
        => new(name, type, ImmutableArray<DnsSrvRecord>.Empty, ImmutableArray<IPAddress>.Empty,
            ImmutableArray<string>.Empty, ttl, security);
}
