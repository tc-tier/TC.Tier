using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierHash 配置参数（tc-tier-collections-spec §3/§5/§9——Options 只管零件规格，装配归 Builder）。
/// <para>★ record——测试/组合根可用 <c>defaults with { … }</c> 派生（对齐 TimeSeriesOptions 先例）。</para>
/// </summary>
public sealed record HashOptions
{
    /// <summary>默认实例名。</summary>
    public const string DefaultName = "tc.hash";

    /// <summary>实例名（引擎空间隔离：数据 Ring = {name}.hash.ring，点查索引 = {name}.hash.index，
    /// 域账 = {name}.hash.water——spec §9 文件布局）。</summary>
    public string HashName { get; init; } = DefaultName;

    /// <summary>数据 Ring 段增长上限（字节）。默认 64MB。</summary>
    public long SegmentGrowthLimit { get; init; } = 64L << 20;

    /// <summary>Ring 页大小（字节，2 的幂 [4KB, 1GB]）。默认 1MB（record 粒度小——TimeSeries 同款理由）。</summary>
    public int PageSize { get; init; } = 1 << 20;

    /// <summary>Ring 内存容量（字节，≥ PageSize 且页数为 2 的幂）。默认 64MB。
    /// ★ 不要照抄 RingSettings 的 16GB 缺省——Ring 首写 EnsureSpace 会 Allocate 整个
    /// PageCount×PageSize 跨度，mem 卷（测试/dev）把它物化成真实内存（2026-08-27 OOM 实锤）。</summary>
    public long MemorySize { get; init; } = 64L << 20;

    /// <summary>溢出策略（超大值分离到溢出引擎——spec §4 产品层零特判）。默认 Disabled（值内联）。</summary>
    public OverflowPolicy OverflowPolicy { get; init; } = OverflowPolicy.Disabled;

    /// <summary>值溢出阈值（字节；OverflowPolicy.Enabled 时生效）。默认 0 = 全部溢出。</summary>
    public int MinOverflowSize { get; init; }

    /// <summary>冷页缓存比例（冷读回源 ClockCache 占页池比例）。默认 0.25（对齐 RingSettings）。</summary>
    public double ColdReadRatio { get; init; } = 0.25;

    // === 域级 TTL / retention（spec §5——TimeSeries §5 模板沿用；过期粒度 = 整域，定案②）===

    /// <summary>域保留时长（TTL——整域过期消失，Redis key TTL 语义）。默认 null = 不淘汰。
    /// <para>★ 过期锚 = 域账 last-write；后台 worker 逐域扫描（<see cref="RetentionScanInterval"/>），
    ///   判定超期 → 整域回收 + 域账注销。per-field/per-member TTL = 非目标（定案②）。</para></summary>
    public TimeSpan? DomainTtl { get; init; }

    /// <summary>单域字节上限（envelope 逻辑字节口径）。超限写入 fail-fast（不静默丢——写侧守卫）。
    /// 默认 null = 不限。</summary>
    public long? DomainMaxBytes { get; init; }

    /// <summary>TTL 后台扫描间隔。默认 1 分钟（低频扫域账）；测试可收紧。</summary>
    public TimeSpan RetentionScanInterval { get; init; } = TimeSpan.FromMinutes(1);

    // === 域容量护栏（spec §9）===

    /// <summary>域容量护栏：已注册域数超限 fail-fast（SeriesCapacity 同款）。默认 2²⁰；0 = 不限。</summary>
    public uint DomainCapacity { get; init; } = 1 << 20;

    /// <summary>时钟供给源（故障注入面——TTL 过期判定/last-write 锚走墙钟；retention worker 周期走 provider Delay）。</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
