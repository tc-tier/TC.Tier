using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Storage;
using TC.Tier.Contracts.Transactions;

namespace TC.Tier.Products.TimeSeries;

/// <summary>追加结果——分配的样本地址（地址一等公民：可持久化/传输/点查）。</summary>
/// <param name="Address">追加分配的样本地址。</param>
public readonly record struct SampleAppendResult(LogicalAddress Address);

/// <summary>时序观测快照（GetStatsAsync 产物）。</summary>
/// <param name="FirstTimestamp">最早存活样本时刻（UTC Ticks；空序列 = null）。</param>
/// <param name="LastTimestamp">最新样本时刻（UTC Ticks；空序列 = null）。</param>
/// <param name="SampleCount">当前存活样本数。</param>
/// <param name="IndexEntryCount">时间索引条目数（Indexed=false 降档恒 0）。</param>
/// <param name="TrimmedUntilTimestamp">回收边界（早于此的样本已回收）。</param>
/// <param name="HeadAddress">当前截断头地址（= Ring BeginAddress——retention 占用口径的起点）。</param>
/// <param name="TailAddress">已分配尾地址（含未落盘）。</param>
/// <param name="DurableTail">已落盘水位（= Ring.FlushedUntilAddress）。</param>
public readonly record struct TimeSeriesStats(
    long? FirstTimestamp,
    long? LastTimestamp,
    long SampleCount,
    long IndexEntryCount,
    long TrimmedUntilTimestamp,
    LogicalAddress HeadAddress,
    LogicalAddress TailAddress,
    LogicalAddress DurableTail);

/// <summary>
/// TierTimeSeries 恢复 hints（spec §7——水位/索引全自恢复，无注入项——预留对齐产品 hints 惯例，
/// 形态同 <c>TierQueueRecoveryHints</c>）。
/// </summary>
public readonly struct TimeSeriesRecoveryHints;

/// <summary>
/// TierTimeSeries——时序序列（tc-tier-timeseries-spec：Ring×BTree 组合特化；#443 稠密多序列扩展）。
/// <para>★ 组合配方（spec §1 四件积木）：RingOfTimeKey/RingOfDenseTimeKey（数据真相源）+
///   BTree 时间索引（乱序吸收/范围序/最新点查的正确性必需）+ VersionedMetadata（水位真相源——
///   retention 边界持久）+ Session 域（可选 2PC）。</para>
/// <para>★ 双 API（#443 设计稿 §6）：两参形态 = 默认序列（seriesId 0——单序列模式零迁移；dense
///   模式落默认序列）；三参形态 = 命名序列（dense 实例惰性注册；单序列实例仅 0 合法）。</para>
/// <para>★ 写入（spec §4.1/⑧）：Append 返回 = 内存可见（查询即时可见——BTree 同步插入）；
///   落盘由显式 FlushAsync / retention trim 前置 flush / 水位提交触发（分配-持久分离）。</para>
/// <para>★ 乱序（定案③）：BTree 有序插入天然吸收——无后台排序；同刻多样本不覆盖（写入序交付）。</para>
/// <para>★ retention（spec §5/④）：TTL/字节直接回收（无 fence）；截断先行、水位随后，
///   崩溃窗口由恢复对账以数据事实收口。</para>
/// </summary>
public interface ITierTimeSeries : ILifecycle<TimeSeriesRecoveryHints>, IDisposable, IAsyncDisposable
{
    /// <summary>默认序列标识（两参 API 落点——零迁移；dense 实例中与命名序列同位平等）。</summary>
    public const uint DefaultSeriesId = 0;

    // ══ 写入（多生产者并发安全——Ring tail 串行化；乱序吸收见 spec §4）══

    /// <summary>追加单条样本到默认序列，返回分配地址。</summary>
    /// <param name="timestamp">样本时刻（UTC Ticks）。</param>
    /// <param name="value">样本值字节。</param>
    /// <param name="ct">取消令牌（溢出引擎异步写路径响应）。</param>
    /// <returns>分配的样本地址。</returns>
    /// <exception cref="InvalidOperationException">ts 早于回收边界（TrimmedUntil）或超出 MaxOutOfOrderPast——fail-fast 不静默丢。</exception>
    ValueTask<SampleAppendResult> AppendAsync(long timestamp, ReadOnlyMemory<byte> value, CancellationToken ct);

    /// <summary>追加单条样本到命名序列（dense 实例惰性注册；单序列实例仅 0 合法）。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="timestamp">样本时刻（UTC Ticks）。</param>
    /// <param name="value">样本值字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分配的样本地址。</returns>
    /// <exception cref="InvalidOperationException">守卫违规（同两参）/超出 SeriesCapacity（fail-fast）/单序列实例写非零序列。</exception>
    ValueTask<SampleAppendResult> AppendAsync(uint seriesId, long timestamp, ReadOnlyMemory<byte> value, CancellationToken ct);

    /// <summary>追加一批样本到默认序列（Ring 批量窗口写——地址连续推进；逐条守卫）。</summary>
    /// <param name="samples">样本列表（时刻 + 值）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>各样本的分配地址（与入参一一对应）。</returns>
    /// <exception cref="InvalidOperationException">任一样本违反乱序守卫（批内前面已写入的样本不回滚——调用方按地址取舍）。</exception>
    ValueTask<IReadOnlyList<SampleAppendResult>> AppendBatchAsync(
        IReadOnlyList<(long Timestamp, ReadOnlyMemory<byte> Value)> samples, CancellationToken ct);

