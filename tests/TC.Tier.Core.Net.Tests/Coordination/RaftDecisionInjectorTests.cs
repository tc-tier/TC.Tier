using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Coordination;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Tests.Fixtures;
using TC.Tier.Runtime.Transactions;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Coordination;

/// <summary>
/// 跨组事务决策注入（二期-G3——NETGAP-026 验收）：
/// ① TierSession.CommitReplicatedAsync 决策经协调组共识定案 → Confirm-all，
///    决策记录 3 节点可见 + 效果随决策复制到参与组（跨组原子呈现）；
/// ② 决策取消 → 注入器上抛（事务管线回滚路径）；
/// ③ 决策记录编码/解码往返（含空上下文）+ 畸形拒绝。
/// </summary>
public class RaftDecisionInjectorTests
{
    /// <summary>状态机：记录 applied 命令；决策记录到达时回调（转发效果到参与组）。
    /// 回调为装配后接线（构造序 state machine 先于 raft 实例）。</summary>
    private sealed class HookedStateMachine : IStateMachine
    {
        public readonly ConcurrentDictionary<long, byte[]> Applied = new();
        public Func<byte[], ValueTask>? OnDecision;

        public async ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            var copy = command.ToArray();
            Applied[index] = copy;
            if (OnDecision is not null
                && copy.Length >= RaftDecisionInjector.HeaderBytes
                && copy[0] == RaftDecisionInjector.RecordKindCommit)
                await OnDecision(copy);
        }
    }

    /// <summary>效果记录种类（组 B 业务载荷形态——[Kind 0x02][TxSeq 8B]）。</summary>
    private const byte EffectKind = 0x02;

    private sealed class GroupNode : IAsyncDisposable
    {
        public required InProcessNode Transport { get; init; }
        public required RaftGroupHost Host { get; init; }
        public required HookedStateMachine MachineA { get; init; }
        public required HookedStateMachine MachineB { get; init; }
        public required RaftStateMachine RaftA { get; init; }
        public required RaftStateMachine RaftB { get; init; }

        public async ValueTask DisposeAsync()
        {
            await RaftA.DisposeAsync();
            await RaftB.DisposeAsync();
            await Host.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    private sealed class Rig(List<GroupNode> nodes, InProcessTransportHub hub) : IAsyncDisposable
    {
        public List<GroupNode> Nodes { get; } = nodes;
        public InProcessTransportHub Hub { get; } = hub;

        public async ValueTask DisposeAsync()
        {
            foreach (var n in Nodes) { try { await n.DisposeAsync(); } catch { /* 尽力清理 */ } }
            await Hub.DisposeAsync();
        }
    }

    /// <summary>3 节点 × 2 组内存簇（装配同 RaftGroupHostTests——组 A=协调、组 B=参与）。</summary>
    private static async Task<Rig> CreateClusterAsync(int count)
    {
        var hub = new InProcessTransportHub();
        var nodes = new List<GroupNode>();
        try
        {
            var members = Enumerable.Range(0, count).Select(_ => NodeId.NewRandom()).ToArray();
            var config = new ClusterConfig(members.Select(id => new ClusterMember(id, "")).ToArray());
            foreach (var id in members)
            {
                var transport = hub.Register(id);
                transport.Start();
                var host = new RaftGroupHost(transport);
                await host.StartAsync();
                var groupA = host.CreateGroup(new RaftGroupId(0x0C0A));
                var groupB = host.CreateGroup(new RaftGroupId(0x0C0B));
                var machineA = new HookedStateMachine();
                var machineB = new HookedStateMachine();
                var storeA = new InMemoryRaftStore();
                var storeB = new InMemoryRaftStore();
                var pipelineA = new ApplyPipeline(storeA, machineA, ApplyPipelineOptions.Default);
                var pipelineB = new ApplyPipeline(storeB, machineB, ApplyPipelineOptions.Default);
                var options = RaftOptions.Default
                    .WithElectionTimeout(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800))
                    .WithRandom(new Random(id.GetHashCode()));
                var raftA = new RaftStateMachine(id, storeA, groupA, pipelineA, options);
                var raftB = new RaftStateMachine(id, storeB, groupB, pipelineB, options);
                pipelineA.SetConfigCallback(raftA.PostConfigChanged);
                pipelineB.SetConfigCallback(raftB.PostConfigChanged);
                await pipelineA.StartAsync();
                await pipelineB.StartAsync();
                await raftA.StartAsync(config);
                await raftB.StartAsync(config);
                // 跨组效果转发（编排语义）：决策 applied 且本机为协调组 leader 时——效果
                // 复制到参与组**当前 leader**。apply 回调不做阻塞外部依赖（等待组内复制
                // 会挂起决策定案；非组 B leader 时盲目 Replicate 抛 NotLeader 污染 apply）——
                // 受控异步 + 有界 NotLeader 重试。
                machineA.OnDecision = record =>
                {
                    if (!raftA.IsLeader) return ValueTask.CompletedTask;
                    var effect = EncodeEffect(BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(1, 8)));
                    _ = Task.Run(async () =>
                    {
                        for (var i = 0; i < 200; i++)
                        {
                            var bLeader = nodes.FirstOrDefault(n => n.RaftB.IsLeader);
                            if (bLeader is null) { await Task.Delay(50); continue; }
                            try { await bLeader.RaftB.ReplicateAsync(effect); return; }
                            catch (NotLeaderException) { await Task.Delay(50); }
                        }
                    });
                    return ValueTask.CompletedTask;
                };
                nodes.Add(new GroupNode
                {
                    Transport = transport, Host = host,
                    MachineA = machineA, MachineB = machineB, RaftA = raftA, RaftB = raftB,
                });
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

    private static byte[] EncodeEffect(long txSeq)
    {
        var effect = new byte[9];
        effect[0] = EffectKind;
        BinaryPrimitives.WriteInt64LittleEndian(effect.AsSpan(1, 8), txSeq);
        return effect;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null, Func<string>? onTimeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                var detail = onTimeout?.Invoke();
                throw new TimeoutException("条件未满足（超时）。" + detail);
            }
            await Task.Delay(50);
        }
    }

    private static string DumpGroup(Rig rig)
    {
        return string.Join(" | ", rig.Nodes.Select(n =>
            $"node={n.Transport.Self} A#={n.MachineA.Applied.Count} B#={n.MachineB.Applied.Count} " +
            $"Al={n.RaftA.IsLeader} Bl={n.RaftB.IsLeader} " +
            $"A=[{string.Join(",", n.MachineA.Applied.Values.Take(4).Select(v => $"{v[0]:X2}/{v.Length}b"))}] " +
            $"B=[{string.Join(",", n.MachineB.Applied.Values.Take(4).Select(v => $"{v[0]:X2}/{v.Length}b"))}]"));
    }

    /// <summary>验收①：决策经协调组共识定案——Confirm-all + 决策 3 节点可见 + 效果跨组呈现。</summary>
    [Fact]
    public async Task CommitReplicatedAsync_DecisionViaCoordinatorGroup_AtomicAcrossGroups()
    {
        await using var rig = await CreateClusterAsync(3);
        await WaitForAsync(() => rig.Nodes.Any(n => n.RaftA.IsLeader), TimeSpan.FromSeconds(15));
        var leader = rig.Nodes.First(n => n.RaftA.IsLeader);
        var injector = new RaftDecisionInjector(leader.RaftA);
        var context = Encoding.UTF8.GetBytes("groups=B");

        using var manager = SessionManager.Create(MemoryFileSystem.New(new MemoryFileSystemOptions()), "g3");
        manager.Initialize();
        manager.WaitForReady();
        using var session = manager.OpenSession();
        session.Stage(() => { });

        long seq;
        try
        {
            seq = await session.CommitReplicatedAsync(injector.CreateDecider(context))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception)
        {
            throw new TimeoutException("决策 30s 未定案。" + DumpGroup(rig));
        }

        seq.Should().Be(1, "候选 seq 随 Confirm 定案");
        // 决策记录共识传播：协调组 3 节点状态机均 applied 该决策（txSeq + 上下文一致）
        await WaitForAsync(() => rig.Nodes.All(n => n.MachineA.Applied.Values.Any(r =>
            r.Length == RaftDecisionInjector.HeaderBytes + context.Length
            && BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(1, 8)) == seq
            && r.AsSpan(RaftDecisionInjector.HeaderBytes).ToArray().SequenceEqual(context))),
            TimeSpan.FromSeconds(15), () => DumpGroup(rig));
        // 跨组效果：决策定案后效果记录在参与组（组 B）3 节点可见（leader 单点转发 + 组内复制）
        await WaitForAsync(() => rig.Nodes.All(n => n.MachineB.Applied.Values.Any(r =>
            r.Length == 9 && r[0] == EffectKind
            && BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(1, 8)) == seq)),
            TimeSpan.FromSeconds(15), () => DumpGroup(rig));
    }

    /// <summary>验收②：决策取消——注入器上抛 OperationCanceledException（事务管线回滚路径）。</summary>
    [Fact]
    public async Task DecideAsync_Cancellation_ThrowsAndLeavesNoRecord()
    {
        await using var rig = await CreateClusterAsync(1);
        var injector = new RaftDecisionInjector(rig.Nodes[0].RaftA);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var decider = injector.CreateDecider(new byte[] { 0xAA });
        var act = () => decider(1, cts.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>("取消上抛——回滚归事务管线");

        rig.Nodes[0].MachineA.Applied.Values.Should().NotContain(r => r[0] == RaftDecisionInjector.RecordKindCommit,
            "未定案不落决策记录（回滚无痕）");
    }

    /// <summary>验收③：决策记录编码/解码往返 + 畸形拒绝。</summary>
    [Fact]
    public void DecisionRecord_CodecRoundTrip_MalformedRejected()
    {
        var (txSeq, ctx) = RaftDecisionInjector.DecodeDecision(
            RaftDecisionInjector.EncodeDecision(0x1234, "ctx-π"u8));
        txSeq.Should().Be(0x1234);
        Encoding.UTF8.GetString(ctx).Should().Be("ctx-π");

        var (emptySeq, emptyCtx) = RaftDecisionInjector.DecodeDecision(
            RaftDecisionInjector.EncodeDecision(-1, ReadOnlySpan<byte>.Empty));
        emptySeq.Should().Be(-1, "负 seq（哑值）往返无损");
        emptyCtx.Should().BeEmpty("空上下文往返无损");

        var actTrunc = () => RaftDecisionInjector.DecodeDecision(new byte[] { 0x01, 0x02 });
        actTrunc.Should().Throw<FormatException>("截断记录拒绝");

        var actKind = () => RaftDecisionInjector.DecodeDecision(new byte[15]);
        actKind.Should().Throw<FormatException>("未知种类拒绝");

        var badLen = new byte[RaftDecisionInjector.HeaderBytes + 3];
        badLen[0] = RaftDecisionInjector.RecordKindCommit;
        BinaryPrimitives.WriteInt32LittleEndian(badLen.AsSpan(9, 4), 7);   // 声明 7 实带 3
        var actLen = () => RaftDecisionInjector.DecodeDecision(badLen);
        actLen.Should().Throw<FormatException>("长度失配拒绝");
    }
}
