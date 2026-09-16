using System.Net;
using TC.Tier.Core.IO;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Net.Host;

/// <summary>
/// TierRaftHost 装配选项（二期-C3 §8/§9——裁定②-4 双形态：自建工厂 / 外部传输注入）。
/// </summary>
public sealed class TierRaftHostOptions
{
    /// <summary>组存储命名空间工厂（每组独立 TierWal 域——§8.1 由组 ID 派生；CreateGroup 必供给：
    /// host 参数或本工厂二选一）。返回的文件系统生命周期归调用方——host 只用不释。</summary>
    public Func<RaftGroupId, IFileSystem>? GroupFileSystemFactory { get; init; }

    /// <summary>组默认节点选项（每组建装可覆写——§8.3 组内调度参数独立）。</summary>
    public TierRaftNodeOptions GroupDefaults { get; init; } = TierRaftNodeOptions.Default;

    /// <summary>成员对账周期（§8.2——全体组配置成员并集 ↔ 物理对端表收敛；≤ 0 = 关闭对账）。
    /// 缺省 5s。</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(5);

    // ── 自建工厂形态（Create）的传输装配参数 ──

    /// <summary>监听端点（null = 不监听——纯客户端形态无组面，不适用多组；建组需监听）。</summary>
    public IPEndPoint? ListenEndPoint { get; init; }

    /// <summary>初始对端地址表（动态成员运行期经 IPeerRegistry 增删——§5.3）。</summary>
    public IReadOnlyDictionary<NodeId, IPEndPoint>? Peers { get; init; }

    /// <summary>安全档（null = 明文档）。</summary>
    public SecurityOptions? Security { get; init; }

    /// <summary>日志。</summary>
    public ILogger? Logger { get; init; }

    /// <summary>可观测枢纽（null = Disabled）。</summary>
    public ObservabilityHub? Hub { get; init; }

    /// <summary>拓扑感知（二期-F1——成员位置标签 rack/zone/region 与反亲和/故障域判定面）。</summary>
    public TopologyMap? Topology { get; init; }

    /// <summary>服务发现源（二期-F8/F9——add-only 汇聚入 IPeerRegistry；DNS 漂移跟随面）。</summary>
    public IReadOnlyList<IPeerDiscoverySource>? DiscoverySources { get; init; }

    /// <summary>发现轮询周期（缺省 30s）。</summary>
    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>建组时强制反亲和校验（组配置成员跨故障域——violation 抛；缺省关闭）。</summary>
    public bool EnforceAntiAffinity { get; init; }
}
