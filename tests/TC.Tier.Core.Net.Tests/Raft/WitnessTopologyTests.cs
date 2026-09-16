using FluentAssertions;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Tests.Fixtures;
using RaftEngineTestsType = TC.Tier.Core.Net.Tests.Raft.RaftEngineTests;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Witness/仲裁者角色（二期-F2——DDR-F2 验收，2+1 拓扑选主/容灾测试）：
/// ① 计票语义——witness 计入选主/提交多数派（VoterCount/Majority），learner 维持排除；
/// ② 选主——witness 不自荐（全 voter 停机时 witness 独存不称王；存活 voter 当选）；
/// ③ 容灾·可写——单 voter 宕机，Voter+Witness 多数派继续提交；
/// ④ 容灾·安全——witness 独存不产生分裂 leader；
/// ⑤ 足迹——高水位单调推进、无日志体（Append/TryGet 诚实拒绝）。
/// </summary>
public class WitnessTopologyTests
{
    private static async Task<(RaftEngineTests.ClusterRig Rig, RaftEngineTests.TestNode V0, RaftEngineTests.TestNode V1, RaftEngineTests.TestNode W)> CreateTwoPlusOneAsync()
    {
        var rig = await RaftEngineTests.CreateClusterAsync(3, roleFactory: i => i == 2 ? ClusterMemberRole.Witness : ClusterMemberRole.Voter);
        var witness = rig.Nodes[2];
        witness.Store.Should().BeOfType<WitnessHighWaterStore>("witness 成员自动配高水位存储（装配一致性）");
        return (rig, rig.Nodes[0], rig.Nodes[1], witness);
    }

    /// <summary>验收①：计票语义（配置层）。</summary>
    [Fact]
    public void Config_WitnessCountsForQuorum_LearnerExcluded()
    {
        var v1 = NodeId.NewRandom();
        var v2 = NodeId.NewRandom();
        var w = NodeId.NewRandom();
        var l = NodeId.NewRandom();
        var config = new ClusterConfig(new[]
        {
            new ClusterMember(v1, ""), new ClusterMember(v2, ""),
            new ClusterMember(w, "", ClusterMemberRole.Witness),
            new ClusterMember(l, "", ClusterMemberRole.Learner),
        });

        config.VoterCount.Should().Be(3, "2 voter + 1 witness（learner 不计）");
        config.MajorityThreshold.Should().Be(2);
        config.IsVoter(w).Should().BeTrue("witness 计入选主/提交多数派");
        config.IsVoter(l).Should().BeFalse("learner 维持排除");
        config.IsWitness(w).Should().BeTrue();
        config.IsFullVoter(w).Should().BeFalse("witness 不可承接 leader（转让门）");
        config.IsFullVoter(v1).Should().BeTrue();
    }

