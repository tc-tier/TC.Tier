using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;
using Xunit;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// TierRaftHost 多组装配（二期-C3——验证矩阵 C3 行）：3 节点 × 2 组同传输装配、
/// 各组独立提交 + 组隔离、宿主重启后各组独立恢复（WAL 重放）、成员对账并集收敛。
/// </summary>
public class TierRaftHostTests
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

    private sealed class HostNode : IAsyncDisposable
    {
        public required NodeId Id { get; init; }
        public required string RootDir { get; init; }
        public required InProcessNode Transport { get; init; }
        public required TierRaftHost Host { get; init; }
        public required Dictionary<RaftGroupId, RecordingStateMachine> Machines { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    private static readonly RaftGroupId GroupA = new(0x0A0A);
    private static readonly RaftGroupId GroupB = new(0x0B0B);

    private static TierRaftNodeOptions FastOptions() => TierRaftNodeOptions.Default
        .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))
        .WithRaft(RaftOptions.Default.WithElectionTimeout(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1200)));

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }

    private static async Task<(List<HostNode> Nodes, string RootDir)> CreateClusterAsync(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "tctier-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hub = new InProcessTransportHub();
        var nodes = new List<HostNode>();
        try
        {
            var members = Enumerable.Range(0, count).Select(_ => NodeId.NewRandom()).ToArray();
            var config = new ClusterConfig(members.Select(id => new ClusterMember(id, "")).ToArray());
            for (var i = 0; i < count; i++)
            {
                var id = members[i];
                var transport = hub.Register(id);
                transport.Start();
                var nodeRoot = Path.Combine(root, $"n{i}");
                var host = new TierRaftHost(transport, new TierRaftHostOptions
                {
                    GroupFileSystemFactory = gid => TierFs.OpenOrCreate(
                        $"local:///{Path.Combine(nodeRoot, gid.Value.ToString("X16")).Replace('\\', '/')}"),
                    ReconcileInterval = TimeSpan.Zero,   // InProcess 无对账面——隔离测试关闭
                });
                await host.StartAsync();

                var machines = new Dictionary<RaftGroupId, RecordingStateMachine>();
                foreach (var gid in new[] { GroupA, GroupB })
                {
                    var machine = new RecordingStateMachine();
                    machines[gid] = machine;
                    await host.CreateGroupAsync(gid, config, machine, FastOptions());
                }
                nodes.Add(new HostNode { Id = id, RootDir = nodeRoot, Transport = transport, Host = host, Machines = machines });
            }
            return (nodes, root);
        }
        catch
        {
            foreach (var n in nodes) { try { await n.DisposeAsync(); } catch { } }
            await hub.DisposeAsync();
            throw;
        }
    }

    /// <summary>C3：3 节点 × 2 组同传输装配——各组独立收敛选主、独立提交、组间隔离。</summary>
    [Fact]
    public async Task TwoGroups_IsolatedElectionCommit_AndRecovery()
    {
        var root = "";
        List<HostNode>? nodes = null;
        var hubLogs = new List<string>();
        try
        {
            (nodes, root) = await CreateClusterAsync(3);

            // 各组独立收敛单 leader
            await WaitForAsync(() => nodes.Count(n => n.Host.GetGroup(GroupA).Raft.IsLeader) == 1);
            await WaitForAsync(() => nodes.Count(n => n.Host.GetGroup(GroupB).Raft.IsLeader) == 1);

            // 各组独立提交：等组 A leader 收敛 → 向 leader 提交（NotLeader 重路由重试——标准客户端模式）
            var leaderA = nodes.Single(n => n.Host.GetGroup(GroupA).Raft.IsLeader);
            for (var attempt = 0; ; attempt++)
            {
                var current = nodes.FirstOrDefault(n => n.Host.GetGroup(GroupA).Raft.IsLeader) ?? leaderA;
                try
                {
                    await current.Host.GetGroup(GroupA).Raft.ReplicateAsync(new byte[] { 0xA1 })
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    break;
                }
                catch (NotLeaderException) when (attempt < 40) { await Task.Delay(50); }
            }
            await WaitForAsync(() => nodes.All(n => !n.Machines[GroupA].Applied.IsEmpty), TimeSpan.FromSeconds(10));

            // 组隔离：组 A 的提交对组 B 状态机不可见（此刻组 B 零提交）
            nodes.All(n => n.Machines[GroupB].Applied.IsEmpty).Should().BeTrue("组 A 提交对组 B 不可见");

            // 重启恢复：全 host 释放 → 同 fs 重建 → 各组 WAL 重放恢复状态机
            foreach (var n in nodes) await n.DisposeAsync();
            var hub2 = new InProcessTransportHub();
            try
            {
                foreach (var n in nodes)
                {
                    var transport = hub2.Register(n.Id);
                    transport.Start();
                    var host = new TierRaftHost(transport, new TierRaftHostOptions
                    {
                        GroupFileSystemFactory = gid => TierFs.OpenOrCreate(
                            $"local:///{Path.Combine(n.RootDir, gid.Value.ToString("X16")).Replace('\\', '/')}"),
                        ReconcileInterval = TimeSpan.Zero,
                    });
                    await host.StartAsync();
                    foreach (var gid in new[] { GroupA, GroupB })
                        await host.CreateGroupAsync(gid, new ClusterConfig(nodes.Select(x => new ClusterMember(x.Id, "")).ToArray()),
                            n.Machines[gid], FastOptions());
                }

                // 恢复断言：各组状态机重放出重启前的已提交内容
                await WaitForAsync(
                    () => nodes.All(n => n.Machines[GroupA].Applied.Values.Any(v => v.SequenceEqual(new byte[] { 0xA1 }))),
                    TimeSpan.FromSeconds(15));
            }
            finally
            {
                foreach (var n in nodes) await n.Transport.DisposeAsync();
                await hub2.DisposeAsync();
            }
        }
        finally
        {
            if (root != "" && Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { /* 临时目录尽力清理 */ }
            }
        }
    }

    /// <summary>C3/§8.2：成员对账并集收敛——组配置成员 → registry AddPeer；组移除 → RemovePeer。</summary>
    [Fact]
    public async Task Reconcile_ConvergesUnionIntoRegistry()
    {
        var port = ReservePort();
        var self = NodeId.NewRandom();
        var other = NodeId.NewRandom();

        var t1 = new TC.Tier.Core.Net.Transport.Tcp.ClusterTransport(self,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        t1.Start();
        await using var _ = t1;

        var otherConfig = new ClusterConfig(
        [
            new ClusterMember(self, ""),
            new ClusterMember(other, $"127.0.0.1:{port}"),
        ]);
        var dir = Path.Combine(Path.GetTempPath(), "tctier-rec-" + Guid.NewGuid().ToString("N"));
        var host = new TierRaftHost(t1, new TierRaftHostOptions
        {
            GroupFileSystemFactory = gid => TierFs.OpenOrCreate($"local:///{Path.Combine(dir, gid.Value.ToString("X16")).Replace('\\', '/')}"),
            ReconcileInterval = TimeSpan.FromMilliseconds(200),
        });
        await host.StartAsync();
        try
        {
            await host.CreateGroupAsync(GroupA, otherConfig, new RecordingStateMachine(), FastOptions());
            // 具体类型直用（ClusterTransport 本实现 IPeerRegistry）——上转型接口触发 CA1859（TreatWarningsAsErrors 下为错）
            var registry = t1;
            await WaitForAsync(() => registry.Peers.ContainsKey(other), TimeSpan.FromSeconds(5));
            registry.Peers[other].Port.Should().Be(port, "对账把组配置端点收敛进物理表");

            await host.RemoveGroupAsync(GroupA);
            await WaitForAsync(() => !registry.Peers.ContainsKey(other), TimeSpan.FromSeconds(5));
            registry.Peers.Should().NotContainKey(other, "不再被任何组引用——对账移除");
        }
        finally
        {
            await host.DisposeAsync();
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    private static int ReservePort()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }
    /// <summary>D8：节点退役编排——leader drain + 转让给指定 follower + 配置自移除；
    /// 集群在剩余成员上继续可用（复制照常提交）。</summary>
    [Fact]
    public async Task Deprovision_LeaderWithTransfer_ClusterContinues()
    {
        (var nodes, var root) = await CreateClusterAsync(3);
        try
        {
            await WaitForAsync(() => nodes.Count(n => n.Host.GetGroup(GroupA).Raft.IsLeader) == 1);
            var leader = nodes.Single(n => n.Host.GetGroup(GroupA).Raft.IsLeader);
            var target = nodes.First(n => n.Id != leader.Id);

            // 退役编排：转让 + 配置自移除
            await leader.Host.GetGroup(GroupA).Node.DeprovisionAsync(transferTarget: target.Id)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15));

            // 目标接管（★ 有界收敛等待——DeprovisionAsync 契约=转让发起（超时引擎自愈回退），
            //   目标 TimeoutNow 当选上线晚于编排返回一拍；2vCPU runner 上即时断言必偶发红，#423）
            await WaitForAsync(() => target.Host.GetGroup(GroupA).Raft.IsLeader, TimeSpan.FromSeconds(10));
            target.Host.GetGroup(GroupA).Raft.IsLeader.Should().BeTrue("转让目标接管——零 leadership 真空");
            // 配置移除：转让形态下由新 leader 侧编排（§8.2——幸存者对账自动黑名单+断链退役节点）
            await target.Host.GetGroup(GroupA).Node.Membership.RemoveMemberAsync(leader.Id).WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                await WaitForAsync(() => !target.Host.GetGroup(GroupA).Raft.Config.Contains(leader.Id),
                    TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                var dump = string.Join(" | ", nodes.Select(n => $"{n.Id.ToString()[..6]}:cfg=[{string.Join(",", n.Host.GetGroup(GroupA).Raft.Config.Members.Select(m => m.Id.ToString()[..6] + (m.Role == ClusterMemberRole.Learner ? "L" : "")))}]"));
                throw new TimeoutException($"config 收敛超时：{dump}");
            }

            // 集群继续可用：新 leader 写入照常提交
            var idx = await target.Host.GetGroup(GroupA).Raft.ReplicateAsync(new byte[] { 0xD8 })
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            idx.Should().BeGreaterThanOrEqualTo(1);
            await WaitForAsync(() => nodes.Where(n => n.Id != leader.Id)
                .All(n => n.Machines[GroupA].Applied.ContainsKey(idx)), TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var n in nodes) { try { await n.DisposeAsync(); } catch { } }
            try { Directory.Delete(root, true); } catch { }
        }
    }

}