    /// <summary>追加一批样本到命名序列（同序列批量窗口写）。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="samples">样本列表（时刻 + 值）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>各样本的分配地址（与入参一一对应）。</returns>
    /// <exception cref="InvalidOperationException">同上；单序列实例仅 0 合法。</exception>
    ValueTask<IReadOnlyList<SampleAppendResult>> AppendBatchAsync(
        uint seriesId, IReadOnlyList<(long Timestamp, ReadOnlyMemory<byte> Value)> samples, CancellationToken ct);

    // ══ 查询（索引驱动——范围序/最新/点查）══

    /// <summary>默认序列范围流：[fromInclusive, toExclusive) 按时间序逐条交付（索引序；冷热透明）。</summary>
    /// <param name="fromInclusive">起点（含）。</param>
    /// <param name="toExclusive">终点（不含）。</param>
    /// <param name="ct">取消令牌（冷区异步回源途中响应）。</param>
    /// <returns>(时刻, 值, 地址) 流——严格 (ts, addr) 字典序。</returns>
    IAsyncEnumerable<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)> RangeAsync(
        long fromInclusive, long toExclusive, CancellationToken ct = default);

    /// <summary>命名序列范围流（dense = 单树键前缀域 seek/迭代——出域即停）。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="fromInclusive">起点（含）。</param>
    /// <param name="toExclusive">终点（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(时刻, 值, 地址) 流。</returns>
    IAsyncEnumerable<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)> RangeAsync(
        uint seriesId, long fromInclusive, long toExclusive, CancellationToken ct = default);

    /// <summary>默认序列最新样本（索引 Backward 语义 O(log n)——定案⑥）；空序列 = null。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(时刻, 值, 地址)；空序列 = null。</returns>
    ValueTask<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)?> LatestAsync(CancellationToken ct);

    /// <summary>命名序列最新样本；空序列 = null。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(时刻, 值, 地址)；空序列 = null。</returns>
    ValueTask<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)?> LatestAsync(uint seriesId, CancellationToken ct);

    /// <summary>默认序列 ≤ ts 的最近样本（Prometheus @ 采样语义）；无 ≤ ts 样本 = null。</summary>
    /// <param name="ts">查询时刻（UTC Ticks，含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(时刻, 值, 地址)；无命中 = null。</returns>
    ValueTask<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)?> FloorAsync(long ts, CancellationToken ct);

    /// <summary>命名序列 ≤ ts 的最近样本；无命中 = null。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="ts">查询时刻（UTC Ticks，含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(时刻, 值, 地址)；无命中 = null。</returns>
    ValueTask<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)?> FloorAsync(uint seriesId, long ts, CancellationToken ct);

    /// <summary>全实例观测快照（dense = 注册表聚合——设计稿 §5.6）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>统计快照。</returns>
    ValueTask<TimeSeriesStats> GetStatsAsync(CancellationToken ct);

    /// <summary>命名序列观测快照（dense = 序列侧账 + 域内首末键；TrimmedUntil = 该序列回收边界）。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>统计快照。</returns>
    ValueTask<TimeSeriesStats> GetStatsAsync(uint seriesId, CancellationToken ct);

    // ══ 水位 / retention ══

    /// <summary>持久回收边界（早于此的样本已回收；重启后由恢复载入/校正）。dense = 默认序列边界。</summary>
    long TrimmedUntilTimestamp { get; }

    /// <summary>已注册序列数（dense = 注册表计数；单序列恒 1）。</summary>
    int SeriesCount { get; }

    /// <summary>已注册序列标识快照（升序；dense = 注册表全体，单序列 = 仅默认序列——备份导出/诊断遍历面）。</summary>
    IEnumerable<uint> SeriesIds { get; }

    /// <summary>显式落盘到当前尾（组提交攒批后的同步点——索引锚点帧随后台策略物化）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask FlushAsync(CancellationToken ct);

    /// <summary>默认序列 retention 推进：回收 ts &lt; beforeTimestampExclusive 的样本（索引清理 → 数据截断 → 水位持久）。</summary>
    /// <param name="beforeTimestampExclusive">回收边界（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回收的样本数。</returns>
    ValueTask<long> TruncateAsync(long beforeTimestampExclusive, CancellationToken ct);

    /// <summary>命名序列 retention 推进（dense = 本序列键域前缀截断；Ring 截断下限由全体序列钉住地址的 min 收口——慢序列钉住）。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="beforeTimestampExclusive">回收边界（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回收的样本数。</returns>
    ValueTask<long> TruncateAsync(uint seriesId, long beforeTimestampExclusive, CancellationToken ct);

    // ══ 地址 / Session 接线 ══

    /// <summary>已分配尾地址（含未落盘）。</summary>
    LogicalAddress TailAddress { get; }

    /// <summary>已落盘水位（= Ring.FlushedUntilAddress）。</summary>
    LogicalAddress DurableTail { get; }

    /// <summary>★ 水位参与者（spec §10——Session 2PC：业务写 + trim 水位同域原子；rollup 管道的事务化形态）。</summary>
    /// <returns>水位 VersionedMetadata 参与者。</returns>
    ITransactionParticipant GetWatermarkParticipant();
}