    /// <summary>验收②+③：2+1 拓扑选主 + 单 voter 宕机可写 + witness 不自荐。</summary>
    [Fact]
    public async Task TwoPlusOne_SingleVoterDown_ClusterKeepsCommitting()
    {
        var (rig, v0, v1, witness) = await CreateTwoPlusOneAsync();
        await using var rigRef = rig;
        var leader = await RaftEngineTests.WaitStableLeaderAsync(rig);
        var follower = rig.Nodes.Single(n => n.Id != leader.Id && n.Id != witness.Id);

        // 稳定态提交
        var (seq1, _) = await RaftEngineTests.ReplicateRetryAsync(leader.Raft is null ? leader : leader, new byte[] { 1 });
        seq1.Should().BeGreaterThan(0);
        await RaftEngineTests.WaitForAsync(() => follower.Machine.Applied.ContainsKey(seq1), TimeSpan.FromSeconds(10));


        // 一个 voter 宕机 → Leader+Witness 仍是多数派（2/3）——集群继续提交
        var victim = leader.Id == v0.Id ? follower : leader;
        await victim.DisposeAsync();

        var survivor = rig.Nodes.Single(n => n.Id != victim.Id && n.Id != witness.Id);
        await RaftEngineTests.WaitForAsync(() => survivor.Raft.IsLeader, TimeSpan.FromSeconds(15));

        long seq2;
        try
        {
            (seq2, _) = await RaftEngineTests.ReplicateRetryAsync(survivor, new byte[] { 2 }, maxAttempts: 8);
        }
        catch (Exception)
        {
            var wStore = (WitnessHighWaterStore)witness.Store;
            var snap = survivor.Raft.GetStateSnapshot();
            var lanes = string.Join(",", (snap.Members ?? []).Select(m => $"{m.Id.ToString()[..6]}:match={m.MatchIndex}"));
            throw new TimeoutException(
                $"survivor={survivor.Raft.Role} lead={survivor.Raft.LeaderId} term={snap.Term} commit={snap.CommitIndex} last={snap.LastLogIndex} " +
                $"lanes=[{lanes}] | wStore hw={wStore.LastLogIndex}/{wStore.LastLogTerm} " +
                $"wTerm={witness.Raft.GetStateSnapshot().Term} wRole={witness.Raft.Role} wLead={witness.Raft.LeaderId}");
        }
        seq2.Should().BeGreaterThan(seq1, "Voter+Witness 多数派继续提交（2+1 容灾核心）");

        // witness 高水位随断言流推进
        var hw = (WitnessHighWaterStore)witness.Store;
        hw.LastLogIndex.Should().BeGreaterThanOrEqualTo(seq2, "高水位跟随 leader 前沿断言");
    }

    /// <summary>验收④：witness 独存不产生分裂 leader（安全停摆）。</summary>
    [Fact]
    public async Task WitnessAlone_NeverBecomesLeader()
    {
        var (rig, v0, v1, witness) = await CreateTwoPlusOneAsync();
        await using var rigRef = rig;
        await RaftEngineTests.WaitStableLeaderAsync(rig);

        // 两 voter 全宕——witness 独存：多数派 2/3 不可达，且 witness 不自荐
        foreach (var n in new[] { v0, v1 })
        {
            await n.DisposeAsync();
        }

        await Task.Delay(3000);   // 多个选举窗——断言无分裂 leader 产生
        witness.Raft.IsLeader.Should().BeFalse("witness 永不自荐（高水位无日志体）");
    }

    /// <summary>验收⑤：高水位存储足迹——断言推进单调、无日志体。</summary>
    [Fact]
    public async Task WitnessHighWaterStore_AssertMonotonic_NoLogBody()
    {
        var store = new WitnessHighWaterStore();
        await store.InitializeAsync();
        store.LastLogIndex.Should().Be(0);

        (await store.AssertHighWatermarkAsync(5, 1)).Should().BeTrue();
        (await store.AssertHighWatermarkAsync(8, 1)).Should().BeTrue();
        (await store.AssertHighWatermarkAsync(6, 2)).Should().BeFalse("index 落后不推进（单调不回退）");
        (await store.AssertHighWatermarkAsync(8, 1)).Should().BeFalse("同位不重复推进");
        (await store.AssertHighWatermarkAsync(9, 0)).Should().BeFalse("term 旧不推进（高水位记录口径）");
        (await store.AssertHighWatermarkAsync(9, 2)).Should().BeTrue("同任期前沿增长 / 更高任期推进");
        store.LastLogIndex.Should().Be(9);
        store.PersistedIndex.Should().Be(9, "断言推进即持久化完成（内存版无 fsync 窗口）");

        await store.WriteTermAndVoteAsync(2, NodeId.NewRandom());
        store.Term.Should().Be(2, "选举契约完整（term/vote 持久化面）");

        var actAppend = async () => await store.AppendAsync(0, Array.Empty<(long, byte, ReadOnlyMemory<byte>)>());
        await actAppend.Should().ThrowAsync<NotSupportedException>("无日志体——诚实拒绝");
        var actGet = () => store.TryGetEntry(1, out _, out _, out _);
        actGet.Should().Throw<NotSupportedException>("无日志体——诚实拒绝");
    }
}
