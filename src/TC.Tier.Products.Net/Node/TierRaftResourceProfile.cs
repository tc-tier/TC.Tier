using TC.Tier.Core.Execution;

namespace TC.Tier.Products.Net.Node;

/// <summary>
/// 节点资源档位（装配期一次定性——展开为具体组件参数，热路径零分支）。
/// <para>★ 展开语义 = <b>只补缺省</b>：显式 <see cref="TierRaftNodeOptions.WithWorkerScheduler"/> 注入
///   恒优先；<see cref="TierRaftResourceProfile.LowResource"/> 在 Wal 配置未被定制（== Default）时全量
///   生效预设（引擎调度器收敛到进程级共享单例 + 几何收缩：页 1MB/快照段 8MB/worker 消费者 1）；
///   Wal 已定制 = 用户主权——仅补调度器缺省，几何不动。</para>
/// <para><see cref="TierRaftResourceProfile.HighPerformance"/> = 缺省自建形态
///   （每引擎 2~4 条专用调度线程，单实例最优并发）。</para>
/// </summary>
public enum TierRaftResourceProfile
{
    /// <summary>高性能档（缺省）：每引擎自建专用调度线程——存储并发度最优。</summary>
    HighPerformance = 0,

    /// <summary>低资源档：引擎 worker 调度器收敛到进程级共享一组线程 + Wal 几何收缩
    /// （多节点同进程/嵌入式/边缘部署的线程与内存地板形态）。raft/apply/传输的协议专用线程
    /// 不受档位影响（活性地板，2026-09-03 判例）。</summary>
    LowResource = 1,
}

/// <summary>
/// 资源档位展开内核（装配期共用——<see cref="TierRaftNode"/> 节点级 / TierRaftHost 宿主级两调用方：
/// 宿主级作用域 = Host 自建调度器实例传入，节点级 = 进程级 Shared）。
/// </summary>
internal static class TierRaftResourceProfileExpander
{
    /// <summary>低资源档 Wal 展开（只补缺省）：Default → 预设全量（自带 scheduler + 几何收缩）；
    /// 已定制 → 仅补 scheduler 缺省（用户主权——几何不动）；scheduler 已注入 → 原样。</summary>
    public static TierWalOptions ApplyLowResourceWal(TierWalOptions wal, IsolatedTaskScheduler scheduler)
    {
        if (wal == TierWalOptions.Default)
            return LowResourceWalPreset(scheduler);
        return wal.WorkerScheduler is null ? wal.WithWorkerScheduler(scheduler) : wal;
    }

    /// <summary>低资源档 Wal 预设（共享调度器 + 内存/磁盘足迹收缩——单实例最优并发让位于多实例线程地板）。</summary>
    public static TierWalOptions LowResourceWalPreset(IsolatedTaskScheduler scheduler) => TierWalOptions.Default
        .WithWorkerScheduler(scheduler)
        .WithLogPageSizeBits(20)                                    // 4MB → 1MB 页（低吞吐场景刷盘写穿量降四分之三）
        .WithSnapshotSegmentGrowthLimit(8L << 20)                   // 快照段 64MB → 8MB
        .WithOptimization(new TC.Tier.Runtime.Storage.StorageEngineOptimization
        {
            WorkerConsumers = 1,                                    // 共享调度器上压单引擎并发占用
        });
}
