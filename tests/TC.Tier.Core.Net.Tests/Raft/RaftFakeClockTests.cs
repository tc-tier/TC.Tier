using FluentAssertions;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Testing;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// raft 时钟缝（故障注入面补全设计 件一 + §1.3 矩阵）——假钟驱动的选举计时：
/// 快进 = 选举超时确定性触发（零真实睡等）；墙钟跳变 = 单调钟守卫（选举窗不误触发）。
/// <para>★ 节拍唤醒是真实线程（同钟注册表，封顶 100ms 扫描粒度）——快进后到Role收敛
///   以真实等待上限观察，语义由假钟决定。</para>
/// </summary>
public sealed class RaftFakeClockTests
{
    [Fact]
    public async Task ElectionTimeout_AdvanceDriven_SingleLeaderDeterministic()
    {
        var clock = new FakeTimeProvider();
        await using var fx = await RaftEngineTests.CreateClusterAsync(3, seed: 41,
            optionsFactory: _ => RaftOptions.Default
                .WithClock(clock)
                .WithElectionTimeout(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)));

        // 不快进——选举 deadline 悬在假钟未来：真实 300ms 内无选举（假钟停走 = 共识停走）
        await Task.Delay(300);
        fx.Nodes.Count(n => n.Raft.IsLeader).Should().Be(0, "假钟未推进——选举窗未到期（停走行：时钟冻结不推进）");

