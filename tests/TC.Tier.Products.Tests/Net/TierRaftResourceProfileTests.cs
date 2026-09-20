using System.Reflection;
using TC.Tier.Core.Execution;
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
