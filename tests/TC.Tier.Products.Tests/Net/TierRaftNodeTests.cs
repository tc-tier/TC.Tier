using TC.Tier.Core.Logging;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// TierRaftNode 产品装配集成测试（D6 接线收口）：3 节点 InProcess 全链——选举收敛 / 复制+apply /
/// 宿主快照压缩调度（增长阈值 → SnapshotIndex 推进）/ 版本发布与反熵循环挂载（leader 门）。
/// </summary>
public class TierRaftNodeTests
{
    /// <summary>计数状态机（apply 面观测——at-least-once 容忍）。</summary>
    private sealed class CountingMachine : IStateMachine
    {
        public long Applied;
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Applied);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>阶段日志（诊断用——无锁队列缓冲 + finally 批量落盘）。★ 不得在测试路径上同步写盘：
    /// File.AppendAllText 逐行同步写曾把 raft 循环堵在 CreateFile 内核调用里 30s（T6 取证 0x80c8 实锤
    /// ——测量仪器成为故障源）；队列入队 O(1) 永不阻塞，落盘在 finally（断言失败路径也保住日志）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> LogBuffer = new();

    private static void LogStage(string message)
    {
        // ★ 有界（5000 行，丢最旧留最新——取证要的是尾）；无界队列在选举风暴日志洪流下
        //   曾把 testhost 撑到 22GB（旧版同步写盘客观上当了限流器，缓冲化拆掉背压——判例）
        while (LogBuffer.Count >= 5000) LogBuffer.TryDequeue(out _);
        LogBuffer.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    private static void FlushStageLog()
    {
        try
        {
            while (LogBuffer.TryDequeue(out var line))
                File.AppendAllText("F:/tier-test-tmp/tcn-stages.log", line + Environment.NewLine);
        }
        catch { /* 诊断日志容错 */ }
    }

    private sealed class FileLogger(string? prefix = null) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log(LogLevel logLevel, string message, Exception? exception = null)
            => LogStage($"[{logLevel}]{(prefix is null ? "" : $"[{prefix}]")} {message} {exception?.Message}");
    }

