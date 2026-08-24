namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// <see cref="EntryLog"/> 专属配置——通用 entry 顺序日志（WAL 典型用途）。
/// <para>★ group commit 三维度阈值（EntryLog 的核心持久性调度，见 Structures-log-rewrite-design.md §0.6）。</para>
/// <para>★ group commit 建立在引擎组合之上（默认 Mode C：DIO + None）：</para>
/// <para>Append 攒批（pwrite 到 CommittedTail，未 fsync）→ 三维度阈值任一满足触发提交执行链：</para>
/// <para>  engine.Flush(commitTail) 落盘 → CommittedOffset = commitTail → meta.Commit() 记录边界。</para>
/// <para>★ 崩溃窗口 = 提交触发间隔内的写入量（持久性底线）。</para>
/// <para>★ 三场景靠阈值配置表达（无需枚举选模式）：</para>
/// <para> - 典型 WAL（默认 1ms/64KB/1000）：三维度兜底。</para>
/// <para> - 单条强制（全设 0）：每次 Append 立即触发；宜配 PersistenceMode.WriteThrough（Mode D，逐写已落盘）。</para>
/// <para> - 手动/2PC（Interval=InfiniteTimeSpan + 阈值很大）：不自动提交，靠 CommitAsync / TransactionLog 驱动。</para>
/// <para>★ 三维度阈值仅用于构造默认 <see cref="GroupCommitPolicy"/>（EntryLog 构造 commitPolicy 参数为 null 时启用）。</para>
/// </summary>
public sealed class EntryLogSettings : LogSettings
{
    /// <summary>完整构造——注入主引擎选项。</summary>
    public EntryLogSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }
    /// <summary>
    /// 默认构造——仅指定日志文件名（默认 group commit 阈值）。
    /// </summary>
    /// <param name="name">日志文件名。</param>
    public EntryLogSettings(string name = "tc.log") : base(name)
    {
    }

    /// <summary>
    /// group 提交时间阈值（距上次提交 ≥ 此间隔触发）。默认 10ms。
    /// <para>★ 持久性底线：即使上层完全不调 Commit，Interval 到期后台循环必触发提交。</para>
    /// <para>★ 默认 10ms 是稳定平衡点（崩溃窗口 ≤10ms，吞吐不退化）：
    /// 1ms 过于激进（实测仅 0.8 MB/s，每毫秒同步 fsync 主导）；10ms 让攒批充分。</para>
    /// <para>极致低延迟场景可配 1ms（吞吐降为代价）；攒页吞吐优先配 -1ms（禁用时间维度）。</para>
    /// <para>0 = 时间维度立即满足（每次 Append 提交）。-1ms（InfiniteTimeSpan）= 禁用时间维度（攒页/手动提交场景）。</para>
    /// </summary>
    public TimeSpan CommitInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// group 提交数据量阈值（未提交字节 ≥ 此值触发）。默认 64KB。
    /// <para>0 = 字节维度立即满足；要禁用字节维度设 <see cref="long.MaxValue"/>。</para>
    /// </summary>
    public long MaxUnflushedBytes { get; init; } = AlignmentConst.Alignment64K;

    /// <summary>
    /// group 提交记录数阈值（未提交 entry 数 ≥ 此值触发）。默认 1000。
    /// <para>0 = 条数维度立即满足；要禁用条数维度设 <see cref="int.MaxValue"/>。</para>
    /// </summary>
    public int MaxUnflushedCount { get; init; } = 1000;
}
