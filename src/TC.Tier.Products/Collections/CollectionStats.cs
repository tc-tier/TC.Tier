using TC.Tier.Contracts.Lifecycle;

namespace TC.Tier.Products.Collections;

/// <summary>
/// 集合观测快照（GetStatsAsync 产物——tc-tier-collections-spec §9）。
/// </summary>
/// <param name="DomainId">域标识。</param>
/// <param name="MemberCount">当前存活成员数（Hash=field 数 / ZSet=member 数 / Set=member 数）。</param>
/// <param name="BytesOccupied">域字节占用（envelope 逻辑字节口径——非 Ring 物理开销）。</param>
/// <param name="LastWriteTimestamp">域最近写入时刻（UTC Ticks；未写入 = null）。</param>
/// <param name="TtlBoundaryTimestamp">TTL 过期边界（lastWrite + DomainTtl；未配 TTL 或未写入 = null）。</param>
public readonly record struct CollectionStats(
    uint DomainId,
    long MemberCount,
    long BytesOccupied,
    long? LastWriteTimestamp,
    long? TtlBoundaryTimestamp);

/// <summary>
/// 集合家族恢复 hints（spec §7——水位/索引全自恢复，无注入项——预留对齐产品 hints 惯例，
/// 形态同 <c>TimeSeriesRecoveryHints</c>；三产品共用——hints 为空结构零语义差异）。
/// </summary>
public readonly struct CollectionRecoveryHints;
