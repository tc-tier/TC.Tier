using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Transactions;

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierZSet——score 有序集（member 唯一 + double score 全序，tc-tier-collections-spec：
/// Ring×HashIndex×BTree 三件组合特化）。
/// <para>★ 组合配方（spec §1/定案③）：RingOfZSetKey（数据真相源）+ HashOfZSetKey（member→score
/// 点查索引）+ BTreeOfZScoreKey（score 有序视图——(域, score 编码, member 哈希) 全序）+
/// VersionedMetadata（域账）+ Session 域（可选 2PC）。</para>
/// <para>★ 双 API（spec §3）：两参形态 = 默认域（domain 0）；三参形态 = 命名域（惰性注册）。</para>
/// <para>★ 写入语义（spec §4，Redis 对齐）：ZAdd 覆盖 = 更新 score（删旧 ZScoreKey + 插新——
/// 双索引一致性窗口由恢复对账收口）；ZIncrBy 对不存在成员 = 新增（delta 即初值）；
/// ZPopMin/ZPopMax 原子取极值（延迟队列消费面刚需——ZRANGE+ZREM 两步非原子）。</para>
/// </summary>
public interface ITierZSet : ILifecycle<CollectionRecoveryHints>, IDisposable, IAsyncDisposable
{
    /// <summary>默认域标识（两参 API 落点）。</summary>
    public const uint DefaultDomainId = 0;

    // ══ 写入（Redis ZADD/ZINCRBY/ZREM 对齐）══

    /// <summary>添加/更新成员（默认域）。已存在 = 更新 score（删旧有序键插新）。</summary>
    /// <param name="score">分数（NaN fail-fast；±Inf 合法）。</param>
    /// <param name="member">member 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新增数 0/1（覆盖 = 0——Redis ZADD 缺省口径，CH 选项后置）。</returns>
    ValueTask<long> ZAddAsync(double score, ReadOnlyMemory<byte> member, CancellationToken ct);

    /// <summary>添加/更新成员（命名域——惰性注册；容量护栏 fail-fast）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="score">分数。</param>
    /// <param name="member">member 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新增数 0/1。</returns>
    ValueTask<long> ZAddAsync(uint domain, double score, ReadOnlyMemory<byte> member, CancellationToken ct);

    /// <summary>分数原子增量（默认域；不存在成员 = 新增，delta 即初值——Redis ZINCRBY 语义）。</summary>
    /// <param name="delta">增量（可为负；结果 NaN fail-fast）。</param>
    /// <param name="member">member 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>增量后的分数。</returns>
    ValueTask<double> ZIncrByAsync(double delta, ReadOnlyMemory<byte> member, CancellationToken ct);

    /// <summary>分数原子增量（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="delta">增量。</param>
    /// <param name="member">member 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>增量后的分数。</returns>
    ValueTask<double> ZIncrByAsync(uint domain, double delta, ReadOnlyMemory<byte> member, CancellationToken ct);

    /// <summary>批量移除成员（默认域）。</summary>
    /// <param name="members">member 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际移除数（不存在的成员不计——Redis ZREM 语义）。</returns>
    ValueTask<long> ZRemAsync(IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct);

    /// <summary>批量移除成员（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="members">member 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际移除数。</returns>
    ValueTask<long> ZRemAsync(uint domain, IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct);

    // ══ 查询 ══

    /// <summary>取成员分数（默认域）；member 哈希命中但字节不匹配（16B 碰撞）= fail-fast。</summary>
    /// <param name="member">member 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分数；成员不存在 = null。</returns>
    ValueTask<double?> ZScoreAsync(ReadOnlyMemory<byte> member, CancellationToken ct);

    /// <summary>取成员分数（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="member">member 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分数；成员不存在 = null。</returns>
    ValueTask<double?> ZScoreAsync(uint domain, ReadOnlyMemory<byte> member, CancellationToken ct);

