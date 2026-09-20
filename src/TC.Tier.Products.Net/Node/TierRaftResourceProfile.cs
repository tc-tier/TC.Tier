namespace TC.Tier.Products.Net.Node;

/// <summary>
/// 节点资源档位（装配期一次定性——展开为具体组件参数，热路径零分支）。
/// <para>★ 展开语义 = <b>只补缺省</b>：显式 <see cref="TierRaftNodeOptions.WithWorkerScheduler"/> 注入
///   恒优先于档位；<see cref="TierRaftResourceProfile.LowResource"/> 仅在未显式注入时把引擎调度器
///   收敛到进程级共享单例 <c>IsolatedTaskScheduler.Shared</c>（全部引擎一组线程——多实例/嵌入式/
///   边缘部署线程数恒定）。<see cref="TierRaftResourceProfile.HighPerformance"/> = 缺省自建形态
///   （每引擎 2~4 条专用调度线程，单实例最优并发）。</para>
/// </summary>
public enum TierRaftResourceProfile
{
    /// <summary>高性能档（缺省）：每引擎自建专用调度线程——存储并发度最优。</summary>
    HighPerformance = 0,

    /// <summary>低资源档：引擎 worker 调度器收敛到进程级共享一组线程——多节点同进程/嵌入式/边缘
    /// 部署的线程地板形态。raft/apply/传输的协议专用线程不受档位影响（活性地板，2026-09-03 判例）。</summary>
    LowResource = 1,
}
