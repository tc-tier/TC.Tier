using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// TierTimeSeries 配置参数（tc-tier-timeseries-spec §3——Options 只管零件规格，装配归 Builder）。
/// <para>★ record——测试/组合根可用 <c>defaults with { … }</c> 派生（对齐 SortedIndexPersistencePolicy 先例）。</para>
/// </summary>
public sealed record TimeSeriesOptions
{
    /// <summary>默认序列名。</summary>
    public const string DefaultName = "tc.series";

    /// <summary>序列名（引擎空间隔离：数据 Ring = {name}.ts.ring，时间索引 = {name}.ts.index，
    /// 水位 = {name}.ts.water）。</summary>
    public string SeriesName { get; init; } = DefaultName;

    /// <summary>数据 Ring 段增长上限（字节）。默认 64MB。</summary>
    public long SegmentGrowthLimit { get; init; } = 64L << 20;

    /// <summary>Ring 页大小（字节，2 的幂 [4KB, 1GB]）。默认 1MB——样本 record 粒度小，
    /// 小页 = 更细的环绕/驱逐粒度（spec §3：对齐 Queue 惯例）。</summary>
    public int PageSize { get; init; } = 1 << 20;

    /// <summary>Ring 内存容量（字节，≥ PageSize 且页数为 2 的幂）。默认 64MB。
    /// ★ 不要照抄 RingSettings 的 16GB 缺省——Ring 首写 EnsureSpace 会 Allocate 整个
    /// PageCount×PageSize 跨度，mem 卷（测试/dev）把它物化成真实内存（2026-08-27 OOM 实锤）。</summary>
    public long MemorySize { get; init; } = 64L << 20;

    /// <summary>溢出策略（超大值分离到溢出引擎）。默认 Disabled（值内联）。</summary>
    public OverflowPolicy OverflowPolicy { get; init; } = OverflowPolicy.Disabled;

    /// <summary>值溢出阈值（字节；OverflowPolicy.Enabled 时生效）。默认 0 = 全部溢出。</summary>
    public int MinOverflowSize { get; init; }

    /// <summary>冷页缓存比例（冷读回源 ClockCache 占页池比例）。默认 0.25（对齐 RingSettings）。</summary>
    public double ColdReadRatio { get; init; } = 0.25;

    // === retention（spec §5——TTL/字节直接回收，无 fence）===

    /// <summary>样本保留时长（TTL）。默认 7 天；null = 不按时间回收（仅手动 TruncateAsync）。</summary>
    public TimeSpan? RetentionTime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>序列字节上限（Ring 头尾距离）。超限按索引序反查时间锚回收（spec §5 同路）。
    /// null = 不限（默认）。</summary>
    public long? MaxBytes { get; init; }

    /// <summary>retention 后台扫描间隔。默认 1 分钟（低频扫水位）；测试可收紧。</summary>
    public TimeSpan RetentionScanInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>乱序下界保护（spec §4.1——样本 ts 早于 now - MaxOutOfOrderPast 拒绝，
    /// 防单条老样本钉住索引/截断；锚定同 Queue MaxDelay）。null = 不限（默认）。</summary>
    public TimeSpan? MaxOutOfOrderPast { get; init; }

    // === 时间索引（spec §1/§4.3——正确性必需；Indexed=false 是显式降档契约）===

    /// <summary>时间索引开关。默认 true（索引序交付——乱序吸收/范围序/最新点查）；
    /// false = 纯追加降档（Ring 地址序交付——仅写序=时间序的场景，定案⑨显式契约）。</summary>
    public bool Indexed { get; init; } = true;

    /// <summary>BTree 节点大小（字节）。默认 256，样本密集时可增大以减少树高。</summary>
    public int IndexNodeSize { get; init; } = 256;

    /// <summary>时间索引锚点帧持久化策略（后台 dump 间隔/条目增量阈值——任一命中触发；
    /// null = BTree 缺省（30 秒/1 万条）。恢复时载帧 + 增量重放，帧无效 fail-safe 全量重放）。</summary>
    public SortedIndexPersistencePolicy? IndexPersistencePolicy { get; init; }

    // === 稠密多序列（#443 设计稿——DenseTimeKey 20B 单实例共享面）===

    /// <summary>稠密多序列模式开关（设计稿 §7 裁决点 1——同一 TierTimeSeries 类型内部分支）。
    /// true = DenseTimeKey（20B 复合键）+ TTS2 envelope + 单树键域化 + 水位 keyed 块化；
    /// false（默认）= TimeKey（16B）单序列模式——既有行为零迁移。两模式互不读对方文件。</summary>
    public bool DenseSeries { get; init; }

    /// <summary>序列容量护栏（dense 模式——设计稿 §7 裁决点 4）：已注册序列数超限 fail-fast 抛
    /// （水位块尺寸护栏；动态扩容后置）。默认 2²⁰ ≈ 10⁶（10⁵ 需求 + 1 个数量级余量）。</summary>
    public uint SeriesCapacity { get; init; } = 1 << 20;

    /// <summary>时钟供给源（故障注入面 件一——时钟缝 P1 落点；缺省 <see cref="TimeProvider.System"/> 行为零变化）。
    /// <para>写入乱序下界与保留扫描 cutoff 走墙钟（跳变可注入）；保留 worker 周期走 provider Delay。</para></summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
