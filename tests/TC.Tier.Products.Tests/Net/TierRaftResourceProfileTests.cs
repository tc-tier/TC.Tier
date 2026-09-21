using System.Reflection;
using TC.Tier.Core.Execution;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Node;
using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Structures.Snapshot;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// 资源档位（TierRaftResourceProfile）装配契约：低资源档 = 引擎调度器收敛到进程级共享单例
/// <c>IsolatedTaskScheduler.Shared</c>（只补缺省——显式 <c>WithWorkerScheduler</c> 注入恒优先）；
/// 高性能档（缺省）= 缺省自建形态零改动；功能面（复制提交）在共享调度器上零回归。
/// 引擎与调度器均 internal 面——全反射取证（契约断言，同 TierWalWorkerSchedulerTests 惯例）。
/// </summary>
public sealed class TierRaftResourceProfileTests
{
    private static readonly FieldInfo LogEngineField =
        typeof(LogBase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo WalSnapshotField =
        typeof(TierWal).GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo SnapshotEngineField =
        typeof(SnapshotBase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly PropertyInfo EngineSchedulerProperty =
        typeof(LogBase).Assembly.GetTypes()
            .Single(t => t.Name == "StorageEngine")
            .GetProperty("WorkerScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static IsolatedTaskScheduler? MainEngineScheduler(TierRaftNode node) =>
        (IsolatedTaskScheduler?)EngineSchedulerProperty.GetValue(LogEngineField.GetValue(node.Wal.DiagnosticLog)!);

    private static IsolatedTaskScheduler? SnapshotEngineScheduler(TierRaftNode node) =>
        (IsolatedTaskScheduler?)EngineSchedulerProperty.GetValue(
            SnapshotEngineField.GetValue((IncrementalSnapshot)WalSnapshotField.GetValue(node.Wal)!)!);

    /// <summary>计数状态机（apply 面观测——at-least-once 容忍）。</summary>
    private sealed class CountingMachine : IStateMachine
    {
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>N=1 Standalone 启动（零选举——单成员配置直接 leader，单测确定性）。</summary>
    private static async Task<(TierRaftNode Node, InProcessTransportHub Hub)> StartStandaloneAsync(
        TierRaftNodeOptions options)
    {
        var hub = new InProcessTransportHub();
        try
        {
            var member = new ClusterMember(NodeId.NewRandom(), "");
            var transport = hub.Register(member.Id);
            var node = await TierRaftNode.StartAsync(member.Id, TierFs.New("memory:"), transport,
                new ClusterConfig([member]), new CountingMachine(), options).WaitAsync(TimeSpan.FromSeconds(30));
            return (node, hub);
        }
        catch
        {
            await hub.DisposeAsync();
            throw;
        }
    }

    /// <summary>双 Host 低资源组：各组引擎持 Host 专属调度器实例（Host 间隔离、非进程 Shared、
    /// 几何随档收缩）——Host 级作用域 vs 进程级 Shared 的判别面。</summary>
    [Fact]
    public async Task LowResource_TwoHosts_EachGroupUsesOwnHostScheduler()
    {
        var hubs = new List<InProcessTransportHub>();
        var hosts = new List<TC.Tier.Products.Net.Host.TierRaftHost>();
        var pageSizes = new List<int>();
        var schedulers = new List<IsolatedTaskScheduler?>();
        try
        {
            foreach (var hostIndex in new[] { 0, 1 })
            {
                var hub = new InProcessTransportHub();
                hubs.Add(hub);
                var id = NodeId.NewRandom();
                var transport = hub.Register(id);
                var host = new TC.Tier.Products.Net.Host.TierRaftHost(transport,
                    new TC.Tier.Products.Net.Host.TierRaftHostOptions
                    {
                        GroupFileSystemFactory = _ => TierFs.New("memory:"),
                        ReconcileInterval = TimeSpan.Zero,
                    });
                hosts.Add(host);
                await host.StartAsync();

                var groupOptions = TierRaftNodeOptions.Default
                    .WithResourceProfile(TierRaftResourceProfile.LowResource);
                await host.CreateGroupAsync(new RaftGroupId((ulong)(0x70 + hostIndex)),
                    new ClusterConfig([new ClusterMember(id, "")]), new CountingMachine(), groupOptions);

                var group = host.GetGroup(new RaftGroupId((ulong)(0x70 + hostIndex)));
                schedulers.Add(MainEngineScheduler(group.Node));
                pageSizes.Add(group.Node.Wal.DiagnosticLog.PageSize);
            }

            // Host 间隔离：各自专属实例（非进程 Shared、互不相同）
            schedulers[0].Should().NotBeSameAs(IsolatedTaskScheduler.Shared, "Host 作用域 ≠ 进程级 Shared");
            schedulers[1].Should().NotBeSameAs(IsolatedTaskScheduler.Shared);
            schedulers[0].Should().NotBeSameAs(schedulers[1], "双 Host 各自专属实例——互不抢线程");
            pageSizes.Should().OnlyContain(p => p == 1 << 20, "几何预设两 Host 同等生效");
        }
        finally
        {
            foreach (var h in hosts) { try { await h.DisposeAsync(); } catch { } }
            foreach (var h in hubs) { try { await h.DisposeAsync(); } catch { } }
        }
    }

    [Fact]
    public void Options_ResourceProfile_DefaultAndImmutableWithChain()
    {
        TierRaftNodeOptions.Default.ResourceProfile.Should().Be(TierRaftResourceProfile.HighPerformance,
            "缺省档 = 高性能（现网行为零变化）");

        var low = TierRaftNodeOptions.Default.WithResourceProfile(TierRaftResourceProfile.LowResource);
        low.ResourceProfile.Should().Be(TierRaftResourceProfile.LowResource);
        TierRaftNodeOptions.Default.ResourceProfile.Should().Be(TierRaftResourceProfile.HighPerformance,
            "不可变 With 链——原实例不变");
    }

    [Fact]
    public async Task LowResource_Node_EnginesShareProcessScheduler()
    {
        var (node, hub) = await StartStandaloneAsync(
            TierRaftNodeOptions.Default.WithResourceProfile(TierRaftResourceProfile.LowResource));
        try
        {
            node.Raft.IsLeader.Should().BeTrue("N=1 Standalone 启动即 leader");

            // 主日志/快照引擎全部持进程级共享单例（档位展开 = 只补缺省）
            MainEngineScheduler(node).Should().BeSameAs(IsolatedTaskScheduler.Shared, "主日志引擎");
            SnapshotEngineScheduler(node).Should().BeSameAs(IsolatedTaskScheduler.Shared, "镜像快照引擎");

            // 几何随档收缩（Wal 未定制 = 预设全量生效）：页 1MB / 快照段 8MB / worker 消费者 1
            node.Wal.DiagnosticLog.PageSize.Should().Be(1 << 20, "低资源档页模型收缩 4MB→1MB");

            // 功能面零回归：共享调度器上复制提交照常收敛
            for (var i = 0; i < 3; i++)
                await node.Raft.ReplicateCommittedAsync(BitConverter.GetBytes(i))
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            node.Wal.PersistedIndex.Should().BeGreaterThanOrEqualTo(3);
        }
        finally
        {
            await node.DisposeAsync();
            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task LowResource_CustomizedWal_GeometryUserSovereign()
    {
        // 用户已定制 Wal（页 18 位）+ 低资源档：调度器仍补缺省（Shared），几何不越权改写
        var (node, hub) = await StartStandaloneAsync(
            TierRaftNodeOptions.Default
                .WithResourceProfile(TierRaftResourceProfile.LowResource)
                .WithWal(TC.Tier.Products.Wal.TierWalOptions.Default.WithLogPageSizeBits(18)));
        try
        {
            MainEngineScheduler(node).Should().BeSameAs(IsolatedTaskScheduler.Shared, "调度器缺省仍补");
            node.Wal.DiagnosticLog.PageSize.Should().Be(1 << 18, "用户主权——几何保持用户指定值");
        }
        finally
        {
            await node.DisposeAsync();
            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task LowResource_ExplicitInjection_WinsOverProfile()
    {
        using var custom = IsolatedTaskScheduler.Create(
            new IsolatedSchedulerOptions { Name = "profile-explicit-wins", ThreadCount = 2 });
        var (node, hub) = await StartStandaloneAsync(
            TierRaftNodeOptions.Default
                .WithResourceProfile(TierRaftResourceProfile.LowResource)
                .WithWorkerScheduler(custom));
        try
        {
            // 显式注入恒优先——档位不覆盖（只补缺省语义）
            MainEngineScheduler(node).Should().BeSameAs(custom, "显式注入优先于档位");
            SnapshotEngineScheduler(node).Should().BeSameAs(custom, "显式注入优先于档位");
            MainEngineScheduler(node).Should().NotBeSameAs(IsolatedTaskScheduler.Shared);
        }
        finally
        {
            await node.DisposeAsync();
            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task HighPerformance_Default_EnginesSelfBuilt()
    {
        var (node, hub) = await StartStandaloneAsync(TierRaftNodeOptions.Default);
        try
        {
            // 缺省档 = 自建形态（非空但非共享单例）——现网行为零变化
            MainEngineScheduler(node).Should().NotBeNull();
            MainEngineScheduler(node).Should().NotBeSameAs(IsolatedTaskScheduler.Shared);
            SnapshotEngineScheduler(node).Should().NotBeNull();
            SnapshotEngineScheduler(node).Should().NotBeSameAs(IsolatedTaskScheduler.Shared);
        }
        finally
        {
            await node.DisposeAsync();
            await hub.DisposeAsync();
        }
    }
}