    /// <summary>分数区间成员数（默认域；[min, max] 双闭——Redis ZCOUNT 语义；min &gt; max = 0）。</summary>
    /// <param name="min">下界（含）。</param>
    /// <param name="max">上界（含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>区间成员数。</returns>
    ValueTask<long> ZCountAsync(double min, double max, CancellationToken ct);

    /// <summary>分数区间成员数（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="min">下界（含）。</param>
    /// <param name="max">上界（含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>区间成员数。</returns>
    ValueTask<long> ZCountAsync(uint domain, double min, double max, CancellationToken ct);

    /// <summary>域成员数（默认域；未注册域 = 0——Redis ZCARD 语义）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成员数。</returns>
    ValueTask<long> ZCardAsync(CancellationToken ct);

    /// <summary>域成员数（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成员数。</returns>
    ValueTask<long> ZCardAsync(uint domain, CancellationToken ct);

    /// <summary>按排名升序区间流（默认域；[start, stop] 双闭位序——0 = 最低分；负数自尾部数
    /// (-1 = 最高分)——Redis ZRANGE 语义；交付序 = 分数升序、同分按 member 哈希序）。</summary>
    /// <param name="start">起始排名（含）。</param>
    /// <param name="stop">结束排名（含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score) 流。</returns>
    IAsyncEnumerable<(byte[] Member, double Score)> ZRangeAsync(long start, long stop, CancellationToken ct);

    /// <summary>按排名升序区间流（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="start">起始排名（含）。</param>
    /// <param name="stop">结束排名（含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score) 流。</returns>
    IAsyncEnumerable<(byte[] Member, double Score)> ZRangeAsync(uint domain, long start, long stop, CancellationToken ct);

    /// <summary>按排名降序区间流（默认域——0 = 最高分；TryGetMax + TryGetPrev 反向步进，波次 0b 原语）。</summary>
    /// <param name="start">起始排名（含，降序位序）。</param>
    /// <param name="stop">结束排名（含，降序位序）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score) 流。</returns>
    IAsyncEnumerable<(byte[] Member, double Score)> ZRevRangeAsync(long start, long stop, CancellationToken ct);

    /// <summary>按排名降序区间流（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="start">起始排名（含）。</param>
    /// <param name="stop">结束排名（含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score) 流。</returns>
    IAsyncEnumerable<(byte[] Member, double Score)> ZRevRangeAsync(uint domain, long start, long stop, CancellationToken ct);

    /// <summary>分数区间流（默认域；[min, max] 双闭升序——Redis ZRANGEBYSCORE 缺省口径，
    /// LIMIT 窗口选项后置 定案⑥）。</summary>
    /// <param name="min">下界（含）。</param>
    /// <param name="max">上界（含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score) 流。</returns>
    IAsyncEnumerable<(byte[] Member, double Score)> ZRangeByScoreAsync(double min, double max, CancellationToken ct);

    /// <summary>分数区间流（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="min">下界（含）。</param>
    /// <param name="max">上界（含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score) 流。</returns>
    IAsyncEnumerable<(byte[] Member, double Score)> ZRangeByScoreAsync(uint domain, double min, double max, CancellationToken ct);

    /// <summary>原子取最小并移除（默认域；空域 = null——延迟队列消费面原子性刚需）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score)；空域 = null。</returns>
    ValueTask<(byte[] Member, double Score)?> ZPopMinAsync(CancellationToken ct);

    /// <summary>原子取最小并移除（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score)；空域 = null。</returns>
    ValueTask<(byte[] Member, double Score)?> ZPopMinAsync(uint domain, CancellationToken ct);

    /// <summary>原子取最大并移除（默认域——定案③ TryGetMax + TryGetPrev 反向步进形态）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score)；空域 = null。</returns>
    ValueTask<(byte[] Member, double Score)?> ZPopMaxAsync(CancellationToken ct);

    /// <summary>原子取最大并移除（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(member, score)；空域 = null。</returns>
    ValueTask<(byte[] Member, double Score)?> ZPopMaxAsync(uint domain, CancellationToken ct);

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

    /// <summary>域整域回收（手动档——双索引域界清理 + Ring 截断下限收口 + 域账注销持久）。</summary>
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
