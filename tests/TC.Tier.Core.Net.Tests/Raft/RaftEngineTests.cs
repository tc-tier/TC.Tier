using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;
using TC.Tier.Core.Metrics;
using TC.Tier.Core.Observability;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Raft engine scenario tests (spec-01..05 semantics on the rewired engine — InMemoryRaftStore
/// fixture + InProcess medium, spec-12 §13.1 fixture ladder): election single-leader agreement,
/// replication identical ordering, leader failure failover, partition quorum loss + heal,
/// ReadIndex, membership changes, non-leader fast-fail.
/// <para>★ Assembly here = the composition root in miniature (assembly-order contract for D5-T1).</para>
/// </summary>
public class RaftEngineTests
{
    /// <summary>v3 条目区构建（测试侧模拟 store 直写产物——RaftEntriesRegion 布局）。</summary>
    private static byte[] BuildEntriesRegion(params (long Term, byte Kind, byte[] Content)[] entries)
    {
        var writer = new TC.Tier.Core.Primitives.PooledBufferWriter();
        RaftEntriesRegion.WriteCount(writer, entries.Length);
        foreach (var (term, kind, content) in entries)
            RaftEntriesRegion.WriteEntry(writer, term, kind, content);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>记录型业务状态机（apply 单 worker 回调——按 index 记录命令）。</summary>
    internal class RecordingStateMachine : IStateMachine
    {
        public readonly ConcurrentDictionary<long, byte[]> Applied = new();
        public virtual ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Applied[index] = command.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>单节点装配（存储+传输+管道+状态机+成员协调——最小组合根）。</summary>
    internal sealed class TestNode : IAsyncDisposable
    {
        public required NodeId Id { get; init; }
        public required IRaftStore Store { get; init; }
        public required InProcessNode Transport { get; init; }
        public required RecordingStateMachine Machine { get; init; }
        public required ApplyPipeline Pipeline { get; init; }
        public required RaftStateMachine Raft { get; init; }
        public required Membership Membership { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Raft.DisposeAsync().ConfigureAwait(false);
            await Pipeline.DisposeAsync().ConfigureAwait(false);
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed record ClusterRig(InProcessTransportHub Hub, TestNode[] Nodes) : IAsyncDisposable
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

    internal static async Task<ClusterRig> CreateClusterAsync(int count, int seed = 42,
        Func<RecordingStateMachine>? machineFactory = null,
        Func<NodeId, IRaftStore>? storeFactory = null,
        Func<NodeId, RaftOptions>? optionsFactory = null,
        Func<int, ClusterMemberRole>? roleFactory = null,
        Func<ObservabilityHub>? hubFactory = null)
    {
        var hub = new InProcessTransportHub();
        var nodes = new TestNode[count];
        try
        {
            var members = new ClusterMember[count];
            for (var i = 0; i < count; i++)
                members[i] = new ClusterMember(NodeId.NewRandom(), "", roleFactory?.Invoke(i) ?? ClusterMemberRole.Voter);
            var config = new ClusterConfig(members);
            for (var i = 0; i < count; i++)
            {
                var id = members[i].Id;
                var transport = hub.Register(id);
                transport.Start();
                // ★ 二期-C1：主回归经 host + 组通道（统一组路由——载荷带 [GroupId] 前缀，出口准则）
                var groupHost = new RaftGroupHost(transport);
                await groupHost.StartAsync().ConfigureAwait(false);
                var groupTransport = groupHost.CreateGroup(RaftGroupId.Empty);
                // ★ 二期-F2：witness 成员自动配高水位存储（无日志体——装配一致性，DDR-F2）
                var store = storeFactory?.Invoke(id)
                    ?? (members[i].Role == ClusterMemberRole.Witness ? new WitnessHighWaterStore() : new InMemoryRaftStore());
                await store.InitializeAsync().ConfigureAwait(false);
                var machine = machineFactory?.Invoke() ?? new RecordingStateMachine();
                var pipeline = new ApplyPipeline(store, machine, ApplyPipelineOptions.Default);
                // ★ 负载容忍选举窗（判例 2026-09-02，flaky 销案远程合并）：默认 150-300ms 在混跑
                //   调度毛刺下心跳间歇超窗 → 换届乒乓——语义测试不绑定时窗量值
                var options = optionsFactory?.Invoke(id) ?? RaftOptions.Default
                    .WithElectionTimeout(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000))
                    .WithRandom(new Random(seed + i));
                var raft = new RaftStateMachine(id, store, groupTransport, pipeline, options, hub: hubFactory?.Invoke());
                pipeline.SetConfigCallback(raft.PostConfigChanged);
                await pipeline.StartAsync().ConfigureAwait(false);
                await raft.StartAsync(config).ConfigureAwait(false);
                nodes[i] = new TestNode
                {
                    Id = id,
                    Store = store,
                    Transport = transport,
                    Machine = machine,
                    Pipeline = pipeline,
                    Raft = raft,
                    Membership = new Membership(raft),
                };
            }
            fx_Current = new ClusterRig(hub, nodes);
            return fx_Current;
        }
        catch
        {
            foreach (var n in nodes.Where(x => x is not null)) await n.DisposeAsync();
            await hub.DisposeAsync();
            throw;
        }
    }

    internal static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }


    /// <summary>选举稳定等待（leader 收敛后额外确认 300ms 无换届——满载下选举刚收敛即
    /// 复制会撞换届窗口：NotLeaderException/复制挂起 flaky 判例 2026-09-02）。
    /// ★ leaderless 窗口容忍（判例 2026-09-02）：换届乒乓的"旧降新未立"窗口 0 leader——
    /// Single 抛 Sequence contains no matching element 实锤——空窗重置稳定期继续等。</summary>
    internal static async Task<TestNode> WaitStableLeaderAsync(ClusterRig fx, TimeSpan? timeout = null)
    {
        try
        {
            await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1, timeout);
        }
        catch (TimeoutException)
        {
            // ★ 收敛失败状态快照（取证判例 2026-09-02——混跑 10s 未收敛实锤；2026-09-03 扩
            //   循环活性诊断：LoopLag/TickLag/QueueDepth/DeadlineIn——tick 链断 vs 循环卡 handler 判别）
            var dump = string.Join(" | ", fx.Nodes.Select(n =>
            {
                var d = n.Raft.DiagnoseLoop();
                return $"{n.Id.ToString()[..6]}:{n.Raft.Role}/t{n.Raft.CurrentTerm}/c{n.Raft.CommitIndex}" +
                       $"/loopLag={d.LoopLagMs}/tickLag={d.TickLagMs}/q={d.QueueDepth}/tickFail={d.TickWriteFailures}/dl={d.DeadlineInMs}" +
                       $"/loopEx={n.Raft.LoopException?.Message ?? "∅"}";
            }));
            Fixtures.FreezeForensics.Hold();   // ★ 取证保持（TC_NET_FREEZE_HOLD 门控——常规跑直通）
            throw new TimeoutException($"集群未收敛单 leader：{dump}");
        }
        var leader = fx.Nodes.Single(n => n.Raft.IsLeader);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
            var current = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader);
            if (current is null)
            {
                deadline = DateTime.UtcNow.AddMilliseconds(300);   // leaderless 窗口——重新计稳定期
                continue;
            }
            if (current != leader) { leader = current; deadline = DateTime.UtcNow.AddMilliseconds(300); }
        }
        return leader;
    }

    /// <summary>重路由复制（默认档 NotLeader 重试——换届窗口容忍；测试产品客户端标准模式）。
    /// ★ 返回完成节点（read-your-writes 判定必须对着完成调用者：重路由后旧引用已非完成节点——
    /// 判例 2026-09-02 混跑实锤 flaky）。</summary>
    internal static async Task<(long Index, TestNode Completing)> ReplicateRetryAsync(TestNode leader, byte[] command, int maxAttempts = 40)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var index = await leader.Raft.ReplicateAsync(command).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                return (index, leader);
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is NotLeaderException or TimeoutException)
            {
                Thread.Sleep(50);
                leader = fx_Current!.Nodes.FirstOrDefault(n => n.Raft.IsLeader) ?? leader;   // 短暂无 leader——保留旧引用下轮重试
            }
        }
    }

    private static ClusterRig? fx_Current;   // 重试重路由用（测试安装时登记）

    private static TestNode Leader(TestNode[] nodes)
        => nodes.Single(n => n.Raft.IsLeader);

    private static TestNode[] Followers(TestNode[] nodes)
        => nodes.Where(n => !n.Raft.IsLeader).ToArray();

    /// <summary>选举（spec-01）：3 节点启动 → 恰一个 leader，全员 leader 认知一致，term > 0。</summary>
    [Fact]
    public async Task Election_SingleLeader_AllNodesAgree()
    {
        await using var fx = await CreateClusterAsync(3);

        var leader = await WaitStableLeaderAsync(fx);
        await WaitForAsync(() => Followers(fx.Nodes).All(f => f.Raft.LeaderId == leader.Id));

        leader.Raft.CurrentTerm.Should().BeGreaterThan(0);
        Followers(fx.Nodes).All(f => f.Raft.CurrentTerm >= leader.Raft.CurrentTerm - 1).Should().BeTrue("任期单调推进");
    }

    /// <summary>
    /// 复制（spec-05 契约②）：10 命令 → 全节点 applied 同序同内容；返回 index = committed 且 applied
    /// （read-your-writes——apply 管道已应用才返回）。
    /// </summary>
    [Fact]
    public async Task Replicate_Commands_AppliedIdenticallyOnAllNodes()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);

        for (var i = 0; i < 10; i++)
        {
            var command = new byte[] { (byte)i, (byte)(i * 2) };
            var (index, completing) = await ReplicateRetryAsync(leader, command);
            // read-your-writes：返回时完成节点已 applied（重路由时完成节点可能已换——对完成节点判定）
            completing.Machine.Applied.TryGetValue(index, out var applied).Should().BeTrue("返回 index 已 applied");
            applied.Should().Equal(command);
        }

        // ★ 重复容忍（at-least-once 客户端重试判例 2026-09-02）：NotLeader 重试的条目可能已
        //   在新 leader 日志中存活（未提交前恰被换届保留）→ 重试产生同载荷重复条目——
        //   精确计数断言会被换届窗口击穿。前缀序一致 + 计数 ≥ 期望 = 语义等价。
        await WaitForAsync(() => fx.Nodes.All(n => n.Machine.Applied.Count >= 10));
        var leaderApplied = leader.Machine.Applied.OrderBy(kv => kv.Key).ToArray();
        foreach (var n in fx.Nodes)
        {
            var nodeApplied = n.Machine.Applied.OrderBy(kv => kv.Key).ToArray();
            nodeApplied.Should().HaveCountGreaterOrEqualTo(10);
            // 逐条内容比较（byte[] 相等性=引用——序列比较须逐条 SequenceEqual；前缀 10 条）
            for (var i = 0; i < 10; i++)
            {
                nodeApplied[i].Key.Should().Be(leaderApplied[i].Key, "全节点 apply 同 index（spec-05 契约：日志序 = apply 序）");
                nodeApplied[i].Value.Should().Equal(leaderApplied[i].Value, $"index {nodeApplied[i].Key} 命令内容一致");
            }
        }
        fx.Nodes.All(n => n.Store.LastLogIndex >= 10).Should().BeTrue("全节点日志尾覆盖期望前缀");
    }

    // ═══ committed 完成档（多数派提交即返回——不等 apply）═══

    /// <summary>apply 门控状态机：首次 ApplyAsync 挂在共享门上（apply worker 单线程 FIFO——
    /// 门未放全集群 applied 水位冻结；committed 档完成路径不经 apply worker，档位差异可确定性验证）。</summary>
    private sealed class GatedStateMachine(TaskCompletionSource gate) : RecordingStateMachine
    {
        public override async ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            await gate.Task.ConfigureAwait(false);   // 门未放——apply 冻结（含首次）
            await base.ApplyAsync(index, command, ct).ConfigureAwait(false);
        }
    }

    /// <summary>committed 档契约①：多数派提交即完成——apply 管道被门冻结时仍返回；
    /// 默认档（applied）同期必须挂起（read-your-writes 语义对照）；放门后两档全部收敛。</summary>
    [Fact]
    public async Task ReplicateCommitted_CompletesBeforeApply_AppliedTierWaits()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fx = await CreateClusterAsync(3, machineFactory: () => new GatedStateMachine(gate));
        var leader = await WaitStableLeaderAsync(fx);

        try
        {
            // committed 档：apply 全集群冻结中仍完成（多数派已提交）。
            // ★ 换届窗口容忍（判例 2026-09-02）：未提交在途在换届时被 NotLeader 失败——
            //   客户端重试契约（同 ReplicateRetryAsync 模式）
            long committedIndex;
            TestNode completing = leader;
            for (var attempt = 0; ; attempt++)
            {
                var current = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader);
                if (current is null)
                {
                    Thread.Sleep(50);
                    continue;
                }
                try
                {
                    committedIndex = await current.Raft.ReplicateCommittedAsync(new byte[] { 0x01 })
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    completing = current;
                    break;
                }
                catch (NotLeaderException) when (attempt < 40)
                {
                    Thread.Sleep(50);
                }
            }
            completing.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(committedIndex, "committed 档完成 = commitIndex 已追上");

            // 默认档同期必须未完成（apply 冻结——read-your-writes 无法兑现；确定性：apply worker
            // 单线程 FIFO 被门挡住，applied 水位不可能推进——300ms 窗口仅为观察缓冲）。
            // ★ 换届容忍（判例 2026-09-02）：窗口内 leader 换届 → 未提交在途被 NotLeader 失败
            //   （任务 completed=异常）——客户端重试契约；成功完成 = read-your-writes 违反（真失败）
            Task<long> appliedTask;
            while (true)
            {
                var current = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader);
                if (current is null)
                {
                    Thread.Sleep(50);
                    continue;
                }
                appliedTask = current.Raft.ReplicateAsync(new byte[] { 0x02 }).AsTask();
                Thread.Sleep(300);
                if (appliedTask.IsCompletedSuccessfully)
                    appliedTask.IsCompleted.Should().BeFalse("apply 被门冻结——默认档（applied）不可成功完成");   // 恒失败——read-your-writes 违反
                if (appliedTask.IsFaulted && appliedTask.Exception?.InnerException is NotLeaderException)
                    continue;   // 换届窗口——重试（NotLeader 失败 = 客户端重试契约）
                break;   // 仍挂起——门冻结生效 ✓
            }
            appliedTask.IsCompleted.Should().BeFalse("apply 被门冻结——默认档（applied）不可完成");

            // 放门 → apply 推进 → 默认档完成 + 全集群收敛（换届重试可能留同载荷尾条——≥ 语义）
            gate.SetResult();
            var appliedIndex = await appliedTask.WaitAsync(TimeSpan.FromSeconds(5));
            appliedIndex.Should().BeGreaterOrEqualTo(committedIndex + 1);
            await WaitForAsync(() => fx.Nodes.All(n => n.Machine.Applied.Count >= 2));
        }
        finally
        {
            gate.TrySetResult();   // Dispose 防挂：apply worker 冻结释放在任何退出路径之前
        }
    }

    /// <summary>committed 档契约②：非 Leader 快速失败（与默认档同型）。</summary>
    [Fact]
    public async Task ReplicateCommitted_OnFollower_ThrowsNotLeader()
    {
        await using var fx = await CreateClusterAsync(3);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);

        // ★ 换届窗口容忍（判例 2026-09-02）：选定 follower 可能在调用瞬间已当选（选举完成于
        //   选取与调用之间）——调用成功非契约违反，重选重试；NotLeader = 契约满足
        for (var attempt = 0; ; attempt++)
        {
            var follower = fx.Nodes.FirstOrDefault(n => !n.Raft.IsLeader);
            if (follower is null)
            {
                Thread.Sleep(25);
                continue;
            }
            try
            {
                await follower.Raft.ReplicateCommittedAsync(new byte[] { 0x01 });
                if (attempt >= 20)
                    throw new Xunit.Sdk.XunitException("非 leader 节点 20 次重试均未快速失败（契约违反）。");
                Thread.Sleep(25);   // 该节点调用瞬间已是 leader——重选重试
            }
            catch (NotLeaderException)
            {
                break;   // ✓ 快速失败契约满足
            }
        }
    }

    /// <summary>committed 档契约③：换届后提交快速失败（NotLeaderException——重路由新 leader 重试）；
    /// 新 leader 上 committed 档继续可用。
    /// <para>★ 分区内旧 leader 依旧自认 Leader（收不到新 term——raft 正确行为），其提交只会
    /// 挂起等多数派而非快速失败——快速失败断言必须在分区解除、旧 leader 感知新 term 降级之后。</para></summary>
    [Fact]
    public async Task ReplicateCommitted_AfterLeaderChange_FailsFastThenRecovers()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fx = await CreateClusterAsync(3, machineFactory: () => new GatedStateMachine(gate));
        var leader = await WaitStableLeaderAsync(fx);
        var survivors = Followers(fx.Nodes).ToArray();

        try
        {
            // 隔离 leader → 幸存者重选举（选举不依赖 apply——gate 冻结无碍）
            fx.Hub.Faults.Partition(new[] { leader.Id }, survivors.Select(n => n.Id));
            await WaitForAsync(() => survivors.Count(n => n.Raft.IsLeader) == 1, TimeSpan.FromSeconds(20));
            survivors.Single(n => n.Raft.IsLeader).Raft.CurrentTerm.Should().BeGreaterThan(leader.Raft.CurrentTerm);

            // 解除分区 + 放门——旧 leader 收到新 term 心跳降级后，提交才快速失败
            fx.Hub.Faults.Reset();
            gate.SetResult();
            await WaitForAsync(() => !leader.Raft.IsLeader, TimeSpan.FromSeconds(20));
            var act = async () => await leader.Raft.ReplicateCommittedAsync(new byte[] { 0x03 });
            await act.Should().ThrowAsync<NotLeaderException>();

            // 新 leader 上 committed 档恢复可用（新任期提交链完整）。
            // ★ 解除分区后旧 leader 可能再竞选 → 短暂 leaderless 窗口——轮询等收敛 + 重试模式（客户端标准形态）
            TestNode newLeader = survivors[0];
            var nlDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < nlDeadline)
            {
                var ls = survivors.Where(n => n.Raft.IsLeader).ToArray();
                if (ls.Length == 1) { newLeader = ls[0]; break; }
                Thread.Sleep(50);
            }
            newLeader.Raft.IsLeader.Should().BeTrue("换届收敛后幸存者中恰一个 leader");
            var index = await ReplicateWithRetryAsync(newLeader, new byte[] { 0x04 });
            newLeader.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(index);
        }
        finally
        {
            gate.TrySetResult();   // Dispose 防挂：apply worker 冻结释放在任何退出路径之前
        }
    }

    /// <summary>重路由复制（spec-08 客户端标准模式：NotLeader → 重试——leaderless 窗口容忍）。</summary>
    private static async Task<long> ReplicateWithRetryAsync(TestNode leader, byte[] command, int maxAttempts = 50)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await leader.Raft.ReplicateCommittedAsync(command).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (NotLeaderException) when (attempt < maxAttempts)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>leader 失效（spec-01/05）：静默死亡 → 多数派重选举 → 复制恢复。</summary>
    [Fact]
    public async Task LeaderFailure_NewLeaderElected_ReplicationContinues()
    {
        await using var fx = await CreateClusterAsync(3);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var victim = Leader(fx.Nodes);
        var survivors = fx.Nodes.Where(n => n != victim).ToArray();

        await victim.Raft.StopAsync();   // 静默崩溃——停循环不再发心跳/应答（真实崩溃形态）

        // ★ 收敛等待 + 失败状态快照（判例 2026-09-02：混跑下 10s 未收敛——取证三件套）
        try
        {
            await WaitForAsync(() => survivors.Count(n => n.Raft.IsLeader) == 1);
        }
        catch (TimeoutException)
        {
            var dump = string.Join(" | ", fx.Nodes.Select(n =>
            {
                var d = n.Raft.DiagnoseLoop();
                var ex = n.Raft.LoopException;
                var frames = (ex?.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var exAt = string.Join(" <- ", frames.Take(3).Select(f => f.Trim()[..Math.Min(110, f.Trim().Length)]));
                return $"{n.Id.ToString()[..6]}:{n.Raft.Role}/t{n.Raft.CurrentTerm}/c{n.Raft.CommitIndex}" +
                       $"/loopLag={d.LoopLagMs}/tickLag={d.TickLagMs}/q={d.QueueDepth}/tickFail={d.TickWriteFailures}/dl={d.DeadlineInMs}" +
                       $"/loopEx={ex?.GetType().Name}:{ex?.Message ?? "∅"}/exAt=[{exAt}]";
            }));
            throw new TimeoutException($"幸存者 10s 未收敛单 leader：{dump}");
        }
        var newLeader = Leader(survivors);

        var command = new byte[] { 0xAA, 0xBB };
        var (index, _) = await ReplicateRetryAsync(newLeader, command);
        await WaitForAsync(() => survivors.All(n => n.Machine.Applied.ContainsKey(index)));
        survivors.Select(n => n.Machine.Applied[index]).All(b => b.SequenceEqual(command)).Should().BeTrue();
    }

    /// <summary>
    /// 分区（spec-09 场景族首轮）：leader 与多数派隔离 → 多数派重选举 → 愈合收敛单 leader。
    /// </summary>
    [Fact]
    public async Task Partition_LeaderLosesQuorum_HealConvergesSingleLeader()
    {
        await using var fx = await CreateClusterAsync(3);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var isolatedLeader = Leader(fx.Nodes);
        var majority = fx.Nodes.Where(n => n != isolatedLeader).ToArray();

        fx.Hub.Faults.Partition([isolatedLeader.Id], majority.Select(m => m.Id));

        // 多数派侧选出新 leader（旧 leader 心跳请求超时/选举超时——失 quorum）
        await WaitForAsync(() => majority.Count(n => n.Raft.IsLeader) == 1);
        var newLeader = Leader(majority);

        // 愈合：注入清零——旧 leader 降级，全集群收敛单 leader
        fx.Hub.Faults.Reset();
        try
        {
            await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1, TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            // ★ 愈合收敛失败状态快照（判例 2026-09-03——泵域改造后残余红实锤轮 17）：角色/任期/
            //   提交水位 + DiagnoseLoop 富字段（loopLag/tickLag/队列深度/tick 写失败/deadline 距离）
            var dump = string.Join(" | ", fx.Nodes.Select(n =>
            {
                var d = n.Raft.DiagnoseLoop();
                var ex = n.Raft.LoopException;
                return $"{n.Id.ToString()[..6]}:{n.Raft.Role}/t{n.Raft.CurrentTerm}/c{n.Raft.CommitIndex}" +
                       $"/p={n.Store.PersistedIndex}/l={n.Store.LastLogIndex}" +
                       $"/loopLag={d.LoopLagMs}/tickLag={d.TickLagMs}/q={d.QueueDepth}/tickFail={d.TickWriteFailures}/dl={d.DeadlineInMs}" +
                       $"/loopEx={ex?.GetType().Name}:{ex?.Message ?? "∅"}";
            }));
            Fixtures.FreezeForensics.Hold();
            throw new TimeoutException($"愈合后 15s 未收敛单 leader：{dump}\nregistry={fx.Nodes[0].Raft.DescribeRegistry()}");
        }
        var finalLeader = Leader(fx.Nodes);
        try
        {
            await WaitForAsync(() => Followers(fx.Nodes).All(f => f.Raft.LeaderId == finalLeader.Id),
                TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            // ★ follower leader 认知停滞快照（判例 2026-09-03 轮 29 实锤：单 leader 已收敛但
            //   follower LeaderId 15s 不更新——心跳 lane 对单 follower 停摆疑云）：全节点
            //   LeaderId 认知 + DiagnoseLoop 富字段
            var dump = string.Join(" | ", fx.Nodes.Select(n =>
            {
                var d = n.Raft.DiagnoseLoop();
                var lid = n.Raft.LeaderId is { } l ? l.ToString()[..6] : "∅";
                var ex = n.Raft.LoopException;
                return $"{n.Id.ToString()[..6]}:{n.Raft.Role}/t{n.Raft.CurrentTerm}/c{n.Raft.CommitIndex}" +
                       $"/leader={lid}/loopLag={d.LoopLagMs}/tickLag={d.TickLagMs}/q={d.QueueDepth}/dl={d.DeadlineInMs}" +
                       $"/loopEx={ex?.GetType().Name}:{ex?.Message ?? "∅"}";
            }));
            Fixtures.FreezeForensics.Hold();
            throw new TimeoutException($"愈合后 follower leader 认知 15s 未收敛（final={finalLeader.Id.ToString()[..6]}）：{dump}\nregistry={fx.Nodes[0].Raft.DescribeRegistry()}");
        }
    }

    /// <summary>PreVote 防扰主（论文 §9.6 本义——2026-09-03 收敛停滞实锤修复）：follower 心跳
    /// 间歇超窗进 PreCandidate 时，在位 leader + 有主 follower 拒预票（活 leader 否决）——
    /// 不发生扰主真选举，leader 身份与任期零变动；愈合后集群照常。</summary>
    [Fact]
    public async Task PreVote_LiveLeader_VetoesDisruption()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var leaderId = leader.Id;
        var termBefore = leader.Raft.CurrentTerm;
        var others = fx.Nodes.Where(n => n != leader).ToArray();

        // 分区 leader ↔ follower[0]：f0 心跳间歇超窗 → PreCandidate——旧实现此处必发生扰主
        // 真选举（f1 授预票 + leader 授预票 → 多数派 → StepDown 链掀翻健康 leader）
        try
        {
            fx.Hub.Faults.Partition([leaderId], [others[0].Id]);
            Thread.Sleep(1500);   // 覆盖 1.5~3 个选举窗
            others[1].Raft.IsLeader.Should().BeFalse("有主集群——另一 follower 不得趁机当选");
        }
        finally
        {
            fx.Hub.Faults.Reset();
        }

        // 愈合后：leader 身份与任期零变动（防扰主 = 任期零空转）+ f0 回归 follower
        await WaitStableLeaderAsync(fx);
        Leader(fx.Nodes).Id.Should().Be(leaderId, "在位 leader 否决预票——超窗不得掀翻健康 leader");
        leader.Raft.CurrentTerm.Should().Be(termBefore, "预票被否决 = 真选举未发生 = 任期不变");
        await WaitForAsync(() => others[0].Raft.Role == RaftRole.Follower, TimeSpan.FromSeconds(15));
    }

    /// <summary>线性读（spec-08 §4）：leader ReadIndex 返回当前 commitIndex。</summary>
    [Fact]
    public async Task ReadIndex_ReturnsCurrentCommit()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);

        await ReplicateRetryAsync(leader, new byte[] { 0x01 });

        // ★ NotLeader 重路由重试（判例 2026-09-02）：混跑下换届窗口——稳定期后 ReadIndex 直调
        //   撞 NotLeaderException（快速失败契约）——客户端标准模式 = 重查 leader 重试
        long readIndex = 0;
        for (var attempt = 0; ; attempt++)
        {
            var current = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader) ?? leader;
            try
            {
                readIndex = await current.Raft.ReadIndexAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                break;
            }
            catch (NotLeaderException) when (attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
        readIndex.Should().BeGreaterThanOrEqualTo(1);
        // 上限取全集群水位（重路由后应答者可能已非原 leader——原引用提交水位可能滞后）
        readIndex.Should().BeLessThanOrEqualTo(fx.Nodes.Max(n => n.Raft.CommitIndex) + 1);
    }

    /// <summary>成员变更（spec-04 single-server）：加成员 → 全节点配置含新成员（apply 产物）；移除 → 收敛。</summary>
    [Fact]
    public async Task Membership_AddRemove_ConfigPropagates()
    {
        await using var fx = await CreateClusterAsync(3);
        await WaitStableLeaderAsync(fx);

        // ★ 换届窗口容忍（判例 2026-09-02）：成员操作与复制同契约——NotLeader 重路由重试
        var newcomer = NodeId.NewRandom();
        for (var attempt = 0; ; attempt++)
        {
            var current = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader);
            if (current is null) { Thread.Sleep(50); continue; }
            try
            {
                await current.Membership.AddMemberAsync(newcomer);
                break;
            }
            catch (NotLeaderException) when (attempt < 40) { Thread.Sleep(50); }
        }
        await WaitForAsync(() => fx.Nodes.All(n => n.Raft.Config.Contains(newcomer)));
        fx.Nodes.All(n => n.Raft.Config.Count == 4).Should().BeTrue();

        for (var attempt = 0; ; attempt++)
        {
            var current = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader);
            if (current is null) { Thread.Sleep(50); continue; }
            try
            {
                await current.Membership.RemoveMemberAsync(newcomer);
                break;
            }
            catch (NotLeaderException) when (attempt < 40) { Thread.Sleep(50); }
        }
        await WaitForAsync(() => fx.Nodes.All(n => !n.Raft.Config.Contains(newcomer)));
        fx.Nodes.All(n => n.Raft.Config.Count == 3).Should().BeTrue();
    }

    /// <summary>非 leader 写入（spec-08 §1）：立即抛 NotLeaderException（不等待超时）。</summary>
    [Fact]
    public async Task Replicate_NonLeader_FastFailsWithNotLeader()
    {
        await using var fx = await CreateClusterAsync(3);
        await WaitForAsync(() => fx.Nodes.Count(n => n.Raft.IsLeader) == 1);
        var follower = Followers(fx.Nodes).First();

        var act = async () => await follower.Raft.ReplicateAsync(new byte[] { 0x01 });
        await act.Should().ThrowAsync<NotLeaderException>();
    }

    /// <summary>
    /// Committed-region protection (out-of-order delivery defense — raft invariant: committed
    /// entries are never truncated): an entry-carrying AppendEntries whose prevLogIndex is below
    /// the follower's commitIndex is rejected with a hint pointing at commit+1 — the follower's
    /// log stays intact and its commit unchanged.
    /// </summary>
    [Fact]
    public async Task AppendEntries_StaleBatchBelowCommit_RejectedLogIntact()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var follower = Followers(fx.Nodes).First();

        for (var i = 0; i < 10; i++)
            await ReplicateRetryAsync(leader, new byte[] { (byte)i });
        // ★ 重复容忍（同 Replicate_Commands 判例）——≥ 期望前缀，精确计数被换届重试窗口击穿
        await WaitForAsync(() => follower.Machine.Applied.Count >= 10);
        var commitBefore = follower.Raft.CommitIndex;
        commitBefore.Should().BeGreaterOrEqualTo(10);
        var tailBefore = follower.Store.LastLogIndex;

        // 攻击者节点直发乱序旧批（prevLogIndex=3 < commit=10——重写区间含已提交条目）
        var attacker = fx.Hub.Register(NodeId.NewRandom());
        attacker.Start();
        // ★ 二期-C1：攻击者同样经 host + 组通道发声（统一组路由——裸报文会被组防线直接丢弃）
        var attackerHost = new RaftGroupHost(attacker);
        await attackerHost.StartAsync();
        var attackerChannel = attackerHost.CreateGroup(RaftGroupId.Empty);
        try
        {
            var stale = new AppendEntriesReq
            {
                Term = leader.Raft.CurrentTerm,
                LeaderId = attacker.Self,
                PrevLogIndex = 3,
                PrevLogTerm = 1,
                EntriesRegion = BuildEntriesRegion((1, RaftEntryKind.Command, new byte[] { 0xEE })),
                LeaderCommit = 10,
            };
            var respBytes = await attackerChannel.SendRequestAsync(follower.Id, ProtocolIds.Raft, RaftRpcCodec.Encode(stale));
            RaftRpcCodec.TryDecode(respBytes, out var decoded).Should().BeTrue();
            var resp = decoded.Should().BeOfType<AppendEntriesResp>().Which;
            resp.Success.Should().BeFalse("已提交区保护——乱序旧批拒绝");
            resp.ConflictIndex.Should().Be(follower.Raft.CommitIndex + 1, "hint 指向 commit+1（leader 单调回退收敛）");
        }
        finally
        {
            await attacker.DisposeAsync();
        }

        follower.Store.LastLogIndex.Should().Be(tailBefore, "已提交区未被截断");
        follower.Raft.CommitIndex.Should().Be(commitBefore);
    }

    // ═══ LeaderLocal 应答档（spec-12 §6 增量——复制家族设计件 A）═══

    /// <summary>fsync 门控存储装饰器：<see cref="IRaftStore.WaitForPersistedAsync"/> 挂共享门——
    /// 本地持久化水位不推进，LeaderLocal 等待者确定性停留在 _pendingLeaderLocal（换届取消路径验证）；
    /// 其余成员全委托（WriteTermAndVoteAsync 等选举落盘不受门影响）。</summary>
    private sealed class GatedPersistStore(IRaftStore inner, TaskCompletionSource gate) : IRaftStore
    {
        public ValueTask<bool> AssertHighWatermarkAsync(long index, long term, CancellationToken ct = default)
            => inner.AssertHighWatermarkAsync(index, term, ct);
        public long Term => inner.Term;
        public NodeId VotedFor => inner.VotedFor;
        public long AppliedIndex => inner.AppliedIndex;
        public long LastLogIndex => inner.LastLogIndex;
        public long LastLogTerm => inner.LastLogTerm;
        public long AllocatedIndex => inner.AllocatedIndex;
        public long PersistedIndex => inner.PersistedIndex;
        public long SnapshotIndex => inner.SnapshotIndex;

        public ValueTask InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public ValueTask WriteTermAndVoteAsync(long term, NodeId votedFor, CancellationToken ct = default) => inner.WriteTermAndVoteAsync(term, votedFor, ct);
        public ValueTask UpdateAppliedIndexAsync(long index, CancellationToken ct = default) => inner.UpdateAppliedIndexAsync(index, ct);
        public ValueTask<long> AppendAsync(long prevIndex, IReadOnlyList<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default) => inner.AppendAsync(prevIndex, entries, ct);
        public bool TryGetEntry(long index, out long term, out byte kind, out ReadOnlyMemory<byte> content) => inner.TryGetEntry(index, out term, out kind, out content);
        public IEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> Scan(long fromIndex, int maxCount) => inner.Scan(fromIndex, maxCount);
        public IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadEntriesAsync(long fromIndex, CancellationToken ct = default) => inner.ReadEntriesAsync(fromIndex, ct);
        public int CountEntries(long fromIndex, int maxCount) => inner.CountEntries(fromIndex, maxCount);
        public ValueTask<int> WriteEntriesToAsync(long fromIndex, int count, System.Buffers.IBufferWriter<byte> destination, long[] termsOut, CancellationToken ct = default) => inner.WriteEntriesToAsync(fromIndex, count, destination, termsOut, ct);
        public ValueTask<bool> PrevLogMatchesAsync(long prevLogIndex, long prevLogTerm, CancellationToken ct = default) => inner.PrevLogMatchesAsync(prevLogIndex, prevLogTerm, ct);
        public ValueTask<long> ReadLogTermAsync(long index, CancellationToken ct = default) => inner.ReadLogTermAsync(index, ct);
        public ValueTask TruncateSuffixFromAsync(long indexInclusive, CancellationToken ct = default) => inner.TruncateSuffixFromAsync(indexInclusive, ct);
        public ValueTask TruncatePrefixToAsync(long indexInclusive, CancellationToken ct = default) => inner.TruncatePrefixToAsync(indexInclusive, ct);
        public IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadSnapshotEntriesAsync(long snapshotIndex, CancellationToken ct = default) => inner.ReadSnapshotEntriesAsync(snapshotIndex, ct);
        public ValueTask ImportSnapshotAsync(long snapshotIndex, IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default) => inner.ImportSnapshotAsync(snapshotIndex, entries, ct);
        public ValueTask RefreshTailAfterImportAsync(CancellationToken ct = default) => inner.RefreshTailAfterImportAsync(ct);

        public async ValueTask WaitForPersistedAsync(long index, CancellationToken ct = default)
        {
            await gate.Task.ConfigureAwait(false);   // 门未放——持久化水位不推进（等待者不可完成）
            await inner.WaitForPersistedAsync(index, ct).ConfigureAwait(false);
        }
    }

    /// <summary>LeaderLocal 档契约①：N=1 Standalone——本地即多数派，与 committed 档等价：
    /// 返回 index 顺序推进、最终 applied 收敛（回归确认——弱档在单节点不丢语义）。</summary>
    [Fact]
    public async Task Standalone_LeaderLocal_EquivalentToCommitted()
    {
        await using var fx = await CreateClusterAsync(1);
        var leader = fx.Nodes[0];
        await WaitForAsync(() => leader.Raft.IsLeader);

        var i1 = await leader.Raft.ReplicateLeaderLocalAsync(new byte[] { 0x01 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var i2 = await leader.Raft.ReplicateCommittedAsync(new byte[] { 0x02 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var i3 = await leader.Raft.ReplicateLeaderLocalAsync(new byte[] { 0x03 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        i2.Should().Be(i1 + 1, "Standalone 无复制窗口——index 连续推进");
        i3.Should().Be(i2 + 1);
        await WaitForAsync(() => leader.Machine.Applied.Count == 3);
        leader.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(i3, "本地即多数派——commitIndex 追平");
    }

    /// <summary>LeaderLocal 档契约②：per-call 两档完成点结构对照——leader 分区（多数派不可达，
    /// committed 档条件性不可能完成）下 LeaderLocal 照常完成：完成点严格早于多数派提交。
    /// <para>★ 不用计时统计——InProcess 内存介质上两档完成差 ns 级（真实差异在磁盘 fsync +
    /// 真网络），分辨率不足；结构性断言零脆弱。</para></summary>
    [Fact]
    public async Task LeaderLocal_CompletesWhereCommittedCannot_PartitionedLeader()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var others = Followers(fx.Nodes).ToArray();

        Task? committedTask = null;
        try
        {
            // 分区：多数派不可达——committed 档逻辑上不可能完成
            fx.Hub.Faults.Partition([leader.Id], others.Select(n => n.Id));

            // LeaderLocal：本地持久化即返（不等复制——照常完成）
            var index = await leader.Raft.ReplicateLeaderLocalAsync(new byte[] { 0x01 })
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            index.Should().BeGreaterThan(0);
            leader.Raft.CommitIndex.Should().BeLessThan(index, "多数派未达成——完成点在本档（本地持久化）");

            // committed 档同期必须挂起（等多数派——500ms 观察窗）
            committedTask = leader.Raft.ReplicateCommittedAsync(new byte[] { 0x02 }).AsTask();
            Thread.Sleep(500);
            committedTask.IsCompleted.Should().BeFalse("committed 档等多数派——分区中不可完成");
        }
        finally
        {
            fx.Hub.Faults.Reset();
            if (committedTask is not null)
            {
                try { await committedTask.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* 收尾观测——分区解除后完成或换届取消，防未观测异常 */ }
            }
        }
    }

    /// <summary>LeaderLocal 档契约③（丢写窗口）：leader 与全部 follower 分区——LeaderLocal 写入
    /// 照常完成（不等复制）→ follower 侧选出更高 term 新 leader → 未复制到任何成员的尾部写
    /// 全部丢失（设计稿 §2.1 失败模式——异步复制物理边界），集群继续可用。</summary>
    [Fact]
    public async Task LeaderLocal_UnreplicatedTail_LostOnFailover()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var others = Followers(fx.Nodes).ToArray();

        // 全分区：leader 与两个 follower 断（follower 互相连通——多数派仍在）
        fx.Hub.Faults.Partition([leader.Id], others.Select(n => n.Id));

        // 分区中写 5 条——全部完成（本地持久化即返；复制发不出去不阻塞应答）
        var indexes = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var index = await leader.Raft.ReplicateLeaderLocalAsync(new byte[] { (byte)i })
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            indexes.Add(index);
        }
        indexes.Should().BeInAscendingOrder().And.OnlyContain(idx => idx > 0);

        // follower 侧重选举（感知不到旧 leader——term 必进）
        await WaitForAsync(() => others.Count(n => n.Raft.IsLeader) == 1, TimeSpan.FromSeconds(20));
        var newLeader = others.Single(n => n.Raft.IsLeader);
        newLeader.Raft.CurrentTerm.Should().BeGreaterThan(leader.Raft.CurrentTerm);

        // 丢写窗口断言：5 条未到达任何成员——新 leader 日志不含（换届时回退）。
        // ★ 二期-B 锚点感知：新 leader 自任期锚点 no-op 至多占位首丢写位（同 index）——
        //   界断言 ≤ 首丢写位 + 丢失写不得在任何节点应用（锚点不达业务状态机）。
        newLeader.Store.LastLogIndex.Should().BeLessThanOrEqualTo(indexes[0], "未复制尾部写随换届丢失（锚点 no-op 至多占位）");
        newLeader.Machine.Applied.Keys.Should().NotContain(indexes, "丢失写在任何节点不得应用");

        // 集群继续可用：新 leader 写入正常提交（多数派 = 新 leader + 另一 follower）
        var (idx, _) = await ReplicateRetryAsync(newLeader, new byte[] { 0xAA });
        newLeader.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(idx);
    }

    /// <summary>LeaderLocal 档契约④：换届在途等待者取消——fsync 门把等待者确定性停在
    /// _pendingLeaderLocal；分区断 leader 心跳 → 多数派选举超时自发换届 → 愈合后新 term 心跳
    /// 使旧 leader StepDown → NotLeaderException（客户端重路由标准模式）。
    /// <para>★ W4 persist 下环后触发面变化：fsync 挂起不再冻结事件循环（心跳照发——"冻结自发换届"
    /// 机制已不存在，这正是下环的目的），确定性换届改由「分区断心跳 → 多数派选举 → 愈合传导
    /// 新 term」驱动；取消契约本身不变。</para></summary>
    [Fact]
    public async Task LeaderLocal_InFlightWaiter_CancelledWithNotLeaderOnStepDown()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fx = await CreateClusterAsync(3,
            storeFactory: _ => new GatedPersistStore(new InMemoryRaftStore(), gate));
        var leader = await WaitStableLeaderAsync(fx);
        var others = Followers(fx.Nodes).ToArray();

        try
        {
            // 等待者入表后 flush 挂门——W4 下环：循环不停摆、心跳照发（等待者确定性停在表中）
            var writeTask = leader.Raft.ReplicateLeaderLocalAsync(new byte[] { 0x01 }).AsTask();
            Thread.Sleep(300);   // 门挂住；writeTask 状态不作断言点

            // 分区断心跳 → follower 选举超时 → 新 leader（多数派侧确定性换届）
            fx.Hub.Faults.Partition([leader.Id], others.Select(n => n.Id));
            await WaitForAsync(() => others.Count(n => n.Raft.IsLeader) == 1, TimeSpan.FromSeconds(20));
            var newLeader = others.Single(n => n.Raft.IsLeader);
            newLeader.Raft.CurrentTerm.Should().BeGreaterThanOrEqualTo(leader.Raft.CurrentTerm,
                "换届后新 leader 任期不低于旧 leader 已知任期");

            // 愈合：新 term 心跳传导 → 旧 leader StepDown → 清理在途等待者（NotLeaderException）
            fx.Hub.Faults.Reset();
            var act = () => writeTask.WaitAsync(TimeSpan.FromSeconds(20));
            await act.Should().ThrowAsync<NotLeaderException>("换届取消在途 LeaderLocal 等待者");
        }
        finally
        {
            gate.TrySetResult();   // Dispose 防挂：fsync 门冻结释放在任何退出路径之前
        }
    }

    /// <summary>LeaderLocal 档契约⑤（配置面）：<see cref="RaftOptions.WithReplicationAck"/> 切
    /// LeaderLocal 后默认入口 <see cref="RaftStateMachine.ReplicateAsync"/> 完成点前移到本地持久化
    /// ——复制分区（commit 不可能推进）中照常完成，且完成时 commitIndex 落后于返回 index（结构性
    /// 证据）；显式 committed 档不受集群档影响（同期照旧等多数派）。</summary>
    [Fact]
    public async Task ClusterAckOption_LeaderLocal_MovesDefaultEntryPointToLocalPersist()
    {
        await using var fx = await CreateClusterAsync(3,
            optionsFactory: id => RaftOptions.Default
                .WithReplicationAck(ReplicationAck.LeaderLocal)
                .WithElectionTimeout(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000))
                // ★ 每节点独立种子（2026-09-03 判例）：同种子 → 选举窗全同 → 共享节拍唤醒下
                //   三候选同任期锁步分票永不收敛（Candidate/tN 三态僵持实锤）
                .WithRandom(new Random(42 + id.GetHashCode())));
        var leader = await WaitStableLeaderAsync(fx);
        var others = Followers(fx.Nodes).ToArray();

        Task? committedTask = null;
        try
        {
            // 分区：leader 复制发不出去——commit 不可能推进
            fx.Hub.Faults.Partition([leader.Id], others.Select(n => n.Id));

            // 默认入口（集群 LeaderLocal 档）：本地持久化即返
            var index = await leader.Raft.ReplicateAsync(new byte[] { 0x01 })
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            leader.Raft.CommitIndex.Should().BeLessThan(index,
                "完成时多数派未达成——完成点 = 本地持久化（弱档语义的结构性证据）");

            // 显式 committed 档不受集群档影响：同期照旧等多数派（500ms 观察窗）
            committedTask = leader.Raft.ReplicateCommittedAsync(new byte[] { 0x02 }).AsTask();
            Thread.Sleep(500);
            committedTask.IsCompleted.Should().BeFalse("committed 档契约不变——仍等多数派");
        }
        finally
        {
            fx.Hub.Faults.Reset();
            if (committedTask is not null)
            {
                try { await committedTask.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* 收尾观测——分区解除后完成或换届取消，防未观测异常 */ }
            }
        }
    }

    // ═══ raft learner（spec-12 §6 增量——复制家族设计件 B：不投票副本）═══

    /// <summary>learner 契约①：3 voter + 2 learner——leader 必为 voter；写入后 learner 日志/apply
    /// 全程追平（复制链与 LeaderCommit 跟随不区分角色）；停 1 voter 后写入继续提交（多数派 =
    /// 2 voter——learner 不占多数派），learner 继续追平（更高 term 降级跟随语义共存）。</summary>
    [Fact]
    public async Task Learner_ReplicatesFollows_WithoutAffectingQuorum()
    {
        await using var fx = await CreateClusterAsync(5,
            roleFactory: i => i >= 3 ? ClusterMemberRole.Learner : ClusterMemberRole.Voter);
        var voters = fx.Nodes.Take(3).ToArray();
        var learners = fx.Nodes.Skip(3).ToArray();

        var leader = await WaitStableLeaderAsync(fx);
        voters.Should().Contain(leader, "选举只在 voter 间进行——leader 必为 voter");

        // 写 10 条——全员（含 learner）追平
        for (var i = 0; i < 10; i++)
            await ReplicateRetryAsync(leader, new byte[] { (byte)i });
        await WaitForAsync(() => fx.Nodes.All(n => n.Machine.Applied.Count == 10), TimeSpan.FromSeconds(15));
        // ★ 二期-B 锚点感知：leader 任期锚点 no-op 计入日志尾——追平断言对齐 leader 尾（不绑绝对值）
        learners.All(l => l.Store.LastLogIndex == leader.Store.LastLogIndex).Should().BeTrue("learner 日志追平——复制链不区分角色");
        learners.All(l => l.Raft.LeaderId == leader.Id).Should().BeTrue("learner LeaderCommit 跟随——可读");

        // 停 1 voter（非 leader）——多数派 = 2 voter：提交继续；learner 继续追平
        var victim = voters.First(n => n != leader);
        await victim.Raft.StopAsync();
        var (idx, _) = await ReplicateRetryAsync(leader, new byte[] { 0xBB });
        leader.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(idx, "多数派只数 voter——learner 不占位");
        await WaitForAsync(() => learners.All(l => l.Machine.Applied.ContainsKey(idx)), TimeSpan.FromSeconds(15));
    }

    /// <summary>learner 契约②：全体 voter 停机 → learner 不发起选举（term 不进）、不当选——
    /// 多数派语义保持（无 leader，等待 voter 恢复）。</summary>
    [Fact]
    public async Task Learner_DoesNotRunForElection_WhenAllVotersDown()
    {
        await using var fx = await CreateClusterAsync(5,
            roleFactory: i => i >= 3 ? ClusterMemberRole.Learner : ClusterMemberRole.Voter);
        var leader = await WaitStableLeaderAsync(fx);
        var voters = fx.Nodes.Take(3).ToArray();
        var learners = fx.Nodes.Skip(3).ToArray();

        var learnerTerm = learners[0].Raft.CurrentTerm;

        // 全体 voter 静默停机（真实崩溃形态——选举超时多轮）
        foreach (var v in voters) await v.Raft.StopAsync();
        Thread.Sleep(2000);   // 选举超时 [150,300]ms × 多轮—— learner 选举门断言窗口

        // ★ 断言对象 = learner（停机 voter 的 IsLeader 是僵尸标志——循环已停，集群事实以活着的 learner 为准）
        learners.Where(l => l.Raft.IsLeader).Should().BeEmpty("learner 不发起选举——voter 全停机时 learner 不当选");
        learners.All(l => l.Raft.CurrentTerm == learnerTerm).Should().BeTrue(
            "learner 选举超时被角色门拦截——term 不进（预票也被 learner 拒绝，真实选举不可达）");
    }

    /// <summary>批窗时间维到期（判例 2026-09-04 回归——窗到期曾骑 50ms 心跳 tick：BatchWindow
    /// 1-5ms 配置全被心跳粒度绑架 → 死期/吞吐崩塌/分配 ×10）。窗 150ms ≪ 心跳 600ms：
    /// 写入应 ~150ms 内完成复制（旧实现 = 等下一次心跳 tick ≥600ms）。</summary>
    [Fact]
    public async Task Replication_BatchWindow_FlushesOnWindowNotHeartbeat()
    {
        await using var fx = await CreateClusterAsync(3,
            optionsFactory: _ => RaftOptions.Default
                .WithElectionTimeout(TimeSpan.FromMilliseconds(3000), TimeSpan.FromMilliseconds(4000))
                .WithHeartbeatInterval(TimeSpan.FromMilliseconds(600))
                .WithReplication(new ReplicationPolicy { BatchWindow = TimeSpan.FromMilliseconds(150) }));
        var leader = await WaitStableLeaderAsync(fx);

        var sw = Stopwatch.StartNew();
        var (idx, _) = await ReplicateRetryAsync(leader, new byte[] { 0x77 });
        var elapsed = sw.ElapsedMilliseconds;

        elapsed.Should().BeLessThan(450,
            $"窗到期触发发送（~150ms + RTT 裕度）——旧形态等 600ms 心跳 tick（实测 {elapsed}ms）");
        await WaitForAsync(() => fx.Nodes.All(n => n.Machine.Applied.ContainsKey(idx)), TimeSpan.FromSeconds(10));
    }

    // ══ 二期-B follower 线性读（§6——验证矩阵 B 行）══

    /// <summary>B 行·leader 并发 ReadIndex（N 并发）：批量轮次不丢不挂、结果单调
    /// （单槽覆盖缺陷根治——全部 waiter 同轮完成且 ≥ 各自入队时 commit 水位）。</summary>
    [Fact]
    public async Task ReadIndex_ConcurrentBatch_AllCompleteMonotonic()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        await ReplicateRetryAsync(leader, new byte[] { 0x01 });
        await WaitForAsync(() => Followers(fx.Nodes).All(f => f.Raft.CommitIndex >= 1));

        const int concurrency = 16;
        var tasks = new Task<long>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            // 换届窗口容忍（判例 2026-09-02）：NotLeader 重查 leader 重试
            tasks[i] = Task.Run(async () =>
            {
                for (var attempt = 0; ; attempt++)
                {
                    var current = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader) ?? leader;
                    try { return await current.Raft.ReadIndexAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (NotLeaderException) when (attempt < 40) { await Task.Delay(50); }
                }
            });
        }
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        results.Should().OnlyContain(r => r >= 1, "全部完成（不丢不挂——单槽时代后发覆盖先发挂死）");
        results.Should().OnlyContain(r => r <= fx.Nodes.Max(n => n.Raft.CommitIndex) + 1,
            "单调水位：任何读都不超前于集群提交水位");
    }

    /// <summary>B 行·降级时挂起 ReadIndex：全部以 NotLeader 失败（批量 waiter 降级清理——
    /// 单槽时代旧 waiter 挂死至取消的缺陷回归）。构造：停两名 follower 制造多数派不可达
    /// → 读挂起在途 → ResignAsync 走 StepDown 统一路径 → 挂起读立即 NotLeader。</summary>
    [Fact]
    public async Task ReadIndex_PendingOnStepDown_FailWithNotLeader()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        await ReplicateRetryAsync(leader, new byte[] { 0x01 });
        await WaitForAsync(() => Followers(fx.Nodes).All(f => f.Raft.CommitIndex >= leader.Raft.CommitIndex - 1));

        var followers = Followers(fx.Nodes);
        foreach (var f in followers) await f.Transport.DisposeAsync();   // 多数派不可达——确认轮无法完成

        var pending = leader.Raft.ReadIndexAsync().AsTask();
        await Task.Delay(150);   // 轮次在途（waiter 已入队、心跳已推、确认未达多数派）
        pending.IsCompleted.Should().BeFalse("多数派不可达——读挂起（不返回过期 index）");

        await leader.Raft.ResignAsync();   // 降级——StepDown 统一路径
        var act = async () => await pending.WaitAsync(TimeSpan.FromSeconds(5));
        (await act.Should().ThrowAsync<NotLeaderException>().WaitAsync(TimeSpan.FromSeconds(5)))
            .Which.Message.Should().NotBeNull("挂起读立即以 NotLeader 失败——不挂死");
    }

    /// <summary>B 行·写后从 follower 读：ReadIndexForwardedAsync 转发 leader → 等待 applied →
    /// 读到最新提交（read-your-writes 经 follower）。</summary>
    [Fact]
    public async Task ReadIndexForwarded_FromFollower_ReadsLatestCommit()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var (index, _) = await ReplicateRetryAsync(leader, new byte[] { 0x42 });
        await WaitForAsync(() => Followers(fx.Nodes).All(f => f.Raft.CommitIndex >= index));

        var follower = Followers(fx.Nodes).First();
        var readIndex = await follower.Raft.ReadIndexForwardedAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        readIndex.Should().BeGreaterThanOrEqualTo(index, "转发读水位 ≥ 已提交写入");
        follower.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(index);
    }

    /// <summary>B 行·leader 切换中转发读：有界重试后成功。Resign 让位（全员存活——确定性）：
    /// follower 持过期 LeaderId 转发 → 旧 leader 应答 -1 → 有界重试至新 leader 心跳收敛
    /// LeaderId → 转发完成（-1 重试路径实测）。</summary>
    [Fact]
    public async Task ReadIndexForwarded_LeaderSwitch_BoundedRetrySucceeds()
    {
        await using var fx = await CreateClusterAsync(3);
        var firstLeader = await WaitStableLeaderAsync(fx);
        await ReplicateRetryAsync(firstLeader, new byte[] { 0x01 });
        var follower = Followers(fx.Nodes).First();
        await WaitForAsync(() => follower.Raft.LeaderId == firstLeader.Id);

        await firstLeader.Raft.ResignAsync();   // 让位（任期不变自降级）——follower 的 LeaderId 即刻过期

        var readIndex = await follower.Raft.ReadIndexForwardedAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var dump = string.Join(" | ", fx.Nodes.Select(n =>
            $"{n.Id.ToString()[..6]}:{n.Raft.Role}/t{n.Raft.CurrentTerm}/c{n.Raft.CommitIndex}/ldr={n.Raft.LeaderId?.ToString()[..6]}"));
        readIndex.Should().BeGreaterThanOrEqualTo(1, $"换届窗口内转发读有界重试收敛（nodes: {dump}）");
    }

    /// <summary>B 行·新 leader 任期锚点（no-op 回归——线性读破约缺陷）：换届后新 leader 的
    /// ReadIndex 必须 ≥ 前任期已提交水位（no-op 未提交前轮次挂起，不得以 0/旧水位应答）。</summary>
    [Fact]
    public async Task ReadIndex_NewLeader_AnchorsToPreviousCommit()
    {
        await using var fx = await CreateClusterAsync(3);
        var firstLeader = await WaitStableLeaderAsync(fx);
        var (committed, _) = await ReplicateRetryAsync(firstLeader, new byte[] { 0x7A });
        await WaitForAsync(() => Followers(fx.Nodes).All(f => f.Raft.CommitIndex >= committed));

        // 全员 Resign 轮转——每个新 leader 上任即面对"前任期已提交水位"，锚点缺失则返回 0
        for (var round = 0; round < 2; round++)
        {
            var current = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader) ?? firstLeader;
            var follower = fx.Nodes.First(n => !n.Raft.IsLeader);
            await current.Raft.ResignAsync();

            // 换届窗口容忍：新 leader 收敛后直读（NotLeader 重试）
            long readIndex = 0;
            for (var attempt = 0; ; attempt++)
            {
                var next = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader);
                if (next is null) { await Task.Delay(50); continue; }
                try
                {
                    readIndex = await next.Raft.ReadIndexAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    break;
                }
                catch (NotLeaderException) when (attempt < 40) { await Task.Delay(50); }
            }
            readIndex.Should().BeGreaterThanOrEqualTo(committed,
                $"新 leader（轮 {round}）ReadIndex ≥ 前任期已提交水位——锚点未建时挂起不破约");
        }
    }

    /// <summary>B 行·WaitForAppliedAsync：已 applied 位点同步立即返回；未到位点挂起、
    /// 提交推进 applied 后完成（read-your-writes 等待段）。</summary>
    [Fact]
    public async Task WaitForAppliedAsync_CompletesWhenAppliedAdvances()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var (index, completing) = await ReplicateRetryAsync(leader, new byte[] { 0x33 });
        await WaitForAsync(() => completing.Raft.CommitIndex >= index);

        // 已 applied 位点——同步立即返回
        var done = completing.Raft.WaitForAppliedAsync(index);
        done.IsCompleted.Should().BeTrue("已 applied 位点——同步返回");

        // 未到位点——挂起；再提交两条推进 applied 后完成
        var far = index + 2;
        var pending = completing.Raft.WaitForAppliedAsync(far).AsTask();
        pending.IsCompleted.Should().BeFalse("applied 未到 far——挂起等待");
        await ReplicateRetryAsync(completing, new byte[] { 0x34 });
        await ReplicateRetryAsync(completing, new byte[] { 0x35 });
        await pending.WaitAsync(TimeSpan.FromSeconds(5));   // applied 推进过 far → 完成
    }
    // ═══ 二期-D2/D3：领导权定向转让 + 优雅 drain（验证矩阵 D 行）═══

    /// <summary>D2：转让成功——目标追平后 TimeoutNow → 目标立即真选举当选、任期前进；
    /// 原 leader 降级为 follower（零 leadership 真空——转让面核心契约）。</summary>
    [Fact]
    public async Task TransferLeadership_TargetWins_TermAdvances()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var follower = Followers(fx.Nodes).First();
        var termBefore = leader.Raft.CurrentTerm;
        await ReplicateRetryAsync(leader, new byte[] { 0x01 });

        await leader.Raft.TransferLeadershipAsync(follower.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        follower.Raft.IsLeader.Should().BeTrue("转让目标当选（TimeoutNow 立即选举）");
        follower.Raft.CurrentTerm.Should().BeGreaterThan(termBefore, "真选举抬任期");
        leader.Raft.IsLeader.Should().BeFalse("原 leader 已让位");
    }

    /// <summary>D2：目标不可达——超时回退（本端仍在位可继续服务，转让意图作废）。</summary>
    [Fact]
    public async Task TransferLeadership_TargetUnreachable_TimesOutAndRetains()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var followers = Followers(fx.Nodes).ToArray();
        var target = followers[0];
        await target.Transport.DisposeAsync();   // ★ 目标自身不可达——永不追平（其余节点正常）

        var act = async () => await leader.Raft.TransferLeadershipAsync(target.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        (await act.Should().ThrowAsync<TimeoutException>().WaitAsync(TimeSpan.FromSeconds(15)))
            .Which.Message.Should().Contain("超时回退");
        leader.Raft.IsLeader.Should().BeTrue("超时回退——本端仍在位可继续服务");
    }

    /// <summary>D3：drain 窗口内新提案立即拒绝（NotLeader 重路由语义）；drain 完成后让位。</summary>
    [Fact]
    public async Task Drain_RejectsNewProposals_ThenResigns()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        await WaitForAsync(() => Followers(fx.Nodes).All(f => f.Raft.CommitIndex >= leader.Raft.CommitIndex - 1));

        var drain = leader.Raft.DrainAsync(transferTarget: null, drainWindow: TimeSpan.FromMilliseconds(400));

        // 窗口内新提案——立即拒绝（重路由语义，不挂起）
        var act = () => leader.Raft.ReplicateAsync(new byte[] { 0xEE }).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        (await act.Should().ThrowAsync<NotLeaderException>().WaitAsync(TimeSpan.FromSeconds(3)))
            .Which.Message.Should().NotBeNull("drain 中拒绝新提案");

        await drain.AsTask().WaitAsync(TimeSpan.FromSeconds(10));   // 排空窗 + 自降级
        leader.Raft.IsLeader.Should().BeFalse("drain 完成 = 已让位（与 ResignAsync 配合）");
    }

    /// <summary>D3+D2 组合：定向 drain——转让给指定 follower，零 leadership 真空。</summary>
    [Fact]
    public async Task Drain_WithTransferTarget_ZeroVacancy()
    {
        await using var fx = await CreateClusterAsync(3);
        var leader = await WaitStableLeaderAsync(fx);
        var follower = Followers(fx.Nodes).First();
        await WaitForAsync(() => Followers(fx.Nodes).All(f => f.Raft.LeaderId == leader.Id));

        await leader.Raft.DrainAsync(transferTarget: follower.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        follower.Raft.IsLeader.Should().BeTrue("定向 drain——转让完成即接管");
        leader.Raft.IsLeader.Should().BeFalse("原 leader 让位");
    }

    /// <summary>I2：Raft 共识维度视图接线（二期-I——hub 注入 raft：选举/换届/水位指标经
    /// RecordingMetricsSink 捕获断言——视图唯一接入点验收面）。</summary>
    [Fact]
    public async Task RaftView_MetricsWired_ElectionsAndLeaderChanges()
    {
        var sink = new Fixtures.RecordingMetricsSink();
        await using var fx = await CreateClusterAsync(3, hubFactory: () => ObservabilityHub.Create(sink, null,
            new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = true, EnableRaftMetrics = true } }));
        var leader = await WaitStableLeaderAsync(fx);
        await ReplicateRetryAsync(leader, new byte[] { 0x01 });
        await WaitForAsync(() => fx.Nodes.All(n => !n.Machine.Applied.IsEmpty));

        sink.CountOf("raft.elections_started").Should().BeGreaterThanOrEqualTo(1, "至少一次真选举");
        sink.CountOf("raft.leader_changes").Should().BeGreaterThanOrEqualTo(1, "至少一次换届事件");
        sink.LastGaugeOf("raft.commit_index").Should().BeGreaterThanOrEqualTo(1, "提交水位 gauge 可见");
        sink.LastGaugeOf("raft.applied_index").Should().BeGreaterThanOrEqualTo(1, "应用水位 gauge 可见");
    }

}
