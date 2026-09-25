using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierSet 配置参数（tc-tier-collections-spec §3/§5/§9——Options 只管零件规格，装配归 Builder）。
/// <para>★ record——测试/组合根可用 <c>defaults with { … }</c> 派生。</para>
/// </summary>
public sealed record SetOptions
{
    /// <summary>默认实例名。</summary>
    public const string DefaultName = "tc.set";

    /// <summary>实例名（引擎空间隔离：数据 Ring = {name}.set.ring，点查索引 = {name}.set.index，
    /// 域账 = {name}.set.water——spec §9 文件布局）。</summary>
    public string SetName { get; init; } = DefaultName;

    /// <summary>数据 Ring 段增长上限（字节）。默认 64MB。</summary>
    public long SegmentGrowthLimit { get; init; } = 64L << 20;

    /// <summary>Ring 页大小（字节，2 的幂 [4KB, 1GB]）。默认 1MB。</summary>
    public int PageSize { get; init; } = 1 << 20;

    /// <summary>Ring 内存容量（字节）。默认 64MB（mem 卷物化教训——不照抄 16GB 缺省）。</summary>
    public long MemorySize { get; init; } = 64L << 20;

    /// <summary>溢出策略。默认 Disabled。</summary>
    public OverflowPolicy OverflowPolicy { get; init; } = OverflowPolicy.Disabled;

    /// <summary>值溢出阈值（字节；OverflowPolicy.Enabled 时生效）。</summary>
    public int MinOverflowSize { get; init; }

    /// <summary>冷页缓存比例。默认 0.25。</summary>
    public double ColdReadRatio { get; init; } = 0.25;

    // === 域级 TTL / retention（spec §5——过期粒度 = 整域，定案②）===

    /// <summary>域保留时长（TTL——整域过期消失）。默认 null = 不淘汰。</summary>
    public TimeSpan? DomainTtl { get; init; }

    /// <summary>单域字节上限（envelope 逻辑字节口径）。超限写入 fail-fast。默认 null = 不限。</summary>
    public long? DomainMaxBytes { get; init; }

    /// <summary>TTL 后台扫描间隔。默认 1 分钟。</summary>
    public TimeSpan RetentionScanInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>域容量护栏：已注册域数超限 fail-fast。默认 2²⁰；0 = 不限。</summary>
    public uint DomainCapacity { get; init; } = 1 << 20;

    /// <summary>时钟供给源（TTL 过期判定/last-write 锚走墙钟；retention worker 周期走 provider Delay）。</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
