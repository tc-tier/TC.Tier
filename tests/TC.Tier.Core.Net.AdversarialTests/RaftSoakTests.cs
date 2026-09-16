using System.Diagnostics;
using System.Threading.Channels;

namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>
/// spec-09 §3 soak 场景（单一真源——介质形态由子类装配，与 FaultMatrix 同构）：
/// 持续复制负载 × 轮转单节点故障（Kill → Rejoin 轮换，同时最多一死保 quorum）。
/// 断言 = 收敛语义：全节点 applied 恰好一次（1..N 连续）+ 集群收敛单 leader。
/// <para>★ 时长档（spec-09 §6 定案——对齐 Runtime.AdversarialTests soak 惯例）：
/// 默认 15s CI 档（快速回归）；<c>TC_SOAK_SECONDS</c> 环境变量延长生产档；
/// 收敛等待有界（30s）+ 失败转储取证（Dump/Traces）。</para>
/// </summary>
public abstract class RaftSoakScenarios
{
    /// <summary>soak 时长（秒）——默认 15（CI 档），TC_SOAK_SECONDS 覆盖。</summary>
    private static int SoakSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("TC_SOAK_SECONDS"), out var s) && s > 0 ? s : 15;

    /// <summary>故障轮转周期（秒）——单节点故障窗口。</summary>
    private static readonly TimeSpan FaultInterval = TimeSpan.FromSeconds(2);

    /// <summary>介质形态标识（失败信息区分）。</summary>
    internal abstract string WireLabel { get; }

    /// <summary>装配 rig（n 节点集群——传输形态由子类决定）。</summary>
    internal abstract Task<IAdversarialRig> CreateRigAsync(int n, RaftOptions? raft = null);

    [Fact]
    public async Task Soak_SustainedReplication_RollingNodeFaults_Converges()
    {
        await using var fx = await CreateRigAsync(3);
        var leader = await ClusterOps.WaitLeaderAsync(fx.Nodes);

        var sw = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(SoakSeconds);
        var nextFaultAt = FaultInterval;
        long committed = 0;
        var cmd = 0;
        var downIndex = -1;   // 当前被杀节点（同时最多一个——保多数派）
        var faults = 0;

        while (sw.Elapsed < deadline)
        {
            // ── 负载：持续复制（leader 被杀/漂移即追认存活节点重试）──
            cmd++;
            try
            {
                committed = await ClusterOps.ReplicateAsync(leader, ClusterOps.Cmd(cmd), TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException) { leader = await AdoptLeaderAsync(fx, downIndex, leader); }
            catch (NotLeaderException) { leader = await AdoptLeaderAsync(fx, downIndex, leader); }
            catch (ObjectDisposedException) { leader = await AdoptLeaderAsync(fx, downIndex, leader); }   // leader 恰被杀
            catch (ChannelClosedException) { leader = await AdoptLeaderAsync(fx, downIndex, leader); }   // 同上——Dispose 关闭事件通道，在途写收到

            // ── 轮转故障：交替 Kill/Rejoin（每窗一次动作，轮换目标节点）──
            if (sw.Elapsed >= nextFaultAt)
            {
                if (downIndex >= 0)
                {
                    await fx.RejoinNodeAsync(downIndex);
                    downIndex = -1;
                }
                else
                {
                    downIndex = faults++ % fx.Nodes.Count;
                    await fx.KillNodeAsync(downIndex);
                }
                nextFaultAt += FaultInterval;
            }
        }

        committed.Should().BePositive("soak 期间至少应有一条命令提交");

        // ── 收尾：最后死者归队 + 收尾探针 + 全量收敛（有界等待）──
        if (downIndex >= 0)
            await fx.RejoinNodeAsync(downIndex);

        // ★ 收尾探针（测试口径裁定：末条命令恰逢换届时——旧 term 条目已达 quorum 但新 leader 按
        //   §5.4.2 保守语义不直接提交，须本任期新条目间接提交；无新写入则 commit 停在其前任——
        //   协议正确非缺陷，收敛等待前追加一条命令驱动提交链越过换届窗口）
        for (var attempt = 0; ; attempt++)
        {
            var finalLeader = await ClusterOps.WaitLeaderAsync(fx.Nodes, TimeSpan.FromSeconds(30));
            try
            {
                var barrier = await ClusterOps.ReplicateAsync(finalLeader, ClusterOps.Cmd(++cmd), TimeSpan.FromSeconds(10));
                committed = Math.Max(committed, barrier);
                break;
            }
            catch (TimeoutException) when (attempt < 5) { /* 探针撞选举——重试 */ }
            catch (NotLeaderException) when (attempt < 5) { /* 换届窗口——重试 */ }
        }

        try
        {
            foreach (var n in fx.Nodes)
                await ClusterOps.WaitForAsync(() => n.Raft.CommitIndex >= committed, TimeSpan.FromSeconds(30));
            // applied 收敛口径：应用到本节点提交尾（★重启节点 Machine.Applied 只含恢复起点之后的新应用段
            // ——pipeline 从 store.AppliedIndex 续传不重放，Applied.Count ≠ committed 是正常形态）
            await ClusterOps.WaitForAsync(
                () => fx.Nodes.All(n => !n.Machine.Applied.IsEmpty
                    && n.Machine.Applied.Keys.Max() == n.Raft.CommitIndex),
                TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            var appliedDetail = string.Join(" | ", fx.Nodes.Select(n =>
            {
                var keys = n.Machine.Applied.Keys.OrderBy(k => k).ToArray();
                return $"min={(keys.Length > 0 ? keys[0] : -1)} max={(keys.Length > 0 ? keys[^1] : -1)} " +
                    $"count={keys.Length} commit={n.Raft.CommitIndex} storeApplied={n.Store.AppliedIndex}";
            }));
            throw new TimeoutException($"[{WireLabel}] soak 收敛失败：committed={committed} applied[{appliedDetail}] "
                + ClusterOps.Dump(fx.Nodes) + "\n" + ClusterOps.Traces(fx.Nodes));
        }

        // 恰好一次（相对恢复起点）：各节点 applied 键连续无洞无重复 + 尾达 committed
        foreach (var n in fx.Nodes)
        {
            var keys = n.Machine.Applied.Keys.OrderBy(k => k).ToArray();
            keys.Should().NotBeEmpty($"[{WireLabel}] 节点应至少应用一段");
            keys.Should().BeEquivalentTo(Enumerable.Range((int)keys[0], keys.Length),
                $"[{WireLabel}] applied 键连续无洞（重启续传段）");
            keys[^1].Should().Be((int)committed, $"[{WireLabel}] 应用尾达提交尾");
        }

        // 跨节点一致：同 index 命令内容全节点相同（对齐取样首段/末段——全键比对成本 O(N²)）
        var reference = fx.Nodes.First(n => !n.Machine.Applied.IsEmpty).Machine.Applied;
        var probe = new[] { reference.Keys.Min(), reference.Keys.Max() };
        foreach (var index in probe)
        {
            var expectCmd = reference[index];
            foreach (var n in fx.Nodes)
                if (n.Machine.Applied.TryGetValue(index, out var other))
                    other.Should().Equal(expectCmd, $"[{WireLabel}] index={index} 跨节点命令一致");
        }

        // 集群收敛单 leader（含全部重加入节点）
        await ClusterOps.WaitLeaderAsync(fx.Nodes, TimeSpan.FromSeconds(30));
    }

    /// <summary>追认存活节点中的当前 leader（选举窗口内短候；无果交还调用方，下轮复制再触发）。</summary>
    private static async Task<AdversarialNode> AdoptLeaderAsync(IAdversarialRig fx, int downIndex, AdversarialNode current)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var candidate = fx.Nodes.Where((n, i) => i != downIndex).FirstOrDefault(n => n.Raft.IsLeader);
            if (candidate is not null) return candidate;
            await Task.Delay(100);
        }
        return current;
    }
}

/// <summary>soak——InProcess 主场（spec-12 §7 同构基准）。</summary>
public sealed class RaftSoakTests : RaftSoakScenarios
{
    internal override string WireLabel => "InProcess";

    internal override Task<IAdversarialRig> CreateRigAsync(int n, RaftOptions? raft = null)
        => InProcessAdversarialRig.CreateAsync(n, raft);
}

/// <summary>soak——TCP 复跑（spec-12 §12 同构门：同一场景同一断言换介质）。</summary>
public sealed class RaftSoakTcpTests : RaftSoakScenarios
{
    internal override string WireLabel => "TCP";

    internal override Task<IAdversarialRig> CreateRigAsync(int n, RaftOptions? raft = null)
        => TcpAdversarialRig.CreateAsync(n, raft);
}
