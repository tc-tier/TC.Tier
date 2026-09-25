using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Transactions;

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierSet——去重集合（member 唯一无值，tc-tier-collections-spec：Ring×HashIndex 组合特化）。
/// <para>★ 组合配方（spec §1）：RingOfSetKey（数据真相源）+ HashOfSetKey（判重/点查索引）+
/// VersionedMetadata（域账）+ Session 域（可选 2PC）。</para>
/// <para>★ 双 API（spec §3）：两参形态 = 默认域（domain 0）；三参形态 = 命名域（惰性注册）。</para>
/// <para>★ 写入语义（spec §4，Redis 对齐）：SAdd 幂等——已存在成员 no-op 返回新增数；
/// SRem 不存在成员不计。</para>
/// </summary>
public interface ITierSet : ILifecycle<CollectionRecoveryHints>, IDisposable, IAsyncDisposable
{
    /// <summary>默认域标识（两参 API 落点）。</summary>
    public const uint DefaultDomainId = 0;

    // ══ 写入（Redis SADD/SREM 对齐——判重真相源 = Hash 索引点查）══

    /// <summary>批量添加成员（默认域）；已存在成员 no-op（幂等）。</summary>
    /// <param name="members">成员集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际新增数（已存在不计——Redis SADD 返回值语义）。</returns>
    ValueTask<long> SAddAsync(IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct);

    /// <summary>批量添加成员（命名域——惰性注册；容量护栏 fail-fast）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="members">成员集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际新增数。</returns>
    ValueTask<long> SAddAsync(uint domain, IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct);

    /// <summary>批量移除成员（默认域）。</summary>
    /// <param name="members">成员集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际移除数（不存在不计——Redis SREM 语义）。</returns>
    ValueTask<long> SRemAsync(IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct);

    /// <summary>批量移除成员（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="members">成员集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际移除数。</returns>
    ValueTask<long> SRemAsync(uint domain, IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct);

    // ══ 查询 ══

    /// <summary>成员存在性（默认域）；哈希命中但字节不匹配（16B 碰撞）= fail-fast。</summary>
    /// <param name="member">成员字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 存在。</returns>
    ValueTask<bool> SIsMemberAsync(ReadOnlyMemory<byte> member, CancellationToken ct);

    /// <summary>成员存在性（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="member">成员字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 存在。</returns>
    ValueTask<bool> SIsMemberAsync(uint domain, ReadOnlyMemory<byte> member, CancellationToken ct);

    /// <summary>域成员数（默认域；未注册域 = 0——Redis SCARD 语义）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成员数。</returns>
    ValueTask<long> SCardAsync(CancellationToken ct);

    /// <summary>域成员数（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成员数。</returns>
    ValueTask<long> SCardAsync(uint domain, CancellationToken ct);

    /// <summary>全域成员流（默认域；交付序 = Ring 地址序——Redis SMEMBERS 无序契约）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成员流。</returns>
    IAsyncEnumerable<byte[]> SMembersAsync(CancellationToken ct);

    /// <summary>全域成员流（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成员流。</returns>
    IAsyncEnumerable<byte[]> SMembersAsync(uint domain, CancellationToken ct);

    // ══ 家族公共面（spec §3）══

    /// <summary>已注册域数。</summary>
    int DomainCount { get; }

    /// <summary>已注册域标识快照（升序——导出/诊断遍历面）。</summary>
    IEnumerable<uint> DomainIds { get; }

    /// <summary>域观测快照（成员数/字节占用/last-write/TTL 边界——spec §9）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>统计快照（未注册域 = 全零快照）。</returns>
    ValueTask<CollectionStats> GetStatsAsync(uint domain, CancellationToken ct);

    /// <summary>域整域回收（手动档——索引域界清理 + Ring 截断下限收口 + 域账注销持久）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回收的成员数（未注册域 = 0）。</returns>
    ValueTask<long> TruncateDomainAsync(uint domain, CancellationToken ct);

    /// <summary>显式落盘到当前尾 + 脏域账持久。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask FlushAsync(CancellationToken ct);

    /// <summary>★ 域账参与者（spec §10）。</summary>
    /// <returns>域账 VersionedMetadata 参与者。</returns>
    ITransactionParticipant GetWatermarkParticipant();
}
