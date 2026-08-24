namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>探测族持久化形态（主存储开关）。</summary>
public enum ProbingIndexPersistenceKind
{
    /// <summary>纯内存 + Ring 重放恢复（不建主存储数据）。</summary>
    None = 0,

    /// <summary>自建主存储（默认）——后台协作式 dump，恢复三级回退主存储级。</summary>
    Builtin = 1,
}

/// <summary>
/// 后台持久化触发策略——时间间隔 / 条目增量水位阈值，**任一命中**即触发 dump。
/// </summary>
public sealed record ProbingIndexPersistencePolicy
{
    /// <summary>时间间隔（自上次 dump 起）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>条目增量水位阈值（自上次 dump 起的新增条目数）。</summary>
    public long EntryDeltaThreshold { get; init; } = 10_000;

    /// <summary>判定是否触发（elapsed=距上次 dump 时长；entryDelta=距上次 dump 新增条目数）。</summary>
    public bool IsTriggered(TimeSpan elapsed, long entryDelta)
        => elapsed >= Interval || entryDelta >= EntryDeltaThreshold;
}

/// <summary>
/// ProbingIndexSettings 配置基类（引擎选项经 Settings.MainEngine；水位线归结构层不下传）。
/// <para>★ 持久化机制配置（Kind/Policy/KeepVersions）收基类——机制归基类，子类只填格式布局
///   （对齐 LogBase/RingBase/MirrorBase：机制容器在基类，子类只实现 codec）。</para>
/// </summary>
public abstract class ProbingIndexSettings : Settings
{
    protected ProbingIndexSettings(string name = "tc.probingindex")
        : base(name, segmentGrowthLimit: AlignmentConst.Alignment1G) { }

    /// <summary>引擎选项直构（对齐 RingSettings/LogSettings 双 ctor 形态）。</summary>
    protected ProbingIndexSettings(StorageEngineOptions mainEngine) : base(mainEngine) { }

    /// <summary>持久化形态（默认 Builtin——自建主存储；None=纯内存 + Ring 重放，派生结构红利可关闭）。</summary>
    public ProbingIndexPersistenceKind PersistenceKind { get; init; } = ProbingIndexPersistenceKind.Builtin;

    /// <summary>后台持久化触发策略（时间间隔 / 条目增量水位阈值，任一命中）。</summary>
    public ProbingIndexPersistencePolicy PersistencePolicy { get; init; } = new();

    /// <summary>版本保留数（默认 N=2，对齐 Mirror/Metadata 家族轮替惯例）。</summary>
    public int PersistenceKeepVersions { get; init; } = 2;
}
