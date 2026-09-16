using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// 租约读（RaftOptions.WithLeaseReads——线性读零往返快路径）：配置校验 / 有效窗判定 /
/// 默认关 / 快路径同步完成（租约有效时 ReadIndexAsync 不入事件队列）。
/// </summary>
public class RaftLeaseReadTests
{
    private sealed class CountingMachine : IStateMachine
    {
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed record TestNode(NodeId Id, RaftStateMachine Raft);

    private sealed record Rig(InProcessTransportHub Hub, TestNode[] Nodes, IAsyncDisposable[] Owned) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var d in Owned.Reverse())
            {
                try { await d.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await Hub.DisposeAsync();
        }
    }

    private static async Task<Rig> CreateAsync(int count, TimeSpan leaseDrift)
    {
        var hub = new InProcessTransportHub();
        var nodes = new List<TestNode>(count);
        var disposables = new List<IAsyncDisposable>();
        try
        {
            var members = Enumerable.Range(0, count).Select(_ => new ClusterMember(NodeId.NewRandom(), "")).ToArray();
            var config = new ClusterConfig(members);
            for (var i = 0; i < count; i++)
            {
                var id = members[i].Id;
                var store = new InMemoryRaftStore();
                await store.InitializeAsync();
                var transport = hub.Register(id);
                transport.Start();
                var pipeline = new ApplyPipeline(store, new CountingMachine(), ApplyPipelineOptions.Default);
                var options = RaftOptions.Default
                    .WithElectionTimeout(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000))
                    .WithHeartbeatInterval(TimeSpan.FromMilliseconds(40))
                    .WithRandom(new Random(i));
                if (leaseDrift > TimeSpan.Zero) options = options.WithLeaseReads(leaseDrift);
                var raft = new RaftStateMachine(id, store, transport, pipeline, options);
                pipeline.SetConfigCallback(raft.PostConfigChanged);
                await pipeline.StartAsync();
                await raft.StartAsync(config);
                nodes.Add(new TestNode(id, raft));
                disposables.Add(raft);
                disposables.Add(pipeline);
                disposables.Add(transport);
            }
            return new Rig(hub, [.. nodes], [.. disposables]);
        }
        catch
        {
            foreach (var d in disposables)
            {
                try { await d.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await hub.DisposeAsync();
            throw;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(30);
        }
    }

    [Fact]
    public void LeaseReads_OptionValidation()
    {
        var valid = RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000))
            .WithLeaseReads(TimeSpan.FromMilliseconds(50));
        valid.LeaseClockDriftBound.Should().Be(TimeSpan.FromMilliseconds(50));

        var tooLarge = () => RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000))
            .WithLeaseReads(TimeSpan.FromMilliseconds(500));
        tooLarge.Should().Throw<ArgumentOutOfRangeException>("漂移界 ≥ 选举窗下界——租约窗非正");

        var zero = () => RaftOptions.Default.WithLeaseReads(TimeSpan.Zero);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task LeaseRead_LeaderWithFreshQuorum_ServesFastPath()
    {
        await using var fx = await CreateAsync(3, leaseDrift: TimeSpan.FromMilliseconds(50));
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var leader = fx.Nodes.Single(n => n.Raft.IsLeader);

        // 心跳轮次到达后（40ms 间隔 ×2 新鲜窗）租约生效——窗 = 选举窗下界 500ms − 漂移 50ms = 450ms
        await WaitForAsync(() => leader.Raft.IsLeaseValid);
        leader.Raft.IsLeaseValid.Should().BeTrue("多数派心跳确认在窗内");

        // 快路径结构性判定：同步完成（事件队列路径的 ValueTask 在循环处理前不会完成）
        var vt = leader.Raft.ReadIndexAsync();
        vt.IsCompleted.Should().BeTrue("租约有效——零往返同步返回");
        (await vt).Should().Be(leader.Raft.CommitIndex);
    }

    [Fact]
    public async Task LeaseRead_DefaultDisabled_IsLeaseValidAlwaysFalse()
    {
        await using var fx = await CreateAsync(3, leaseDrift: TimeSpan.Zero);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var leader = fx.Nodes.Single(n => n.Raft.IsLeader);

        await Task.Delay(200);   // 心跳若干轮——缺省关：判定恒假
        leader.Raft.IsLeaseValid.Should().BeFalse("租约读缺省关");
        // 读仍可用（回落事件队列多数派确认路径）
        var index = await leader.Raft.ReadIndexAsync();
        index.Should().Be(leader.Raft.CommitIndex);
    }
}
