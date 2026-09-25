using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Transactions;

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierHash——域级哈希表（field → value map，tc-tier-collections-spec：Ring×HashIndex 组合特化）。
/// <para>★ 组合配方（spec §1 四件积木）：RingOfHashKey（数据真相源——record key 只做分类，
///   envelope 自述完整域/field/值）+ HashOfHashKey（点查索引——(域, field 哈希) → record 地址）+
///   VersionedMetadata（域账真相源——last-write/TTL 锚/计数 keyed 块）+ Session 域（可选 2PC）。</para>
/// <para>★ 双 API（spec §3，TimeSeries 先例）：两参形态 = 默认域（domain 0 落点）；三参形态 = 命名域
///   （dense-only 内核——单实例多命名域共享 Ring 页池/索引树/水位文件，惰性注册）。</para>
/// <para>★ 写入语义（spec §4，Redis 对齐）：HSet 覆盖旧值返回新增判定（覆盖 = false）；
///   HIncrBy 对不存在字段从 0 起算；判重/覆盖的真相源 = Hash 索引点查。写操作全程单闸串行
///   （spec §6——第一版单闸够用）。</para>
/// <para>★ 写入返回 = 内存可见（查询即时可见——索引同步插入）；落盘由显式 FlushAsync 触发
///   （分配-持久分离，TimeSeries 同款）。</para>
/// </summary>
public interface ITierHash : ILifecycle<CollectionRecoveryHints>, IDisposable, IAsyncDisposable
{
    /// <summary>默认域标识（两参 API 落点——零迁移；与命名域同位平等）。</summary>
    public const uint DefaultDomainId = 0;

    // ══ 写入（Redis HSET/HDEL/HINCRBY 对齐——判重/覆盖真相源 = Hash 索引点查）══

    /// <summary>设置 field 值（默认域）。已存在 = 覆盖旧值（Ring append-only——旧 record 由域界截断回收）。</summary>
    /// <param name="field">field 字节（任意字节串——16B 强哈希入键，envelope 自述完整字节）。</param>
    /// <param name="value">值字节。</param>
    /// <param name="ct">取消令牌（溢出引擎异步写路径响应）。</param>
    /// <returns>true = 新增 field；false = 覆盖既有 field（Redis HSET 返回值语义）。</returns>
    ValueTask<bool> HSetAsync(ReadOnlyMemory<byte> field, ReadOnlyMemory<byte> value, CancellationToken ct);

    /// <summary>设置 field 值（命名域——惰性注册；容量护栏 fail-fast）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="field">field 字节。</param>
    /// <param name="value">值字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 新增 field；false = 覆盖既有 field。</returns>
    ValueTask<bool> HSetAsync(uint domain, ReadOnlyMemory<byte> field, ReadOnlyMemory<byte> value, CancellationToken ct);

    /// <summary>整数值原子自增（默认域；不存在字段 = 从 0 起算——Redis HINCRBY 语义）。
    /// 值档 = 8B 小端 int64（非 8B 既有值 fail-fast；溢出 fail-fast）。</summary>
    /// <param name="field">field 字节。</param>
    /// <param name="delta">增量（可为负）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>自增后的值。</returns>
    ValueTask<long> HIncrByAsync(ReadOnlyMemory<byte> field, long delta, CancellationToken ct);

    /// <summary>整数值原子自增（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="field">field 字节。</param>
    /// <param name="delta">增量（可为负）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>自增后的值。</returns>
    ValueTask<long> HIncrByAsync(uint domain, ReadOnlyMemory<byte> field, long delta, CancellationToken ct);

    // ══ 查询 ══

    /// <summary>取 field 值（默认域）；field 哈希命中但字节不匹配（16B 碰撞）= fail-fast。</summary>
    /// <param name="field">field 字节。</param>
    /// <param name="ct">取消令牌（冷区异步回源途中响应）。</param>
    /// <returns>值字节（拷贝交付）；field 不存在 = null。</returns>
    ValueTask<ReadOnlyMemory<byte>?> HGetAsync(ReadOnlyMemory<byte> field, CancellationToken ct);

    /// <summary>取 field 值（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="field">field 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>值字节（拷贝交付）；field 不存在 = null。</returns>
    ValueTask<ReadOnlyMemory<byte>?> HGetAsync(uint domain, ReadOnlyMemory<byte> field, CancellationToken ct);

    /// <summary>批量删 field（默认域）。</summary>
    /// <param name="fields">field 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际删除的 field 数（不存在的 field 不计——Redis HDEL 返回值语义）。</returns>
    ValueTask<long> HDelAsync(IReadOnlyList<ReadOnlyMemory<byte>> fields, CancellationToken ct);

    /// <summary>批量删 field（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="fields">field 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际删除的 field 数（不存在的 field 不计）。</returns>
    ValueTask<long> HDelAsync(uint domain, IReadOnlyList<ReadOnlyMemory<byte>> fields, CancellationToken ct);

    /// <summary>field 存在性（默认域）。</summary>
    /// <param name="field">field 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 存在。</returns>
    ValueTask<bool> HExistsAsync(ReadOnlyMemory<byte> field, CancellationToken ct);

    /// <summary>field 存在性（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="field">field 字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 存在。</returns>
    ValueTask<bool> HExistsAsync(uint domain, ReadOnlyMemory<byte> field, CancellationToken ct);

    /// <summary>域内存活 field 数（默认域；未注册域 = 0——Redis HLEN 语义）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>field 数。</returns>
    ValueTask<long> HLenAsync(CancellationToken ct);

    /// <summary>域内存活 field 数（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>field 数。</returns>
    ValueTask<long> HLenAsync(uint domain, CancellationToken ct);

    /// <summary>全域 field→value 流（默认域；交付序 = Ring 地址序——Redis HGETALL 无序契约）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(field, value) 流。</returns>
    IAsyncEnumerable<(byte[] Field, byte[] Value)> HGetAllAsync(CancellationToken ct);

    /// <summary>全域 field→value 流（命名域）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(field, value) 流。</returns>
    IAsyncEnumerable<(byte[] Field, byte[] Value)> HGetAllAsync(uint domain, CancellationToken ct);

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

    /// <summary>显式落盘到当前尾（组提交攒批后的同步点）+ 脏域账持久。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask FlushAsync(CancellationToken ct);

    /// <summary>★ 域账参与者（spec §10——Session 2PC：业务写 + 域账同域原子）。</summary>
    /// <returns>域账 VersionedMetadata 参与者。</returns>
    ITransactionParticipant GetWatermarkParticipant();
}
