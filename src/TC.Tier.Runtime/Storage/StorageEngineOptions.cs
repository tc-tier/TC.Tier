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
    /// <param name="engineName">引擎名（构造引擎子目录用）。</param>
    /// <returns>以 <paramref name="engineName"/> 构造的 <see cref="StorageEngineOptions"/>（其余取默认值）。</returns>
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

    /// <summary>
    /// 段元组耐久化泵周期——生命周期元组（建段初始/段满）记脏后由后台泵按此周期批量落盘。
    /// <para>★ <see cref="Timeout.InfiniteTimeSpan"/> = 耐久化仅锚定 Dispose 补写；
    ///   <see cref="TimeSpan.Zero"/> = 逐写同步直写（每生命周期点 fsync——小段高频场景的延迟瓶颈形态）。
    ///   默认 200ms 批量。</para>
    /// <para>★ 依据（2026-09-08 磁盘销案）：机械盘每 meta fsync ~15-30ms × 小段压测几何
    ///   （4KB 段 × 600 创建+段满 ≈ 1200 次串行 fsync ≈ 数十秒）把建段路径拖过测试预算；
    ///   泵化后生命周期操作零 fsync 等待。崩溃窗口 ≤ 周期——恢复设计本可容忍 stale 元组
    ///   （fileSize 权威回退）；进程存活期缓存读己写恒成立。</para>
    /// </summary>
    public TimeSpan MetaTupleFlushInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>存储引擎优化参数（不可变子对象——With* 链同构）。</summary>
    public StorageEngineOptimization Optimization { get; init; } = new();

    /// <summary>时钟供给源（故障注入面 件一——时钟缝 P1 落点；缺省 <see cref="TimeProvider.System"/> 行为逐字节零变化）。
    /// <para>节流自旋窗/元组耐久化泵周期经本源驱动——假钟下由快进确定性触发。</para></summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>是否开启引擎故障注入面（默认 false——<see cref="IStorageEngineFaultInjector"/> 缺省关闭零开销，
    /// 引擎 op 入口一次空引用短路）。开启后经引擎 <c>Faults</c> 属性取得注入面。</summary>
    public bool EnableFaultInjection { get; init; }

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
    /// <param name="limit">段增长上限（字节）；≤ 0 回落引擎默认（256MB）。</param>
    /// <param name="enableSegmentation">是否启用段分段，默认 true。</param>
    /// <returns>替换 SegmentGrowthLimit/EnableSegmentation 后的新 <see cref="StorageEngineOptions"/> 实例。</returns>
    public StorageEngineOptions WithSegment(long limit, bool enableSegmentation = true)
        => this with
        {
            SegmentGrowthLimit = limit > 0 ? limit : 256L * 1024 * 1024,   // ≤0 回落引擎默认
            EnableSegmentation = enableSegmentation,
        };

    /// <summary>建段时是否真实预分配（返回新实例）。</summary>
    /// <param name="enable">true = 每段按段大小预留；false = 稀疏按需增长。</param>
    /// <returns>替换 PreallocateFile 后的新 <see cref="StorageEngineOptions"/> 实例。</returns>
    public StorageEngineOptions WithPreallocateFile(bool enable) => this with { PreallocateFile = enable };

    /// <summary>Dispose 时是否删除引擎产物（返回新实例）。</summary>
    /// <param name="enable">true = Dispose 时删除引擎子目录下全部产物；false = 保留。</param>
    /// <returns>替换 DeleteOnClose 后的新 <see cref="StorageEngineOptions"/> 实例。</returns>
    public StorageEngineOptions WithDeleteOnClose(bool enable) => this with { DeleteOnClose = enable };

    /// <summary>设置段元组耐久化泵周期（返回新实例；负值归一为 Infinite——仅 Dispose 锚定）。</summary>
    /// <param name="interval">泵周期（正值 = 批量落盘周期；<see cref="Timeout.InfiniteTimeSpan"/> = 仅 Dispose 锚定；
    ///   <see cref="TimeSpan.Zero"/> = 逐写同步直写；负值归一为 Infinite）。</param>
    /// <returns>替换 MetaTupleFlushInterval 后的新 <see cref="StorageEngineOptions"/> 实例。</returns>
    public StorageEngineOptions WithMetaTupleFlushInterval(TimeSpan interval)
        => this with { MetaTupleFlushInterval = interval < TimeSpan.Zero ? Timeout.InfiniteTimeSpan : interval };

    /// <summary>打开提示（返回新实例——替换语义，可显式表达 None）。</summary>
    /// <param name="hints">Core IO 打开提示（如 <see cref="FileOpenHints.NoBuffering"/>/<see cref="FileOpenHints.WriteThrough"/>）。</param>
    /// <returns>替换 Hints 后的新 <see cref="StorageEngineOptions"/> 实例。</returns>
    public StorageEngineOptions WithHints(FileOpenHints hints) => this with { Hints = hints };

    /// <summary>设置最小有效段号（恢复路径首段，返回新实例）。</summary>
    /// <param name="minSegId">最小有效段号（非负，默认 0）。</param>
    /// <returns>替换 MinSegId 后的新 <see cref="StorageEngineOptions"/> 实例。</returns>
    public StorageEngineOptions WithMinSegId(int minSegId) => this with { MinSegId = minSegId };

    /// <summary>设置优化参数（返回新实例——子对象不可变链同构）。</summary>
    /// <param name="optimization">新的优化参数对象，非空。</param>
    /// <returns>替换 Optimization 后的新 <see cref="StorageEngineOptions"/> 实例。</returns>
    public StorageEngineOptions WithOptimization(StorageEngineOptimization optimization)
        => this with { Optimization = optimization };

    /// <summary>With 链——时钟供给源（节流自旋/元组泵周期注入）。</summary>
    /// <param name="clock">时钟供给源（非空）。</param>
    /// <returns>替换 Clock 后的新 <see cref="StorageEngineOptions"/> 实例。</returns>
    public StorageEngineOptions WithClock(TimeProvider clock) => this with { Clock = clock };

    /// <summary>开启引擎故障注入面（返回新实例；缺省关闭零开销——显式开启面，非测试代码泄漏）。</summary>
    /// <returns>EnableFaultInjection = true 的新 <see cref="StorageEngineOptions"/> 实例
    ///   （引擎 <c>Faults</c> 属性可得 <see cref="IStorageEngineFaultInjector"/>）。</returns>
    public StorageEngineOptions WithFaults() => this with { EnableFaultInjection = true };

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
    /// <param name="segmentHandlerDecorator">可选的段处理器装饰器（包在默认委托外层——合成建段时序等取证/测试面）。</param>
    /// <returns>绑定本选项的 <see cref="StorageEngineBuilder"/>（可 <c>Start/StartAsync</c> 启动）。</returns>
    public StorageEngineBuilder Builder(IFileSystem root, ICompact? compact = null, ICheckpoint? checkpoint = null,
        ILogger? logger = null, ObservabilityHub? hub = null, LightEpoch? epoch = null,
        Func<AddressSpace.ISegmentHandler, AddressSpace.ISegmentHandler>? segmentHandlerDecorator = null)
        => new(root, this, compact, checkpoint, logger, hub, epoch, segmentHandlerDecorator);

    /// <summary>转换为段表设置对象，用于配置段表的行为和参数。</summary>
    /// <returns>由本选项派生的 <see cref="SegmentTableSettings"/>（MinSegId/IndexCapacity/SpinMilliseconds 等逐项映射）。</returns>
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
