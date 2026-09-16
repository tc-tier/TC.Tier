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

        // 持续快进（模拟时间流逝——首轮同窗选票可能分裂，重掷后的选举窗随快进逐轮到期）
        await RaftEngineTests.WaitForAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
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

        await RaftEngineTests.WaitForAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
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
}
