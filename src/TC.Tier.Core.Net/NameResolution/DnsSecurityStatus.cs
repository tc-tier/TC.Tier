namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// 安全位结论（security-aware stub 契约，RFC 4033 §8——#498 诉求 7）：stub 不做签名验证链
/// （RRSIG/DNSKEY/DS/NSEC·NSEC3 为递归解析器职责，永久 Non-goal），把验证结论位<b>完整外显</b>
/// 即履行客户端契约——<see cref="AuthenticatedData"/> = 递归层已验证的可信标注，消费方按需采信。
/// </summary>
/// <param name="ResponseCode">应答码（NxDomain 为负缓存键——消费方据此判"名不存在"）。</param>
/// <param name="AuthenticatedData">AD 位——应答经递归层 DNSSEC 验证（true = 可信标注，采信与否是消费方决策）。</param>
/// <param name="CheckingDisabled">CD 位——应答在递归层未做验证检查（stub 原样外显）。</param>
public sealed record DnsSecurityStatus(DnsResponseCode ResponseCode, bool AuthenticatedData, bool CheckingDisabled);
