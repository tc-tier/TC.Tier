using System.Collections.Concurrent;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.AdversarialTests.Raft;

/// <summary>
/// Snapshot install engine scenarios (spec-03 §2 × spec-12 §6/§6.1——cold node late-start catches
/// up through the InstallSnapshot handshake: leader export → handshake RPC → follower import +
/// rebuild → incremental tail; both data planes wired into the engine — single-source stream
/// and swarm (manifest+holders through the handshake face) produce identical business state).
/// <para>★ Scenario determinism (spec-09 §2 #5): late start stands in for partition — no term
/// races, no double-follower interleavings.</para>
/// </summary>
public class SnapshotInstallTests
{
    private sealed class RecordingMachine : IStateMachine
    {
        public readonly ConcurrentDictionary<long, byte[]> Applied = new();

        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Applied[index] = command.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestNode : IAsyncDisposable
    {
        public required NodeId Id { get; init; }
        public required InMemoryRaftStore Store { get; init; }
        public required InProcessNode Transport { get; init; }
        public required RecordingMachine Machine { get; init; }
        public required ApplyPipeline Pipeline { get; init; }
        public required RaftStateMachine Raft { get; init; }
        public SnapshotStreamTransfer? Stream { get; init; }
        public SwarmSync? Swarm { get; init; }
        public SnapshotSwarmTransfer? SwarmTransfer { get; init; }

        /// <summary>启动（pipeline → raft——晚启动场景对冷节点延后调用）。</summary>
        public async Task StartAsync(ClusterConfig config)
        {
            await Pipeline.StartAsync().ConfigureAwait(false);
            await Raft.StartAsync(config).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await Raft.DisposeAsync().ConfigureAwait(false);
            await Pipeline.DisposeAsync().ConfigureAwait(false);
            if (Stream is not null) await Stream.DisposeAsync().ConfigureAwait(false);
            if (Swarm is not null) await Swarm.DisposeAsync().ConfigureAwait(false);
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record ClusterRig(InProcessTransportHub Hub, TestNode[] Nodes) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var n in Nodes)
            {
                try { await n.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await Hub.DisposeAsync();
        }
    }

    /// <summary>创建 n 节点并启动前 startCount 个（多数派先行——余下冷节点晚启动）。</summary>
    private static async Task<ClusterRig> CreateClusterAsync(int count, int startCount, int seed = 42)
    {
        var hub = new InProcessTransportHub();
        var nodes = new TestNode[count];
        try
        {
            var members = new ClusterMember[count];
            for (var i = 0; i < count; i++) members[i] = new ClusterMember(NodeId.NewRandom(), "");
            var config = new ClusterConfig(members);
            for (var i = 0; i < count; i++)
            {
                nodes[i] = await BuildNodeAsync(hub, members[i].Id, seed + i, swarm: true).ConfigureAwait(false);
                if (i < startCount) await nodes[i].StartAsync(config).ConfigureAwait(false);
            }
            return new ClusterRig(hub, nodes);
        }
        catch
        {
            foreach (var n in nodes.Where(x => x is not null)) await n.DisposeAsync();
            await hub.DisposeAsync();
            throw;
        }
    }

    /// <summary>单节点装配（swarm = true 时双数据面齐挂——swarm 传输面 + 流式传输面对照）。</summary>
    private static async Task<TestNode> BuildNodeAsync(InProcessTransportHub hub, NodeId id,
        int seed, bool swarm)
    {
        var store = new InMemoryRaftStore();
        await store.InitializeAsync().ConfigureAwait(false);
        var transport = hub.Register(id);
        transport.Start();
        var machine = new RecordingMachine();
        var pipeline = new ApplyPipeline(store, machine, ApplyPipelineOptions.Default);

        // 流式传输面（单源基线——双形态节点都挂，装配面按集群策略选用一个注入引擎）
        var stream = new SnapshotStreamTransfer(transport, store);
        await stream.StartAsync().ConfigureAwait(false);

        // swarm 传输面（多源——manifest+holders 经 InstallSnapshot 握手面交换）
        SwarmSync? swarmSync = null;
        SnapshotSwarmTransfer? swarmTransfer = null;
        if (swarm)
        {
            swarmSync = new SwarmSync(transport);
            await swarmSync.StartAsync().ConfigureAwait(false);
            var snapshotSwarm = new SnapshotSwarmSync(swarmSync, store);
            swarmTransfer = new SnapshotSwarmTransfer(transport, snapshotSwarm, store, blockSize: 4096);
        }

        var raft = new RaftStateMachine(id, store, transport, pipeline,
            RaftOptions.Default.WithRandom(new Random(seed + 1))
                // ★ 负载容忍选举窗（判例 2026-09-02——同 RaftEngineTests 夹具注释）
                .WithElectionTimeout(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)),
            snapshotTransfer: swarm ? swarmTransfer : stream);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        return new TestNode
        {
            Id = id, Store = store, Transport = transport, Machine = machine,
            Pipeline = pipeline, Raft = raft, Stream = stream,
            Swarm = swarmSync, SwarmTransfer = swarmTransfer,
        };
    }

