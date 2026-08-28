namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>比较族持久化形态（主存储开关）。</summary>
public enum SortedIndexPersistenceKind
{
    /// <summary>纯内存 + Ring 重放恢复（不建主存储数据）。</summary>
    None = 0,

    /// <summary>自建主存储（默认）——后台协作式 dump，恢复三级回退主存储级。</summary>
    Builtin = 1,
}

/// <summary>
/// 后台持久化触发策略——时间间隔 / 条目增量水位阈值，**任一命中**即触发 dump。
/// </summary>
public sealed record SortedIndexPersistencePolicy
{
    /// <summary>时间间隔（自上次 dump 起）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>条目增量水位阈值（自上次 dump 起的新增条目数）。</summary>
    public long EntryDeltaThreshold { get; init; } = 10_000;

    /// <summary>判定是否触发（elapsed=距上次 dump 时长；entryDelta=距上次 dump 新增条目数）。</summary>
    public bool IsTriggered(TimeSpan elapsed, long entryDelta)
        => elapsed >= Interval || entryDelta >= EntryDeltaThreshold;
}

/// <summary>SortedIndexSettings 配置基类（引擎选项经 Settings.MainEngine；水位线归结构层不下传）。</summary>
public abstract class SortedIndexSettings : Settings
{
    /// <summary>名称直构（默认 tc.sortedindex——segmentGrowthLimit 固定 1G，对齐 Settings 家族 ctor 形态）。</summary>
    /// <param name="name">结构名称。</param>
    protected SortedIndexSettings(string name = "tc.sortedindex")
        : base(name, segmentGrowthLimit: AlignmentConst.Alignment1G) { }

    /// <summary>引擎选项直构（对齐 RingSettings/LogSettings 双 ctor 形态）。</summary>
    protected SortedIndexSettings(StorageEngineOptions mainEngine) : base(mainEngine) { }

    /// <summary>
    /// 持久化形态（默认 Builtin——自建主存储；None=纯内存 + Ring 重放，派生结构红利可关闭）。
    /// <para>★ 比较族主存储帧体 = 32B 几何（根/head 指针 + 计数——节点本就写时持久化在自持引擎内，
    ///   物化只设根 + 计数，无需逐节点流）。</para>
    /// </summary>
    public SortedIndexPersistenceKind PersistenceKind { get; init; } = SortedIndexPersistenceKind.Builtin;

    /// <summary>后台持久化触发策略（时间间隔 / 条目增量水位阈值，任一命中）。</summary>
    public SortedIndexPersistencePolicy PersistencePolicy { get; init; } = new();

    /// <summary>版本保留数（默认 N=2，对齐 Mirror/Metadata 家族轮替惯例）。</summary>
    public int PersistenceKeepVersions { get; init; } = 2;

    /// <summary>节点缓存初始容量（两族共用器官 LogicalAddressMap 的起步槽位）。
    /// <para>★ 节点即数据教义（两族一致）：缓存无上限生长至索引数据量级——本值只定初始槽位
    /// （少一次早期重散列），不是内存上限。100k 条 BTree ≈ 2.2MB/SkipList ≈ 7-29MB，皆 L3 量级。</para></summary>
    public int NodeCacheInitialCapacity { get; init; } = 1024;
}
