using TC.Tier.Core.Execution;

namespace TC.Tier.Products.Blob;

/// <summary>
/// TierBlob 配置参数（tierblob-spec——Options 只管零件规格，装配归 Builder）。
/// <para>★ record——测试/组合根可用 <c>defaults with { … }</c> 派生（对齐 TimeSeriesOptions 先例）。</para>
/// </summary>
public sealed record TierBlobOptions
{
    /// <summary>默认 Blob 空间名（引擎 = {name}.blob.data / {name}.blob.meta）。</summary>
    public const string DefaultName = "tc";

    /// <summary>空间名（引擎空间隔离，对齐时序 <c>{name}.ts.*</c> 惯例——裁定④：
    /// 主数据引擎 = {name}.blob.data，对象表引擎 = {name}.blob.meta）。</summary>
    public string BlobName { get; init; } = DefaultName;

    /// <summary>主数据引擎段增长上限（字节）。默认 64MB——GB/TB 大对象跨段扩容。</summary>
    public long SegmentGrowthLimit { get; init; } = 64L << 20;

    /// <summary>对象表引擎段增长上限（字节）。默认 4MB（表记录 ~90B/条，小段即可）。</summary>
    public long TableSegmentGrowthLimit { get; init; } = 4L << 20;

    /// <summary>数据写会话双缓冲单 buffer 大小（字节，扇区对齐取整生效）。默认 128KB。</summary>
    public int SessionBufferSize { get; init; } = 128 * 1024;

    /// <summary>对象表会话 buffer 大小（字节）。默认 8KB——表记录帧 ≤ 1KB 量级，小 buffer 降恢复重放开销。</summary>
    public int TableSessionBufferSize { get; init; } = 8 * 1024;

    /// <summary>容量上限（字节，引擎已用物理折算）。超限 Put/OpenWrite fail-fast（spec §6）。
    /// null = 不限（默认）。</summary>
    public long? MaxBytes { get; init; }

    /// <summary>恢复对账深度校验开关（spec §5）：false（默认）= 帧尾级 O(1)/对象（footer magic +
    /// TotalLength 对账——捕获截断/撕裂）；true = 整帧读回 + CRC64 全量校验（TB 级对象恢复耗时线性，
    /// 仅运维深检场景开启）。</summary>
    public bool DeepVerifyOnRecovery { get; init; }

    /// <summary>引擎 worker 调度器（共享注入形态——data/meta 两引擎共用一组线程；所有权 Referenced
    /// 归注入方——TierBlob 释放不回收）。null = 缺省自建；非空时 <see cref="WorkerSchedulerOptions"/>
    /// 配置形态被忽略（#505 两形态词汇统一）。</summary>
    public IsolatedTaskScheduler? WorkerScheduler { get; init; }

    /// <summary>引擎 worker 调度器选项（配置形态——每引擎按此自建，null = 全默认 RecommendedThreadCount）。
    /// 实例 <see cref="WorkerScheduler"/> 非空时本项被忽略。</summary>
    public IsolatedSchedulerOptions? WorkerSchedulerOptions { get; init; }
}
