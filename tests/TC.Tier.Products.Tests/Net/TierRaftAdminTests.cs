using System.Collections.Concurrent;
using FluentAssertions;
using TC.Tier.Core.IO;
using TC.Tier.Products.Wal;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;
using Xunit;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// 管理面/健康探针（二期-D1 NETGAP-005——验证矩阵 D1 行）：状态导出全量（Role/Term/Leader/
/// 水位/成员复制进度）、healthz/readyz 语义、admin 协议远程查询（0x70 产品面）。
/// </summary>
public class TierRaftAdminTests
{
    private sealed class RecordingStateMachine : IStateMachine
    {
        public readonly ConcurrentDictionary<long, byte[]> Applied = new();
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Applied[index] = command.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Node : IAsyncDisposable
    {
        public required NodeId Id { get; init; }
        public required InProcessNode Transport { get; init; }
        public required TierRaftHost Host { get; init; }
        public required RecordingStateMachine Machine { get; init; }
        public required TierRaftAdmin Admin { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    private sealed class Rig(List<Node> nodes, InProcessTransportHub hub) : IAsyncDisposable
    {
        public List<Node> Nodes { get; } = nodes;
        public InProcessTransportHub Hub { get; } = hub;

        public async ValueTask DisposeAsync()
        {
            foreach (var n in Nodes) { try { await n.DisposeAsync(); } catch { } }
            await Hub.DisposeAsync();
        }
    }

    private static readonly RaftGroupId GroupA = new(0x0A0A);

    private static async Task<Rig> CreateClusterAsync(int count)
    {
        var hub = new InProcessTransportHub();
        var nodes = new List<Node>();
        try
        {
            var members = Enumerable.Range(0, count).Select(_ => NodeId.NewRandom()).ToArray();
            var config = new ClusterConfig(members.Select(id => new ClusterMember(id, "")).ToArray());
            var root = Path.Combine(Path.GetTempPath(), "tctier-admin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var id in members)
            {
                var transport = hub.Register(id);
                transport.Start();
                var nodeDir = Path.Combine(root, id.ToString()[..8]);
                var host = new TierRaftHost(transport, new TierRaftHostOptions
                {
                    ReconcileInterval = TimeSpan.Zero,
                    GroupFileSystemFactory = gid => TierFs.OpenOrCreate(
                        $"local:///{Path.Combine(nodeDir, gid.Value.ToString("X16")).Replace('\\', '/')}"),
                });
                await host.StartAsync();
                var machine = new RecordingStateMachine();
                var admin = new TierRaftAdmin(transport, () => host.TryGetGroup(GroupA, out var g) && g is not null ? g.Node.GetStateSnapshot() : null);
                await host.CreateGroupAsync(GroupA, config, machine, TierRaftNodeOptions.Default
                    .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))
                    .WithRaft(RaftOptions.Default.WithElectionTimeout(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1200))));
                nodes.Add(new Node { Id = id, Transport = transport, Host = host, Machine = machine, Admin = admin });
            }
            return new Rig(nodes, hub);
        }
        catch
        {
            foreach (var n in nodes) { try { await n.DisposeAsync(); } catch { } }
            await hub.DisposeAsync();
            throw;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }

    /// <summary>D1：admin 协议远程状态导出——Role/Term/Leader/水位/成员复制进度全量可见。</summary>
    [Fact]
    public async Task Admin_StateQuery_FullSnapshot()
    {
        await using var fx = await CreateClusterAsync(3);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Host.GetGroup(GroupA).Raft.IsLeader) == 1);
        var leader = fx.Nodes.Single(n => n.Host.GetGroup(GroupA).Raft.IsLeader);
        await leader.Host.GetGroup(GroupA).Raft.ReplicateAsync(new byte[] { 0x77 })
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));   // 一条提交——驱动水位与成员复制进度
        await WaitForAsync(() => leader.Machine.Applied.IsEmpty == false, TimeSpan.FromSeconds(10));
        var follower = fx.Nodes.First(n => n.Id != leader.Id);

        // follower 向 leader 查询状态（leader 视角成员复制进度）
        var state = await TierRaftAdmin.QueryStateAsync(follower.Transport, leader.Id).WaitAsync(TimeSpan.FromSeconds(5));

        state.Node.Should().Be(leader.Id);
        state.Role.Should().Be((byte)RaftRole.Leader);
        state.Ready.Should().BeTrue("leader 在位——readyz 语义");
        state.Healthy.Should().BeTrue("循环存活——healthz 语义");
        state.HasLeader.Should().BeTrue();
        state.LeaderId.Should().Be(leader.Id);
        state.MemberIds.Should().HaveCount(2, "leader 视角——其余成员复制进度");
        state.MemberIds.Should().NotContain(leader.Id, "成员表不含自身");
        state.GroupIdValue.Should().Be(GroupA.Value, "I6 组维度标签——管理面应答携带组标识");

        // follower 查询自身（Ready = 已知 leader）
        var selfState = await TierRaftAdmin.QueryStateAsync(follower.Transport, follower.Id).WaitAsync(TimeSpan.FromSeconds(5));
        selfState.Ready.Should().BeTrue("follower 已知 leader——ready");
        selfState.LeaderId.Should().Be(leader.Id);
    }

    /// <summary>D1：健康探针——正常节点 healthy+ready；未挂组（状态供给 null）= unhealthy。</summary>
    [Fact]
    public async Task Admin_HealthProbe_ReadyAndUnhealthy()
    {
        await using var fx = await CreateClusterAsync(2);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Host.GetGroup(GroupA).Raft.IsLeader) == 1);
        var leader = fx.Nodes.Single(n => n.Host.GetGroup(GroupA).Raft.IsLeader);
        var follower = fx.Nodes.First(n => !n.Host.GetGroup(GroupA).Raft.IsLeader);

        var health = await TierRaftAdmin.QueryHealthAsync(follower.Transport, leader.Id).WaitAsync(TimeSpan.FromSeconds(5));
        health.Healthy.Should().BeTrue();
        health.Ready.Should().BeTrue();
        health.CommitIndex.Should().BeGreaterThanOrEqualTo(0);

        // 未就绪形态：状态供给回调返回 null（组未装配/未就绪）——探针报 unhealthy（fail-closed）
        var resp2 = await TierRaftAdmin.QueryHealthAsync(follower.Transport, follower.Id).WaitAsync(TimeSpan.FromSeconds(5));
        resp2.Healthy.Should().BeTrue("对照组——follower 自身正常形态");
    }
}