    private static TierRaftNodeOptions NodeOptions() => TierRaftNodeOptions.Default
        .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))   // 冗余时间循环关（raft 显式提交驱动）
        .WithRaft(RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)))          // 放宽——WAL 提交冷路径期间不被心跳超时打掉
        .WithHostLoopInterval(TimeSpan.FromMilliseconds(250))
        .WithSnapshotGrowthThresholdEntries(20)
        .WithSnapshotBlockSize(4096)
        .WithSwarm(SwarmOptions.Default)
        .WithAntiEntropyInterval(TimeSpan.FromSeconds(1));

    // ═══ 多节点活选举拓扑（跨 OS 验证仪——默认选举超时=产品姿态）═══
    // Windows 本机观测：默认 150-300ms 选举窗在宿主调度+WAL 提交并存时换届风暴（term 反复、
    // 复制 InFlightSlot 等待秒级）——W4 persist 下环议题。Linux 环境多节点验证以本测试为准。

    /// <summary>多节点配置（默认选举超时；阈值 20 强场景——压缩×复制并存）。
    /// ★ 历史：自压缩 × AppendEntries 直排竞态曾成热循环（T6）——已结构修复：
    /// 压缩经 followerAppendGate+store 门双串行（宿主走 RunUnderAppendGateAsync）+
    /// 冲突前向 hint 跳前收敛（min 回退公式对前向 hint 是不动点——261 万次重试零进展实测）。</summary>
    private static TierRaftNodeOptions MultiNodeOptions() => TierRaftNodeOptions.Default
        .WithHostLoopInterval(TimeSpan.FromMilliseconds(250))
        .WithSnapshotGrowthThresholdEntries(20)
        .WithSnapshotBlockSize(4096)
        .WithSwarm(SwarmOptions.Default)
        .WithAntiEntropyInterval(TimeSpan.FromSeconds(1));

    [Fact]
    public async Task MultiNode_Elect_Replicate_SnapshotScheduled()
    {
        // 高精度定时环境由产品默认路径覆盖（TierRaftNodeOptions.HighResolutionTimer 缺省开）——测试反映默认装配
        await using var hub = new InProcessTransportHub();
        var members = Enumerable.Range(0, 3).Select(_ => new ClusterMember(NodeId.NewRandom(), "")).ToArray();
        var config = new ClusterConfig(members);
        var nodes = new List<(TierRaftNode Node, CountingMachine Machine)>();

        try
        {
            foreach (var member in members)
            {
                var machine = new CountingMachine();
                var transport = hub.Register(member.Id);
                LogStage($"节点 {member.Id} StartAsync 开始");
                var node = await TierRaftNode.StartAsync(member.Id, TC.Tier.Core.IO.TierFs.New("memory:"),
                    transport, config, machine, MultiNodeOptions(), new FileLogger())
                    .WaitAsync(TimeSpan.FromSeconds(30));
                LogStage($"节点 {member.Id} StartAsync 完成");
                nodes.Add((node, machine));
            }

            // leader 收敛（30s 有界——默认选举超时量级）
            var deadline = DateTime.UtcNow.AddSeconds(30);
            TierRaftNode? leader = null;
            while (DateTime.UtcNow < deadline && leader is null)
            {
                leader = nodes.FirstOrDefault(n => n.Node.Raft.IsLeader).Node;
                if (leader is null) await Task.Delay(50);
            }
            leader.Should().NotBeNull("3 节点必须收敛出 leader");
            var leaderNode = leader!;

            // ★ T6 取证快照（停滞面判别）：loopLag 大=循环卡 handler；persisted 落后=复制/提交未达；
            //   persisted 齐+applied 落=apply worker 停滞；c（commitIndex）落后=多数派 matchIndex 未达
            string NodeSnapshot() => string.Join(" | ", nodes.Select(n =>
            {
                var d = n.Node.Raft.DiagnoseLoop();
                return $"{n.Node.Id.ToString()[..6]}:{n.Node.Raft.Role}/t{n.Node.Raft.CurrentTerm}" +
                       $"/c{n.Node.Raft.CommitIndex}/applied={Interlocked.Read(ref n.Machine.Applied)}" +
                       $"/p={n.Node.Wal.PersistedIndex}/n0={n.Node.Wal.SnapshotIndex}" +
                       $"/loopLag={d.LoopLagMs}/tickLag={d.TickLagMs}/q={d.QueueDepth}/dl={d.DeadlineInMs}";
            }));

            // 复制 25 条（committed 档；NotLeader=换届常态——spec-08 客户端重试，有界快失败）
            for (var i = 0; i < 25; i++)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var l = nodes.FirstOrDefault(n => n.Node.Raft.IsLeader).Node;
                    if (l is null && attempt < 200) { await Task.Delay(25); continue; }
                    if (l is null) throw new InvalidOperationException($"第 {i} 条持续无 leader（换届风暴）");
                    try
                    {
                        LogStage($"复制 {i} → {l.Id} 开始");
                        await l.Raft.ReplicateCommittedAsync(BitConverter.GetBytes(i))
                            .AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                        LogStage($"复制 {i} 完成");
                        break;
                    }
                    catch (NotLeaderException) when (attempt < 200)
                    {
                        LogStage($"复制 {i} NotLeader（重试 {attempt}）");
                        await Task.Delay(25);
                    }
                    catch (TimeoutException)
                    {
                        LogStage($"T6 写挂取证（第 {i} 条 → {l.Id.ToString()[..6]}）：{NodeSnapshot()}");
                        throw;
                    }
                }
            }

            // applied 传播（90s 有界——apply worker 异步；二期新增测试增加套件负载，放宽窗口）
            deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline
                   && nodes.Any(n => Interlocked.Read(ref n.Machine.Applied) < 25))
                await Task.Delay(50);
            if (nodes.Any(n => Interlocked.Read(ref n.Machine.Applied) < 25))
                LogStage($"T6 applied 取证：{NodeSnapshot()}");
            nodes.Select(n => Interlocked.Read(ref n.Machine.Applied))
                .Should().OnlyContain(c => c >= 25, "全节点 apply ≥ 提交条数（at-least-once）");

            // 宿主快照压缩调度（阈值 20 强场景——增长 25 ≥ 20，压缩×复制并存下必须收敛）
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && nodes.All(n => n.Node.Wal.SnapshotIndex <= 0))
                await Task.Delay(50);
            nodes.Max(n => n.Node.Wal.SnapshotIndex).Should().BeGreaterThan(0,
                "增长 25 ≥ 阈值 20——宿主压缩调度应已触发（压缩×复制竞态修复后强断言）");
        }
        finally
        {
            FlushStageLog();
            foreach (var (node, _) in nodes)
                await node.DisposeAsync();
            try
            {
                await hub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception) { /* 枢纽释放超时——节点已释放，进程退出兜底 */ }
        }
    }

    /// <summary>
    /// 多节点 HDD 磁盘 DIO+WD 形态（raft 磁盘部署建议档——本地真卷 + NoBuffering|WriteThrough
    /// hints，默认 150-300ms 选举窗 = 产品姿态）：压缩×复制并存（阈值 20）+ 稳相无换届断言
    /// （复制+压缩全程 leader/term 不变）——磁盘部署换届风暴回归门。
    /// </summary>
    [SkippableFact]
    public async Task MultiNode_HddDioWd_Elect_Replicate_SnapshotScheduled_NoStorm()
    {
        // ★ 真盘回归门（HDD 磁盘部署换届风暴）——opt-in：TC_TEST_HDD_GATE=1 时才跑（本地真卷
        // + DIO+WD 选举窗 150-300ms 为真盘时序，CI/mem 环境无此介质形态必超时）。
        Skip.IfNot(Environment.GetEnvironmentVariable("TC_TEST_HDD_GATE") == "1",
            "真盘 HDD DIO+WD 回归门——设 TC_TEST_HDD_GATE=1 于回归箱运行。");
        var dir = TestTempDir.Create("tc-raft-hdd-diowd");
        await using var hub = new InProcessTransportHub();
        var members = Enumerable.Range(0, 3).Select(_ => new ClusterMember(NodeId.NewRandom(), "")).ToArray();
        var config = new ClusterConfig(members);
        var nodes = new List<(TierRaftNode Node, CountingMachine Machine, TC.Tier.Core.IO.IFileSystem Fs)>();
        // ★ DIO+WD = 磁盘部署建议档（TierWalOptions.Hints 文档：NoBuffering|WriteThrough 全介质最优）；
        //   冗余时间循环关（raft 显式提交驱动）——其余全部产品默认（选举窗 150-300ms 不放宽）。
        var opts = MultiNodeOptions().WithWal(TC.Tier.Products.Wal.TierWalOptions.Default
            .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
            .WithHints(TC.Tier.Core.IO.FileOpenHints.NoBuffering | TC.Tier.Core.IO.FileOpenHints.WriteThrough));
        var log = new FileLogger("hdd");

        try
        {
            foreach (var member in members)
            {
                var machine = new CountingMachine();
                var fs = TC.Tier.Core.IO.TierFs.New($"local:///{dir.Replace('\\', '/')}/node-{member.Id.ToString()[..8]}");
                var transport = hub.Register(member.Id);
                var node = await TierRaftNode.StartAsync(member.Id, fs, transport, config, machine, opts,
                        new FileLogger(member.Id.ToString()[..6]))
                    .WaitAsync(TimeSpan.FromSeconds(30));
                nodes.Add((node, machine, fs));
            }

            // leader 收敛（默认 150-300ms 选举窗——30s 有界）
            var deadline = DateTime.UtcNow.AddSeconds(30);
            TierRaftNode? leader = null;
            while (DateTime.UtcNow < deadline && leader is null)
            {
                leader = nodes.FirstOrDefault(n => n.Node.Raft.IsLeader).Node;
                if (leader is null) await Task.Delay(50);
            }
            leader.Should().NotBeNull("3 节点必须收敛出 leader");
            var steadyLeader = leader!;
            var steadyTerm = steadyLeader.Raft.CurrentTerm;
            LogStage($"[hdd] 稳相起点 leader={steadyLeader.Id.ToString()[..6]} term={steadyTerm}");

            // 复制 25 条（阈值 20——压缩×复制并存；NotLeader=换届常态重试）
            for (var i = 0; i < 25; i++)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var l = nodes.FirstOrDefault(n => n.Node.Raft.IsLeader).Node;
                    if (l is null && attempt < 200) { await Task.Delay(25); continue; }
                    if (l is null)
                    {
                        LogStage($"[hdd] T6 取证（第 {i} 条持续无 leader）：{NodeSnapshot(nodes)}");
                        throw new InvalidOperationException($"第 {i} 条持续无 leader（换届风暴）");
                    }
                    try
                    {
                        await l.Raft.ReplicateCommittedAsync(BitConverter.GetBytes(i))
                            .AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                        break;
                    }
                    catch (NotLeaderException) when (attempt < 200)
                    {
                        LogStage($"[hdd] 复制 {i} NotLeader（重试 {attempt}）");
                        await Task.Delay(25);
                    }
                    catch (TimeoutException)
                    {
                        LogStage($"[hdd] T6 写挂取证（第 {i} 条）：{NodeSnapshot(nodes)}");
                        throw;
                    }
                }
            }

            // ★ 收尾屏障写（soak 同款口径）：末条恰逢换届窗口时——旧 term 条目已达 quorum 但新 leader
            //   按 §5.4.2 保守语义不直接提交，须本任期新条目间接提交；无新写入则 commit 停在前任
            //   （协议正确非缺陷）。并行负载下的合法单次换届即走此路径。
            for (var attempt = 0; ; attempt++)
            {
                var l = nodes.FirstOrDefault(n => n.Node.Raft.IsLeader).Node;
                if (l is null && attempt < 200) { await Task.Delay(25); continue; }
                if (l is null) throw new InvalidOperationException("屏障写持续无 leader（换届风暴）");
                try
                {
                    await l.Raft.ReplicateCommittedAsync(BitConverter.GetBytes(999))
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                    break;
                }
                catch (NotLeaderException) when (attempt < 200) { await Task.Delay(25); }
            }

            // applied 收敛（30s 有界——apply worker 异步）——屏障条（26）驱动提交链越过换届窗口
            deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && nodes.Any(n => Interlocked.Read(ref n.Machine.Applied) < 25))
                await Task.Delay(50);
            if (nodes.Any(n => Interlocked.Read(ref n.Machine.Applied) < 25))
                LogStage($"[hdd] T6 applied 取证：{NodeSnapshot(nodes)}");
            nodes.Select(n => Interlocked.Read(ref n.Machine.Applied))
                .Should().OnlyContain(c => c >= 25, "全节点 apply ≥ 提交条数（at-least-once）");

            // 宿主压缩（阈值 20 强断言——HDD DIO+WD 下压缩×复制并存收敛）
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && nodes.All(n => n.Node.Wal.SnapshotIndex <= 0))
                await Task.Delay(50);
            nodes.Max(n => n.Node.Wal.SnapshotIndex).Should().BeGreaterThan(0,
                "增长 25 ≥ 阈值 20——宿主压缩应已触发");

            // ★ 风暴断言（有界换届口径）：复制+压缩全程 term 增量 ≤ 2（=至多两轮合法选举——
            //   稳相期选举风暴=term 连续跳档即红）；单次换届（§5.4.2 保守提交语义触发）合法。
            var endTerm = nodes.Max(n => n.Node.Raft.CurrentTerm);
            (endTerm - steadyTerm).Should().BeLessThanOrEqualTo(2,
                $"稳相 term {steadyTerm}→{endTerm}——增量超界=换届风暴");
            nodes.Count(n => n.Node.Raft.IsLeader).Should().Be(1, "集群收敛单 leader");
        }
        finally
        {
            FlushStageLog();
            foreach (var (node, _, _) in nodes) await node.DisposeAsync();
            foreach (var (_, _, fs) in nodes) fs.Dispose();   // local 卷 Dispose=目录递归清理
            TestTempDir.TryCleanup(dir);
        }
    }

    /// <summary>全节点水位快照（HDD 形态取证——水位/视图/循环滞后一屏判别）。</summary>
    private static string NodeSnapshot(List<(TierRaftNode Node, CountingMachine Machine, TC.Tier.Core.IO.IFileSystem Fs)> nodes)
        => string.Join(" | ", nodes.Select(n =>
        {
            var d = n.Node.Raft.DiagnoseLoop();
            return $"{n.Node.Id.ToString()[..6]}:{n.Node.Raft.Role}/t{n.Node.Raft.CurrentTerm}" +
                   $"/c{n.Node.Raft.CommitIndex}/applied={Interlocked.Read(ref n.Machine.Applied)}" +
                   $"/p={n.Node.Wal.PersistedIndex}/alloc={n.Node.Wal.AllocatedIndex}/n0={n.Node.Wal.SnapshotIndex}" +
                   $"/loopLag={d.LoopLagMs}";
        }));

    [Fact]
    public async Task Standalone_Node_Elect_Replicate_SnapshotScheduled()
    {
        // ★ N=1 Standalone（零选举——单测确定性）：装配接线全链验证——
        //   TierWal→适配器→apply→raft 组装、复制提交（本地多数派）、宿主压缩调度、版本发布。
        //   （多节点活选举拓扑 = 时序对抗形态——归对抗套件，W4 persist 下环后回迁）
        using var vol = new TestVolume();
        var machine = new CountingMachine();
        var hub = new InProcessTransportHub();
        try
        {
            var member = new ClusterMember(NodeId.NewRandom(), "");
            var transport = hub.Register(member.Id);
            var node = await TierRaftNode.StartAsync(member.Id, TC.Tier.Core.IO.TierFs.New("memory:"),
                transport, new ClusterConfig([member]), machine, NodeOptions()).WaitAsync(TimeSpan.FromSeconds(30));
            node.Raft.IsLeader.Should().BeTrue("N=1 Standalone 启动即 leader");

            for (var i = 0; i < 25; i++)
                await node.Raft.ReplicateCommittedAsync(BitConverter.GetBytes(i))
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(30));

            // applied/快照压缩均为异步（apply worker + 宿主循环 tick）——有界轮询
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline
                   && (Interlocked.Read(ref machine.Applied) < 25 || node.Wal.SnapshotIndex <= 0))
                await Task.Delay(50);

            Interlocked.Read(ref machine.Applied).Should().BeGreaterThanOrEqualTo(25);
            node.Wal.SnapshotIndex.Should().BeGreaterThan(0, "增长 25 ≥ 阈值 20——宿主压缩调度应已触发");
            node.Swarm.Should().NotBeNull();
        }
        finally
        {
            await hub.DisposeAsync();
        }
    }
}
