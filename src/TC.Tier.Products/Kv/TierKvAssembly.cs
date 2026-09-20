using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Products.Kv;

/// <summary>
/// TierKv 默认装配的 Settings 翻译面（public——<c>[KvStore]</c> 生成的封闭形态经此把
/// <see cref="TierKvOptions"/> 翻译为各族结构 Settings；显式层 <see cref="TierKvBuilder{TKey, TValue}"/> 工厂注入同用）。
/// <para>★ 只建 Settings（纯配置类型，构建零副作用）——结构实例构造仍走闸门（开放泛型不落消费面，
/// 生成封闭形态以派生肢构造）。</para>
/// <para>★ 引擎名派生：数据 = KvName+"-data"、索引 = KvName+"-index"（同卷异引擎名与 Ring 共栖）。</para>
/// <para>★ mem 介质不预分配物理段（TierWalBuilder 同款守卫——preallocate 触发大块 pinned 数组，
/// POH 是进程全局堆，N 实例并发建段打爆 POH → native 挂死；mem 无磁盘对齐需求，稀疏按需零代价）。</para>
/// </summary>
public static class TierKvAssembly
{
    /// <summary>数据引擎名后缀（Ring 数据真源）。</summary>
    public const string DataEngineSuffix = "-data";

    /// <summary>索引引擎名后缀（主索引派生结构）。</summary>
    public const string IndexEngineSuffix = "-index";

    /// <summary>引擎选项（同名派生 + 介质适配——mem 不预分配守卫见类型注释）。</summary>
    /// <param name="fs">文件系统抽象（介质面；mem 不预分配段）。</param>
    /// <param name="options">TierKv 选项。</param>
    /// <param name="engineSuffix">引擎名后缀（-data/-index）。</param>
    /// <returns>派生引擎名 + 介质适配的 StorageEngineOptions。</returns>
    public static StorageEngineOptions EngineOptions(IFileSystem fs, TierKvOptions options, string engineSuffix)
    {
        var preallocate = fs is not TC.Tier.Core.IO.Mem.MemoryFileSystem;
        var engine = new StorageEngineOptions(options.KvName + engineSuffix, options.SegmentGrowthLimit,
                enableSegmentation: true, preallocateFile: preallocate, deleteOnClose: false)
            .WithHints(options.Hints)
            .WithMetaTupleFlushInterval(options.MetaTupleFlushInterval);
        // 调度器配置形态透传（#486——null = 引擎默认；非空 = 同实例双引擎同用该配置；
        // 实例共享形态不经此处——经 Settings.WorkerScheduler 直达结构内全部引擎含 meta）
        return options.WorkerSchedulerOptions is { } scheduler ? engine.WithWorkerScheduler(scheduler) : engine;
    }

    /// <summary>Ring 数据引擎 Settings（meta 策略随 Options——锚点 W 随水位落盘载体，W5 检查点依赖；
    /// 页池页大小/容量显式配置——结构默认 32MB 巨页会让 Committed 档增量 flush 每次写穿 32MB，
    /// 实测退化到 ms 级；1MB 页把单次 flush 写穿量降两个数量级）。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TierKv 选项。</param>
    /// <returns>Ring 数据引擎 Settings。</returns>
    public static BlittableRingSettings RingSettings(IFileSystem fs, TierKvOptions options)
        => new(EngineOptions(fs, options, DataEngineSuffix))
        {
            MetaPolicyKind = options.MetaPolicyKind,
            PageSize = options.RingPageSize,
            MemorySize = options.RingMemorySize,
            ColdReadRatio = options.RingColdReadRatio,
            MutableFraction = options.RingMutableFraction,
            MaxPageCount = options.RingMaxPageCount,
            Preallocate = options.RingPreallocate,
            OverflowPolicy = options.RingOverflowPolicy,
            MinOverflowSize = options.RingMinOverflowSize,
            WorkerScheduler = options.WorkerScheduler,
        };

    /// <summary>Hash 主索引 Settings（表容量/溢出池随 Options）。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TierKv 选项。</param>
    /// <returns>Hash 主索引 Settings。</returns>
    public static HashIndexSettings HashSettings(IFileSystem fs, TierKvOptions options)
        => new(EngineOptions(fs, options, IndexEngineSuffix))
        {
            HashTableCapacity = options.HashTableCapacity,
            OverflowPoolCapacity = options.OverflowPoolCapacity,
            WorkerScheduler = options.WorkerScheduler,
        };

    /// <summary>
    /// 范围索引装配（W7 F1——key 字节序 BTree 封闭形态 ByteOrderBTreeIndex，resolver 挂 Ring；
    /// EnableRangeIndex 开启时由 Builder/生成封闭形态调用）。
    /// </summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TierKv 选项。</param>
    /// <param name="ring">Ring 数据引擎（作为 keyResolver——tag 判等闭环回读数据面）。</param>
    /// <returns>key 字节序 BTree 范围索引实例。</returns>
    public static TC.Tier.Runtime.Structures.SortedIndex.ByteOrderBTreeIndex<TKey> CreateRangeIndex<TKey>(
        TC.Tier.Core.IO.IFileSystem fs, TierKvOptions options,
        TC.Tier.Runtime.Structures.Ring.RingBase<TKey> ring)
        where TKey : unmanaged, IEquatable<TKey>
        => new(fs, BTreeSettings(fs, options), keyResolver: ring);

    /// <summary>BTree 主索引 Settings（缺省几何——Options 无 BTree 专属轴）。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TierKv 选项。</param>
    /// <returns>BTree 主索引 Settings。</returns>
    public static BTreeIndexSettings BTreeSettings(IFileSystem fs, TierKvOptions options)
        => new(EngineOptions(fs, options, IndexEngineSuffix))
        {
            WorkerScheduler = options.WorkerScheduler,
        };

    /// <summary>SkipList 主索引 Settings（缺省几何——Options 无 SkipList 专属轴）。</summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TierKv 选项。</param>
    /// <returns>SkipList 主索引 Settings。</returns>
    public static SkipListIndexSettings SkipListSettings(IFileSystem fs, TierKvOptions options)
        => new(EngineOptions(fs, options, IndexEngineSuffix))
        {
            WorkerScheduler = options.WorkerScheduler,
        };
}