        // 持续小步进驱动（模拟时间流逝——调度饥饿鲁棒契约下大步进（> 最小窗）= 被饿判定不触发
        // 选举；驱动须小步进（< 最小窗）让循环持续观测——累计推进过窗即正常触发；首轮同窗选票
        // 可能分裂，重掷后的选举窗随推进逐轮到期）
        await RaftEngineTests.WaitForAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            return fx.Nodes.Count(n => n.Raft.IsLeader) == 1;
        }, TimeSpan.FromSeconds(10));
        fx.Nodes.Count(n => n.Raft.IsLeader).Should().Be(1, "快进触发选举——恰好一 leader");
    }

    [Fact]
    public async Task WallClockJump_DoesNotTriggerElection_MonoGuardHolds()
    {
        var clock = new FakeTimeProvider();
        await using var fx = await RaftEngineTests.CreateClusterAsync(3, seed: 42,
            optionsFactory: _ => RaftOptions.Default
                .WithClock(clock)
                .WithElectionTimeout(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)));

        // 小步进驱动收敛 leader（同 ElectionTimeout_AdvanceDriven——大步进 = 被饿判定不触发选举）
        await RaftEngineTests.WaitForAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            return fx.Nodes.Count(n => n.Raft.IsLeader) == 1;
        }, TimeSpan.FromSeconds(10));
        var leader = fx.Nodes.Single(n => n.Raft.IsLeader);
        var termBefore = leader.Raft.CurrentTerm;

        // 墙钟跳变（NTP 校时形态）+ 小幅单调推进——心跳照常续约，选举窗不误触发（§1.3 倒流行单调列）
        clock.SetWallClock(FakeTimeProvider.DefaultStart.AddMinutes(30));
        clock.Advance(TimeSpan.FromMilliseconds(100));

        await Task.Delay(300);
        fx.Nodes.Count(n => n.Raft.IsLeader).Should().Be(1, "墙钟跳变不换届——单调钟守卫");
        leader.Raft.CurrentTerm.Should().Be(termBefore, "任期未推进（无误选举）");
    }

    /// <summary>
    /// 调度饥饿鲁棒（#504 挂死族根因——docs/design/raft-election-timing-scheduling-starvation-design.md）：
    /// 选举超时只对「观测时间」计数。大步进（> 最小选举窗）模拟泵线程被饿后首次恢复执行——墙钟超期
    /// 但节点全程未观测：不得触发选举（按 now 重掷截止）；随后持续观测下 leader 失联仍正常换届（活性保持）。
    /// </summary>
    [Fact]
    public async Task ElectionTimeout_StarvationAdvance_NoDisruption_ThenRecovers()
    {
        var clock = new FakeTimeProvider();
        await using var fx = await RaftEngineTests.CreateClusterAsync(3, seed: 43,
            optionsFactory: _ => RaftOptions.Default
                .WithClock(clock)
                .WithElectionTimeout(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)));

        // 小步进驱动收敛 leader（同 ElectionTimeout_AdvanceDriven——大步进 = 被饿判定不触发选举）
        await RaftEngineTests.WaitForAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            return fx.Nodes.Count(n => n.Raft.IsLeader) == 1;
        }, TimeSpan.FromSeconds(10));
        var leader = fx.Nodes.Single(n => n.Raft.IsLeader);
        var followers = fx.Nodes.Where(n => !n.Raft.IsLeader).ToArray();
        await RaftEngineTests.WaitForAsync(() => followers.All(f => f.Raft.LeaderId == leader.Id));
        var termBefore = followers[0].Raft.CurrentTerm;

        // 隔离 leader 心跳（follower 收不到续约——被饿模拟的纯净场景：心跳/预票均不可达）
        fx.Hub.Faults.Partition([leader.Id], followers.Select(f => f.Id));

        // 大步进 > 最小选举窗（150ms）：泵线程被饿后首次恢复执行——墙钟已超期但节点全程未观测
        clock.Advance(TimeSpan.FromMilliseconds(300));
        // 真实等待：注册表节拍线程（封顶 100ms 扫描粒度）投 tick → 循环消费并完成判定/重掷
        await RaftEngineTests.WaitForAsync(() => followers.All(f => f.Raft.DiagnoseLoop().LoopLagMs < 100));

        followers.Should().OnlyContain(f => f.Raft.Role == RaftRole.Follower,
            "被饿大步进不触发选举——重掷截止（PreCandidate/抬 term 均不发生）");
        followers.All(f => f.Raft.CurrentTerm == termBefore).Should().BeTrue("任期未进——无扰主抬 term");

        // 活性保持：持续观测（小步进 + 真实等待让循环消化 tick——每步 gap 小，不触发被饿判定）→
        // leader 失联仍换届收敛（分区后 PreVote 在位否决在真实 ~ElectionTimeoutMax 后解除）
        for (var i = 0; i < 16 && fx.Nodes.Count(n => n.Raft.IsLeader) == 1; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(80);
        }
        await RaftEngineTests.WaitForAsync(() => followers.Count(f => f.Raft.IsLeader) == 1, TimeSpan.FromSeconds(15));
        followers.Single(f => f.Raft.IsLeader).Raft.CurrentTerm.Should().BeGreaterThan(termBefore, "真选举抬任期");
    }

    /// <summary>
    /// 正常负载零变化（设计 §4.4——无调度饥饿时与现实现行为一致）：未分区大步进——leader 心跳
    /// 直排处理照常续约重置 follower 选举截止，follower 不误选举、无换届（自适应在无饥饿时零副作用）。
    /// </summary>
    [Fact]
    public async Task ElectionTimeout_StarvationAdvance_HeartbeatRenews_NoDisruption()
    {
        var clock = new FakeTimeProvider();
        await using var fx = await RaftEngineTests.CreateClusterAsync(3, seed: 44,
            optionsFactory: _ => RaftOptions.Default
                .WithClock(clock)
                .WithElectionTimeout(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)));

        // 小步进驱动收敛 leader（同 ElectionTimeout_AdvanceDriven——大步进 = 被饿判定不触发选举）
        await RaftEngineTests.WaitForAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            return fx.Nodes.Count(n => n.Raft.IsLeader) == 1;
        }, TimeSpan.FromSeconds(10));
        var leader = fx.Nodes.Single(n => n.Raft.IsLeader);
        var followers = fx.Nodes.Where(n => !n.Raft.IsLeader).ToArray();
        var termBefore = leader.Raft.CurrentTerm;

        // 大步进 > 最小选举窗（不分区）——被饿模拟；但 leader 心跳照常续约（直排处理重置选举截止）
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await Task.Delay(300);   // 真实等待：leader 补发心跳 + follower 直排续约收敛

        followers.Should().OnlyContain(f => f.Raft.Role == RaftRole.Follower, "心跳照常续约——follower 不误选举");
        followers.All(f => f.Raft.CurrentTerm == termBefore).Should().BeTrue("任期未进");
        fx.Nodes.Count(n => n.Raft.IsLeader).Should().Be(1, "leader 未换届");
        leader.Raft.CurrentTerm.Should().Be(termBefore, "任期未推进（无扰主抬 term）");
    }
}
