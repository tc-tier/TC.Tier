using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// leadership 控制面（主动让位 ResignAsync + LeadershipToken 换届感知令牌）：
/// 让位降级 / 令牌生命周期（非 leader 已取消 → 执政有效 → 降级取消）/ 非 leader fail-fast /
/// 集群自愈重选。
/// </summary>
public class RaftLeadershipControlTests
{
    private sealed class CountingMachine : IStateMachine
    {
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed record TestNode(NodeId Id, RaftStateMachine Raft, InProcessNode Transport, IAsyncDisposable[] Owned);

    private sealed record Rig(InProcessTransportHub Hub, TestNode[] Nodes) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var n in Nodes)
                foreach (var owned in n.Owned.Reverse())
                {
                    try { await owned.DisposeAsync(); } catch { /* 尽力清理 */ }
                }
            await Hub.DisposeAsync();
        }
    }

    private static async Task<Rig> CreateAsync(int count)
    {
        var hub = new InProcessTransportHub();
        var nodes = new List<TestNode>(count);
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
                    .WithElectionTimeout(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600))
                    .WithRandom(new Random(i));
                var raft = new RaftStateMachine(id, store, transport, pipeline, options);
                pipeline.SetConfigCallback(raft.PostConfigChanged);
                await pipeline.StartAsync();
                await raft.StartAsync(config);
                nodes.Add(new TestNode(id, raft, transport,
                    [raft, pipeline, transport]));
            }
            return new Rig(hub, [.. nodes]);
        }
        catch
        {
            foreach (var n in nodes)
                foreach (var owned in n.Owned.Reverse())
                {
                    try { await owned.DisposeAsync(); } catch { /* 尽力清理 */ }
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
            await Task.Delay(40);
        }
    }

    private static TestNode Leader(Rig fx) => fx.Nodes.Single(n => n.Raft.IsLeader);

    [Fact]
    public async Task Resign_LeaderStepsDown_ClusterReElects()
    {
        await using var fx = await CreateAsync(3);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var resigned = Leader(fx);
        var tokenBefore = resigned.Raft.LeadershipToken;
        tokenBefore.IsCancellationRequested.Should().BeFalse("执政期令牌有效");

        await resigned.Raft.ResignAsync();

        resigned.Raft.IsLeader.Should().BeFalse("让位即降级");
        tokenBefore.IsCancellationRequested.Should().BeTrue("令牌随降级取消——挂其上的后台任务立即感知");

        // 集群自愈：选举窗后重新收敛出唯一 leader（可能是原 leader 复位——raft 语义允许）
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
    }

    [Fact]
    public async Task LeadershipToken_Lifecycle_PerTerm()
    {
        await using var fx = await CreateAsync(3);
        // 选举窗前：非 leader = 已取消令牌
        fx.Nodes.Should().Contain(n => n.Raft.LeadershipToken.IsCancellationRequested, "未执政 = 已取消令牌");

        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var leader = Leader(fx);
        var token = leader.Raft.LeadershipToken;
        token.IsCancellationRequested.Should().BeFalse("执政期令牌有效");
        leader.Raft.LeadershipToken.Should().Be(token, "同执政期令牌稳定（缓存）");

        await leader.Raft.ResignAsync();
        token.IsCancellationRequested.Should().BeTrue("降级取消");

        // 重新当选 → 换新令牌（取消语义不跨任期残留）
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var newLeader = Leader(fx);
        newLeader.Raft.LeadershipToken.IsCancellationRequested.Should().BeFalse("新任期新令牌");
    }

    [Fact]
    public async Task Resign_NonLeader_FailsFast()
    {
        await using var fx = await CreateAsync(3);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var follower = fx.Nodes.First(n => !n.Raft.IsLeader);

        var act = async () => await follower.Raft.ResignAsync();
        await act.Should().ThrowAsync<NotLeaderException>("无位可让——明确失败而非幂等静默");
    }
}