    private static byte[] Cmd(int i) => System.Text.Encoding.UTF8.GetBytes($"cmd-{i:D4}");

    /// <summary>节点状态快照（冻结取证三件套扩展判例 2026-09-03）：角色/任期/水位/loopEx +
    /// 循环活性诊断（LoopLag/TickLag/QueueDepth/DeadlineIn——tick 链断 vs 循环卡 handler 判别）
    /// + 选举轨迹（转换级：超时/预票收发/多数派/真选举/投票/当选）。</summary>
    private static string NodeState(TestNode n)
    {
        var d = n.Raft.DiagnoseLoop();
        return $"{n.Id.ToString()[..6]}:{n.Raft.Role}/t{n.Raft.CurrentTerm}/c{n.Raft.CommitIndex}/a{n.Store.AppliedIndex}/p{n.Store.PersistedIndex}/s{n.Store.SnapshotIndex}/l{n.Store.LastLogIndex}" +
               $"/loopLag={d.LoopLagMs}/tickLag={d.TickLagMs}/q={d.QueueDepth}/tickFail={d.TickWriteFailures}/dl={d.DeadlineInMs}/loopEx={n.Raft.LoopException?.Message ?? "∅"}" +
               $"/trace=[{n.Raft.DumpElectionTrace()}]";
    }

