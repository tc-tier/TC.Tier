using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Queue;
using TC.Tier.Products.Queue;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// TierQueueReplica 测试工厂（tierqueue-replicated-spec §6 矩阵的装配底座）：
/// InProcess 传输 × TierRaftHost 多组 × 每节点一副本实例（同构 TierRaftHostTests 三节点形态）。
/// </summary>
internal static class TierQueueReplicaTestHarness
{
    /// <summary>诊断日志（矩阵失败取证用—— raft 换届/复制行为可见）。</summary>
    internal sealed class ConsoleLogger : TC.Tier.Core.Logging.ILogger
    {
        public static readonly ConsoleLogger Instance = new();
        public bool IsEnabled(TC.Tier.Core.Logging.LogLevel logLevel) => logLevel >= TC.Tier.Core.Logging.LogLevel.Information;
        public void Log(TC.Tier.Core.Logging.LogLevel logLevel, string message, Exception? exception = null)
        {
            try
            {
                File.AppendAllText("/tmp/tqr-diag.log",
                    $"[{DateTime.UtcNow:HH:mm:ss.fff}] {logLevel}: {message}" + (exception != null ? $" | {exception.Message}" : "") + "\n");
            }
            catch
            {
                // 诊断面尽力
            }
        }
    }

    /// <summary>fast raft/wal 配置（同 TierRaftHostTests——快选举 + 即时组提交）。</summary>
    public static TierRaftNodeOptions FastOptions() => TierRaftNodeOptions.Default
        .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))
        .WithRaft(RaftOptions.Default.WithElectionTimeout(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1200)));

    /// <summary>队列定义（一队列实例 = 一 raft 组）。</summary>
    /// <param name="Name">队列名（副本定位键）。</param>
    /// <param name="GroupId">raft 组 ID。</param>
    public sealed record QueueDef(string Name, RaftGroupId GroupId);

    /// <summary>单节点句柄（host + transport + 各队列副本）。</summary>
    public sealed class ReplicaNode : IAsyncDisposable
    {
        /// <summary>节点 ID。</summary>
        public required NodeId Id { get; init; }

        /// <summary>节点持久根目录（raft 组卷 + 状态卷——复活/重启复用）。</summary>
        public required string RootDir { get; init; }

        /// <summary>InProcess 传输端点。</summary>
        public required InProcessNode Transport { get; init; }

        /// <summary>多组宿主。</summary>
        public required TierRaftHost Host { get; init; }

        /// <summary>各队列副本（按队列名定位）。</summary>
        public required Dictionary<string, TierQueueReplica> Replicas { get; init; }

        /// <summary>便捷取主队列副本（按名 "main"——多组集群禁用 Values.First 顺序假设）。</summary>
        public TierQueueReplica Replica => Replicas["main"];

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            foreach (var r in Replicas.Values)
            {
                try { await r.DisposeAsync(); } catch { /* 尽力收尾 */ }
            }
            try { await Host.DisposeAsync(); } catch { /* 尽力收尾 */ }
            try { await Transport.DisposeAsync(); } catch { /* 尽力收尾 */ }
        }
    }

    /// <summary>多节点多队列集群（生命周期/重启/复活/收敛等待的测试底座）。</summary>
    public sealed class ReplicaCluster : IAsyncDisposable
    {
        private readonly List<QueueDef> _defs;
        private readonly Func<TierQueueOptions, TierQueueOptions> _mainQueueConfigure;
        private readonly TierQueueReplicaOptions _replicaOptions;
        private List<NodeId> _members = [];
        private readonly HashSet<(string Queue, string Group)> _bootstrapFallbacks = [];

        private ReplicaCluster(string rootDir, InProcessTransportHub hub, List<QueueDef> defs,
            Func<TierQueueOptions, TierQueueOptions> mainQueueConfigure, TierQueueReplicaOptions replicaOptions)
        {
            RootDir = rootDir;
            Hub = hub;
            Nodes = [];
            _defs = defs;
            _mainQueueConfigure = mainQueueConfigure;
            _replicaOptions = replicaOptions;
        }

        /// <summary>节点根目录。</summary>
        public string RootDir { get; }

        /// <summary>传输枢纽。</summary>
        public InProcessTransportHub Hub { get; }

        /// <summary>活节点集合（kill 后移除、复活后加回）。</summary>
        public List<ReplicaNode> Nodes { get; }

        /// <summary>按队列名取活节点上的副本（取首个活节点）。</summary>
        /// <param name="name">队列名。</param>
        /// <returns>副本实例。</returns>
        public TierQueueReplica Replica(string name)
            => Nodes.Select(n => n.Replicas.GetValueOrDefault(name)).First(r => r is not null)!;

        /// <summary>创建三节点单队列集群（DLQ 组按需追加——跨组死信语义）。</summary>
        /// <param name="nodeCount">节点数。</param>
        /// <param name="visibility">消费组可见性超时。</param>
        /// <param name="minRedeliveries">重投上限。</param>
        /// <param name="delayed">延迟索引开关（产品缺省已开）。</param>
        /// <param name="deadLetterGroup">true = 追加独立 DLQ 组并接线（矩阵 7）。</param>
        /// <param name="replicaConfigure">副本行为面配置。</param>
        /// <param name="queueConfigure">主队列选项配置。</param>
        /// <returns>已装配的集群。</returns>
        public static async Task<ReplicaCluster> CreateAsync(
            int nodeCount,
            TimeSpan? visibility = null,
            int minRedeliveries = 16,
            bool delayed = true,
            bool deadLetterGroup = false,
            Func<TierQueueReplicaOptions, TierQueueReplicaOptions>? replicaConfigure = null,
            Func<TierQueueOptions, TierQueueOptions>? queueConfigure = null)
        {
            var replicaOptions = replicaConfigure?.Invoke(
                TierQueueReplicaOptions.Default.WithHomeSweepInterval(TimeSpan.FromMilliseconds(100)))
                ?? TierQueueReplicaOptions.Default.WithHomeSweepInterval(TimeSpan.FromMilliseconds(100));

            var defs = new List<QueueDef> { new("main", new RaftGroupId(0x0A11)) };
            if (deadLetterGroup)
                defs.Add(new QueueDef("dlq", new RaftGroupId(0x0B22)));

            var root = Path.Combine(Path.GetTempPath(), "tctier-tqr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var hub = new InProcessTransportHub();
            var cluster = new ReplicaCluster(root, hub, defs,
                q => queueConfigure?.Invoke(q) ?? q, replicaOptions);
            try
            {
                cluster._members = [.. Enumerable.Range(0, nodeCount).Select(_ => NodeId.NewRandom())];
                var config = new ClusterConfig(cluster._members.Select(id => new ClusterMember(id, "")).ToArray());
                for (var i = 0; i < nodeCount; i++)
                {
                    await cluster.AddNodeAsync(cluster._members[i], Path.Combine(root, $"n{i}"), config,
                        visibility ?? TimeSpan.FromSeconds(60), minRedeliveries, delayed);
                }
                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        private async Task<ReplicaNode> AddNodeAsync(NodeId id, string nodeRoot, ClusterConfig config,
            TimeSpan visibility, int minRedeliveries, bool delayed)
        {
            Directory.CreateDirectory(nodeRoot);
            var transport = Hub.Register(id);
            transport.Start();
            var host = new TierRaftHost(transport, new TierRaftHostOptions
            {
                Logger = ConsoleLogger.Instance,
                GroupFileSystemFactory = gid => TierFs.OpenOrCreate(
                    $"local:///{Path.Combine(nodeRoot, "raft-" + gid.Value.ToString("X16")).Replace('\\', '/')}"),
                ReconcileInterval = TimeSpan.Zero,   // InProcess 隔离测试——对账关闭
            });
            await host.StartAsync();

            var replicas = new Dictionary<string, TierQueueReplica>();
            try
            {
                // DLQ 组先建（主队列跨组死信接线目标——WithDeadLetterTarget 经 Builder 注入）
                foreach (var def in _defs.OrderBy(d => d.Name == "main"))
                {
                    var stateFs = TierFs.OpenOrCreate(
                        $"local:///{Path.Combine(nodeRoot, "state-" + def.Name).Replace('\\', '/')}");
                    var isMain = def.Name == "main";
                    var baseOptions = new TierQueueOptions
                    {
                        QueueName = def.Name,
                        PageSize = 64 << 10,
                        MemorySize = 8 << 20,
                        DeadLetter = null,   // 复制版死信经跨组接线（WithDeadLetterTarget）——核心内建 DLQ 关闭
                        Delayed = delayed ? new DelayedOptions() : null,
                        Idempotency = null,
                    };
                    var queueOptions = isMain ? _mainQueueConfigure(baseOptions) : baseOptions;
                    var builder = new TierQueueReplicaBuilder(MemoryFileSystem.New(), queueOptions)
                        .WithLogger(ConsoleLogger.Instance)
                        .WithRaft(new TierQueueReplicaRaftOptions
                        {
                            GroupId = def.GroupId,
                            Config = config,
                            StateFs = stateFs,
                            NodeOptions = FastOptions(),
                        });
                    // 跨组死信接线：主队列挂同节点 DLQ 副本（矩阵 7——死信写入经 DLQ 组共识）
                    if (isMain && _defs.Any(d => d.Name == "dlq"))
                        builder.WithDeadLetterTarget(replicas["dlq"]);
                    var replica = await builder.StartAsync(host, _replicaOptions);
                    replicas[def.Name] = replica;
                }
            }
            catch
            {
                foreach (var r in replicas.Values)
                {
                    try { await r.DisposeAsync(); } catch { }
                }
                try { await host.DisposeAsync(); } catch { }
                try { await transport.DisposeAsync(); } catch { }
                throw;
            }

            var node = new ReplicaNode
            {
                Id = id,
                RootDir = nodeRoot,
                Transport = transport,
                Host = host,
                Replicas = replicas,
            };
            Nodes.Add(node);
            return node;
        }

        /// <summary>复活节点（同持久卷重建——raft 重入组、复制状态经重放/快照恢复）。</summary>
        /// <param name="id">节点 ID（须为初始成员）。</param>
        /// <param name="nodeRoot">原节点根目录。</param>
        /// <param name="visibility">消费组可见性超时。</param>
        /// <param name="minRedeliveries">重投上限。</param>
        /// <param name="delayed">延迟索引开关。</param>
        /// <returns>复活节点句柄（已加回 Nodes）。</returns>
        public Task<ReplicaNode> ReviveAsync(NodeId id, string nodeRoot,
            TimeSpan? visibility = null, int minRedeliveries = 16, bool delayed = true)
        {
            var members = _members.Contains(id) ? _members : [.. _members, id];
            var config = new ClusterConfig(members.Select(mid => new ClusterMember(mid, "")).ToArray());
            return AddNodeAsync(id, nodeRoot, config, visibility ?? TimeSpan.FromSeconds(60),
                minRedeliveries, delayed);
        }

        /// <summary>单节点原位重启（同持久卷——集群存活，冷启动快照导入/重放续接验证）。</summary>
        /// <param name="index">Nodes 中的节点下标。</param>
        /// <returns>重建后的节点句柄（已加回 Nodes）。</returns>
        public async Task<ReplicaNode> RestartNodeAsync(int index)
        {
            var victim = Nodes[index];
            var (id, rootDir) = (victim.Id, victim.RootDir);
            Nodes.RemoveAt(index);
            await victim.DisposeAsync();
            var config = new ClusterConfig(_members.Select(mid => new ClusterMember(mid, "")).ToArray());
            return await AddNodeAsync(id, rootDir, config, TimeSpan.FromSeconds(60), 16, delayed: true);
        }

        /// <summary>全集群重启（同持久卷——快照恢复/重放语义验证）。</summary>
        /// <returns>重启完成。</returns>
        public async Task RestartAllAsync(TimeSpan? visibility = null, int minRedeliveries = 16)
        {
            var layout = Nodes.Select(n => (n.Id, n.RootDir)).ToList();
            foreach (var n in Nodes)
                await n.DisposeAsync();
            Nodes.Clear();
            var config = new ClusterConfig(_members.Select(mid => new ClusterMember(mid, "")).ToArray());
            foreach (var (id, rootDir) in layout)
            {
                await AddNodeAsync(id, rootDir, config, visibility ?? TimeSpan.FromSeconds(60),
                    minRedeliveries, delayed: true);
            }
        }

        /// <summary>等待各队列组 raft leader 收敛（恰好一 leader/组）+ 显式设定各组 $default 辖权
        /// （leader 自领——消除多组引导竞速的辖权漂移）。</summary>
        /// <returns>收敛完成。</returns>
        public async Task WaitReadyAsync()
        {
            await WaitConvergedAsync(() =>
            {
                foreach (var def in _defs)
                {
                    var leaderCount = Nodes.Count(n =>
                        n.Replicas.TryGetValue(def.Name, out var r) && r.RaftGroup.Raft.IsLeader);
                    if (leaderCount != 1)
                        return Task.FromResult(false);
                }
                return Task.FromResult(true);
            }, TimeSpan.FromSeconds(20));

            foreach (var def in _defs)
            {
                var leader = Nodes.FirstOrDefault(n =>
                    n.Replicas.TryGetValue(def.Name, out var r) && r.RaftGroup.Raft.IsLeader);
                if (leader is not null)
                    await leader.Replicas[def.Name].TakeoverGroupHomeAsync(TierQueue.DefaultGroupName, default);
            }
        }

        /// <summary>等待指定队列组的辖权落定（HomeCmd 应用 + 引导链路；主队列缺省）。</summary>
        /// <param name="group">消费组名（$default）。</param>
        /// <returns>辖权节点句柄。</returns>
        public Task<ReplicaNode> WaitHomeAsync(string group)
            => WaitHomeAsync("main", group);

        /// <summary>等待指定队列组的辖权落定（HomeCmd 应用 + 引导链路）。</summary>
        /// <param name="queueName">队列名。</param>
        /// <param name="group">消费组名。</param>
        /// <returns>辖权节点句柄。</returns>
        public async Task<ReplicaNode> WaitHomeAsync(string queueName, string group)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            var fallbackAt = deadline - TimeSpan.FromSeconds(14);
            while (true)
            {
                foreach (var n in Nodes)
                {
                    if (n.Replicas.TryGetValue(queueName, out var r) && r.GroupHome(group) == n.Id)
                        return n;
                }
                // 引导兜底：leader 在位而辖权未定 → leader 直接自领（LeaderChanged 事件错过的形态；每组建至多一次）
                if (DateTime.UtcNow > fallbackAt && _bootstrapFallbacks.Add((queueName, group)))
                {
                    var leader = Nodes.FirstOrDefault(n =>
                        n.Replicas.TryGetValue(queueName, out var r) && r.RaftGroup.Raft.IsLeader
                        && r.GroupHome(group) == NodeId.Empty);
                    if (leader is not null)
                        await leader.Replicas[queueName].TakeoverGroupHomeAsync(group, default);
                }
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"组 {queueName}/{group} 辖权未落定。");
                await Task.Delay(50);
            }
        }

        /// <summary>等待指定队列组的辖权视图全节点一致（引导竞速后的稳态门——ops 前置）。</summary>
        /// <param name="queueName">队列名。</param>
        /// <param name="group">消费组名。</param>
        /// <returns>视图一致后完成。</returns>
        public Task WaitHomeViewsConvergedAsync(string queueName, string group)
            => WaitConvergedAsync(() =>
            {
                var views = Nodes.Select(n => n.Replicas.GetValueOrDefault(queueName))
                    .Where(r => r is not null)
                    .Select(r => r!.GroupHome(group))
                    .Distinct()
                    .ToList();
                return Task.FromResult(views.Count == 1 && views[0] != NodeId.Empty);
            }, TimeSpan.FromSeconds(30));   // 复制 lane 启动期指数退避——交叉节点 apply 有秒级突发延迟

        /// <summary>等待条件收敛（轮询 + 有界超时）。</summary>
        /// <param name="condition">条件。</param>
        /// <param name="timeout">超时（缺省 15s）。</param>
        /// <returns>收敛完成（超时抛）。</returns>
        public async Task WaitConvergedAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (true)
            {
                if (await condition())
                    return;
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("条件未收敛（超时）。");
                await Task.Delay(50);
            }
        }

        /// <summary>等待主队列 $default 组游标在各副本收敛到目标值（Ack 复制面）。</summary>
        /// <param name="group">消费组名。</param>
        /// <param name="cursor">目标游标。</param>
        /// <returns>收敛完成。</returns>
        public Task WaitCursorConvergedAsync(string group, LogicalAddress cursor)
            => WaitConvergedAsync(() =>
                Task.FromResult(Nodes.All(n => n.Replica.GroupCursor >= cursor)));

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            foreach (var n in Nodes)
            {
                try { await n.DisposeAsync(); } catch { /* 尽力收尾 */ }
            }
            Nodes.Clear();
            try { await Hub.DisposeAsync(); } catch { /* 尽力收尾 */ }
            if (Directory.Exists(RootDir))
            {
                try { Directory.Delete(RootDir, recursive: true); } catch { /* 临时目录尽力清理 */ }
            }
        }
    }
}
