namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 配置（不可变 record + With 链——装配惯例同 StorageEngineOptions）。
/// </summary>
public sealed record TierWalOptions
{
    /// <summary>默认配置实例——全部配置项取声明缺省值（Managed meta 策略、16KB opaque 区、组提交三维度缺省）。</summary>
    public static TierWalOptions Default { get; } = new();

    /// <summary>引擎子目录名。</summary>
    public string WalName { get; private init; } = "tier-wal";

    /// <summary>段上限（引擎段生长上限——TierWAL 自管段 anchor 表按引擎段记录）。</summary>
    public long SegmentGrowthLimit { get; private init; } = 256L * 1024 * 1024;

    /// <summary>★ 默认回落最优模式（零决策设计）：Managed（独立 .meta 引擎、恒单段、单槽覆盖原子语义、O(1) 恢复）。</summary>
    public MetaPolicyKind MetaPolicyKind { get; private init; } = MetaPolicyKind.Managed;

    /// <summary>
    /// ★ opaque 容量 TierWAL 自配（配置无固定上限）——默认 16KB。
    /// <para>容器布局 = [TailIndex 8B][HeadIndex 8B][段表条目数 4B][pad 4B][段 anchor 表 N×24B][raft 元数据预留区]；
    ///   段表容量 = (MetaOpaqueBytes − 24 − raft 区字节) / 24；raft 预留区 = opaque 剩余（配置表达）。</para>
    /// <para>★ 底层 Settings 基类默认 0——TierWAL 必须显式配置 &gt; 0（0 = 无 opaque 区，搭车通道不可用）。</para>
    /// </summary>
    public int MetaOpaqueBytes { get; private init; } = 16 * 1024;

    /// <summary>IO hints（默认组提交；WriteThrough 显式选）。</summary>
    public FileOpenHints Hints { get; private init; } = FileOpenHints.None;

    // === 组提交三维度（两提交形态由配置表达）===

    /// <summary>时间维度（距上次提交）。默认 10ms；-1ms 禁用时间维度。</summary>
    public TimeSpan CommitInterval { get; private init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>数据量维度（未提交字节 ≥ 此值触发）。默认 64KB。</summary>
    public long MaxUnflushedBytes { get; private init; } = AlignmentConst.Alignment64K;

    /// <summary>条数维度（未提交条数 ≥ 此值触发）。默认 1000。</summary>
    public int MaxUnflushedCount { get; private init; } = 1000;

    // ★ 单条提交形态 = 三维度全 0（每次 Append 即触发提交）；显式同步点仍可随时调 ITierWal.CommitAsync
    //   （策略自动提交与显式提交并存）。

    /// <summary>With 链——换名。</summary>
    public TierWalOptions WithWalName(string name) => this with { WalName = name };

    /// <summary>With 链——段上限。</summary>
    public TierWalOptions WithSegmentGrowthLimit(long limit) => this with { SegmentGrowthLimit = limit };

    /// <summary>With 链——meta 策略（Managed/Transport/Disabled）。</summary>
    public TierWalOptions WithMetaPolicyKind(MetaPolicyKind kind) => this with { MetaPolicyKind = kind };

    /// <summary>With 链——opaque 容器容量（段表 + raft 元数据共用）。</summary>
    public TierWalOptions WithMetaOpaqueBytes(int bytes) => this with { MetaOpaqueBytes = bytes };

    /// <summary>With 链——IO hints。</summary>
    public TierWalOptions WithHints(FileOpenHints hints) => this with { Hints = hints };

    /// <summary>With 链——组提交时间维度。</summary>
    public TierWalOptions WithCommitInterval(TimeSpan interval) => this with { CommitInterval = interval };

    /// <summary>With 链——组提交数据量维度。</summary>
    public TierWalOptions WithMaxUnflushedBytes(long bytes) => this with { MaxUnflushedBytes = bytes };

    /// <summary>With 链——组提交条数维度。</summary>
    public TierWalOptions WithMaxUnflushedCount(int count) => this with { MaxUnflushedCount = count };

    /// <summary>完整 builder——注入面开放 + StartAsync 一步到位。</summary>
    public TierWalBuilder Builder(IFileSystem fs) => new(fs, this);
}