    private static string ClusterState(ClusterRig fx)
        => string.Join(" | ", fx.Nodes.Select(NodeState));

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null, Func<string>? diagnose = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                TC.Tier.Core.Net.Tests.Fixtures.FreezeForensics.Hold();   // ★ 取证保持（TC_NET_FREEZE_HOLD 门控——常规跑直通）
                throw new TimeoutException($"条件未满足（超时）。{(diagnose is { } d ? d() : string.Empty)}");
            }
            await Task.Delay(50);
        }
    }

    private static TestNode? TryLeader(IEnumerable<TestNode> nodes)
        => nodes.FirstOrDefault(n => n.Raft.IsLeader);

    /// <summary>
    /// 安装追平（spec-03）：leader+healthy 先跑（60 条 + 快照压缩）→ cold 晚启动（空日志）→
    /// nextIndex=1 ≤ N₀=60 → 快照安装（导出 → InstallSnapshot 握手 → 导入重建）→ 增量追平。
    /// 双形态（流式/多源）同场景同断言——产物逐字节一致。
    /// </summary>
    public static TheoryData<string, bool> DataPlanes => new()
    {
        { "stream", false },
        { "swarm", true },
    };


    /// <summary>写入单条 + NotLeader 重试（重选 leader——满载选举抖动窗口容忍，spec-08 客户端模式）。</summary>
    private static async Task ReplicateWithLeaderRetryAsync(ClusterRig fx, Func<TestNode?> leaderOf, int i)
    {
        for (var attempt = 0; ; attempt++)
        {
            var leader = leaderOf();
            if (leader is null)
            {
                if (attempt >= 60) throw new TimeoutException("选举收敛超时（无 leader）。");
                Thread.Sleep(50);
                continue;
            }
            try
            {
                await leader.Raft.ReplicateAsync(Cmd(i)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                return;
            }
            catch (NotLeaderException) when (attempt < 60)
            {
                Thread.Sleep(50);   // 换届窗口——重查 leader 重试（spec-08 客户端模式）
            }
            catch (TimeoutException) when (attempt < 60)
            {
                // ★ 挂起取证（判例 2026-09-02）：5s 未完成 = 复制挂起——重试前留一轮全节点状态
                //   快照（角色/任期/提交/应用/持久化/快照水位 + 循环活性 + 循环异常）——flaky 定位三件套
                var state = ClusterState(fx);
                TC.Tier.Core.Net.Tests.Fixtures.FreezeForensics.Hold();   // ★ 取证保持（TC_NET_FREEZE_HOLD 门控）
                throw new TimeoutException($"复制挂起（第 {i} 条 5s 未完成——leader={leader.Id.ToString()[..6]}）：{state}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(DataPlanes))]
    public async Task InstallSnapshot_ColdNodeLateStart_CatchesUpConsistent(string mode, bool swarm)
    {
        await using var fx = await CreateClusterAsync(3, startCount: 2);
        await WaitForAsync(() => fx.Nodes.Take(2).Count(n => n.Raft.IsLeader) == 1,
            diagnose: () => ClusterState(fx));
        var leader = TryLeader(fx.Nodes.Take(2))!;   // WaitForAsync 已确认恰一 leader
        var healthy = fx.Nodes.First(n => n != leader);
        var cold = fx.Nodes.First(n => n != leader && n != healthy);
        var config = leader.Raft.Config;

        // 1. 两节点先跑（多数派 2/3——cold 晚启动）：leader 写 60 条。
        //    ★ NotLeader 重试（spec-08 客户端标准模式）：满载下选举抖动 leader 短暂退位——
        //      直调即抛 NotLeaderException（flaky 判例 2026-09-02），重试等新 leader/复位。
        for (var i = 1; i <= 60; i++)
            await ReplicateWithLeaderRetryAsync(fx, () => TryLeader(fx.Nodes), i);   // ★ 全节点选 leader（冷节点当选合法——判例 2026-09-02：Take(2) 排除冷节点 → 其当选期无 leader 可查）
        await WaitForAsync(() => healthy.Machine.Applied.Count >= 60, diagnose: () => ClusterState(fx));

        // 2. 快照压缩（装配层策略触发——fixture 直接推进水位；导出时幂等）
        await leader.Store.TruncatePrefixToAsync(60);
        leader.Store.SnapshotIndex.Should().Be(60);

        // 3. cold 晚启动（空日志）→ 心跳推批：nextIndex 从 1 起 ≤ leader SnapshotIndex(60)
        //    → 快照安装（导出 → InstallSnapshotReq（swarm 形态携 manifest+holders）→ 导入重建 → 应答 → 增量追平）
        await cold.StartAsync(config);
        await WaitForAsync(() => cold.Raft.CommitIndex >= 60, TimeSpan.FromSeconds(20), () => ClusterState(fx));
        await WaitForAsync(() => cold.Machine.Applied.Count >= 60, TimeSpan.FromSeconds(20), () => ClusterState(fx));

        // 4. 一致性：业务状态 ≥60 条（快照区重放 60——NotLeader 重试可能产生同载荷重复条目，
        //    at-least-once 客户端模式——前缀 60 条逐条一致；精确计数断言被换届重试窗口击穿）。
        //    ★ leader 自身 apply 可落后 healthy（apply 与复制解耦——判例 2026-09-02 混跑实锤
        //    59/60）——先等 leader 追平再对比
        await WaitForAsync(() => leader.Machine.Applied.Count >= 60, TimeSpan.FromSeconds(20), () => ClusterState(fx));
        var coldApplied = cold.Machine.Applied.OrderBy(kv => kv.Key).ToArray();
        var leaderApplied = leader.Machine.Applied.OrderBy(kv => kv.Key).ToArray();
        coldApplied.Should().HaveCountGreaterOrEqualTo(60);
        leaderApplied.Should().HaveCountGreaterOrEqualTo(60);
        for (var i = 0; i < 60; i++)
        {
            coldApplied[i].Key.Should().Be(leaderApplied[i].Key, $"{mode}——apply 同 index");
            coldApplied[i].Value.Should().Equal(leaderApplied[i].Value, $"{mode}——index {coldApplied[i].Key} 内容一致");
        }

        // 5. 增量继续追平（61..70）——NotLeader 重试同上（换届窗口容忍）
        for (var i = 61; i <= 70; i++)
            await ReplicateWithLeaderRetryAsync(fx, () => TryLeader(fx.Nodes), i);   // ★ 全节点选 leader（冷节点当选合法——判例 2026-09-02：Take(2) 排除冷节点 → 其当选期无 leader 可查）
        await WaitForAsync(() => cold.Machine.Applied.Count >= 70, TimeSpan.FromSeconds(20), () => ClusterState(fx));
        cold.Raft.CommitIndex.Should().BeGreaterOrEqualTo(70);
    }
}
