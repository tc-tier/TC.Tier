using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// 优先 Leader 放置（二期-F7——DDR-F7 验收，偏好序 + 转让编排）：
/// ① 迁移——偏好序更靠前的存活 voter（已追平）被选中，leader 迁至偏好首；
/// ② 偏好已满足 = 零转让（不抖动）；③ 宕机自然跳过（追平判定）。
/// </summary>
public class LeaderPlacementTests
{
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }

    /// <summary>验收①+②：偏好 [B, A, C]——A 当选后迁移到 B；偏好满足后零抖动；B 宕跳过。</summary>
    [Fact]
    public async Task PreferredOrder_LeaderMigratesToFront_StaysUntilFailure()
    {
        var rig = await RaftEngineTests.CreateClusterAsync(3);
        await using var _ = rig;
        var leader = await RaftEngineTests.WaitStableLeaderAsync(rig);   // policy 必须装在现任 leader（leader 视角 matchIndex）
        var followers = rig.Nodes.Where(n => n.Id != leader.Id).ToArray();
        var (b, c) = (followers[0], followers[1]);
        var policy = new LeaderPlacementPolicy(leader.Raft, new[] { b.Id, leader.Id, c.Id }, TimeSpan.FromMilliseconds(200));

        // 等待 follower 追平（迁移前置：match == commit）
        await RaftEngineTests.WaitForAsync(() =>
        {
            var snap = leader.Raft.GetStateSnapshot();
            return snap.Members.TryGetMatch(b.Id, out var m) && m == snap.CommitIndex;
        }, TimeSpan.FromSeconds(10));

        policy.Start();
        await WaitForAsync(() => b.Raft.IsLeader, TimeSpan.FromSeconds(15));
        leader.Raft.IsLeader.Should().BeFalse("leader 迁移到偏好序更靠前的 B");

        // 偏好已满足（B 在位）——稳定期无抖动（不发生反向转让）
        var stableAt = DateTime.UtcNow;
        await WaitForAsync(() => DateTime.UtcNow - stableAt > TimeSpan.FromSeconds(2)
                                && b.Raft.IsLeader, TimeSpan.FromSeconds(5));
        b.Raft.IsLeader.Should().BeTrue("偏好已满足——leader 稳定在 B（零转让抖动）");

        // B 宕机 → 自然换届到 A/C（偏好序中 B 已死自然跳过）
        await b.Raft.DisposeAsync();
        await WaitForAsync(() => leader.Raft.IsLeader || c.Raft.IsLeader, TimeSpan.FromSeconds(15));
    }
}
