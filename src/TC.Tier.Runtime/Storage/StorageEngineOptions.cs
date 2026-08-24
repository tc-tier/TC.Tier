namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 存储引擎配置构建器（完整 builder 模式，设计决策）——外部使用者的构建链起点：
/// <c>new StorageEngineOptions(...).WithXxx()...Builder(fs).Start()</c>，实现类 internal 不对外。
/// <para>★ 不可变（record class）：全部属性 init-only，<c>With*</c> 返回<b>新实例</b>（with 表达式）——
///   配置对象可安全共享/复用（模板派生变体）、链式无副作用、线程安全。</para>
/// </summary>
public sealed record StorageEngineOptions
{
    /// <summary>默认引擎策略选项，用于构造引擎实例时的默认配置。</summary>
    public static StorageEngineOptions Default { get; } = new();

    /// <summary>隐式转换：引擎名 → 选项（便捷构造）。</summary>
    public static implicit operator StorageEngineOptions(string engineName) => new(engineName);

    /// <summary>引擎名（用于构造引擎子目录，默认 "tier-engine"）。</summary>
    public string EngineName { get; init; }

    /// <summary>建段时是否真实预分配（true = 每段按段大小预留；false = 稀疏按需增长）。默认 true。</summary>
    public bool PreallocateFile { get; init; }

    /// <summary>Dispose 时是否删除引擎子目录下全部产物。默认 false。</summary>
    public bool DeleteOnClose { get; init; }

    /// <summary>
    /// 打开提示（请求意图，Core IO 对象——不自造枚举）：
    /// <see cref="FileOpenHints.NoBuffering"/> = DIO 请求（建段句柄探测后真实结果经
    /// <c>StorageEngine.UnbufferedSupport</c> 报告）；<see cref="FileOpenHints.WriteThrough"/> = 每写同步落盘。
    /// 默认 None（group commit + 显式 Flush 落盘）；WriteThrough 是显式选项，不是默认。
    /// </summary>
    public FileOpenHints Hints { get; init; }

    /// <summary>段增长上限（单位字节，默认 256MB）。超过该值的段将不再增长，写入将失败。</summary>
    public long SegmentGrowthLimit { get; init; }

    /// <summary>是否启用段分段（默认 true）。</summary>
    public bool EnableSegmentation { get; init; }

    /// <summary>段表最小有效段号（恢复路径首段 segId，默认 0）。</summary>
    public int MinSegId { get; init; }

    /// <summary>存储引擎优化参数（不可变子对象——With* 链同构）。</summary>
    public StorageEngineOptimization Optimization { get; init; } = new();

    /// <summary>主构造（位置参数——便捷构造/结构 Settings 消费面在用；≤0 回落引擎默认）。</summary>
    public StorageEngineOptions(
        string engineName = "tier-engine",
        long segmentGrowthLimit = 256L * 1024 * 1024,
        bool enableSegmentation = true, bool preallocateFile = true, bool deleteOnClose = false)
    {
        EngineName = engineName;
        SegmentGrowthLimit = segmentGrowthLimit > 0 ? segmentGrowthLimit : 256L * 1024 * 1024;
        EnableSegmentation = enableSegmentation;
        PreallocateFile = preallocateFile;
        DeleteOnClose = deleteOnClose;
    }

    // ═══════════════════════════════════════════════════════════════
    //  完整 builder——With* 返回新实例（with 表达式，不可变）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>设置段增长上限和是否启用段分段（返回新实例）。</summary>
    public StorageEngineOptions WithSegment(long limit, bool enableSegmentation = true)
        => this with
        {
            SegmentGrowthLimit = limit > 0 ? limit : 256L * 1024 * 1024,   // ≤0 回落引擎默认
            EnableSegmentation = enableSegmentation,
        };

    /// <summary>建段时是否真实预分配（返回新实例）。</summary>
    public StorageEngineOptions WithPreallocateFile(bool enable) => this with { PreallocateFile = enable };

    /// <summary>Dispose 时是否删除引擎产物（返回新实例）。</summary>
    public StorageEngineOptions WithDeleteOnClose(bool enable) => this with { DeleteOnClose = enable };

    /// <summary>打开提示（返回新实例——替换语义，可显式表达 None）。</summary>
    public StorageEngineOptions WithHints(FileOpenHints hints) => this with { Hints = hints };

    /// <summary>设置最小有效段号（恢复路径首段，返回新实例）。</summary>
    public StorageEngineOptions WithMinSegId(int minSegId) => this with { MinSegId = minSegId };

    /// <summary>设置优化参数（返回新实例——子对象不可变链同构）。</summary>
    public StorageEngineOptions WithOptimization(StorageEngineOptimization optimization)
        => this with { Optimization = optimization };

    // ═══════════════════════════════════════════════════════════════
    //  构建出口（builder 中间层——.NET Core 启动配置链路同构）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 构建引擎构建者（外部使用者的唯一引擎入口：Options → Builder → <c>Start/StartAsync</c>，
    /// 启动一步到位，不允许外部直接调 Initialize）。
    /// </summary>
    /// <param name="root">文件系统根目录。</param>
    /// <param name="compact">可选的压缩子系统。</param>
    /// <param name="checkpoint">可选的检查点子系统。</param>
    /// <param name="logger">可选的日志记录器。</param>
    /// <param name="hub">可选的可观察性中心。</param>
    /// <param name="epoch">可选的轻量级纪元。</param>
    public StorageEngineBuilder Builder(IFileSystem root, ICompact? compact = null, ICheckpoint? checkpoint = null,
        ILogger? logger = null, ObservabilityHub? hub = null, LightEpoch? epoch = null)
        => new(root, this, compact, checkpoint, logger, hub, epoch);

    /// <summary>转换为段表设置对象，用于配置段表的行为和参数。</summary>
    public SegmentTableSettings ToSegmentTableSettings()
    {
        return new SegmentTableSettings
        {
            MinSegId = MinSegId,
            IndexCapacity = Optimization.IndexCapacity,
            SpinMilliseconds = Optimization.SpinMilliseconds,
            WarnEvery = Optimization.WarnEvery,
            EnableSingleSegment = !EnableSegmentation
        };
    }
}
