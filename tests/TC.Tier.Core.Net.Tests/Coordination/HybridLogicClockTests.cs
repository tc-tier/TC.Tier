using FluentAssertions;
using TC.Tier.Core.Net.Coordination;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Coordination;

/// <summary>
/// HLC 混合逻辑时钟（二期-G1 NETGAP-024——验证矩阵 G1 行"跨分区时间戳单调"）：
/// 本地 Tick 单调、墙钟回拨安全（逻辑域吸收）、分区重连因果吸收（远端领先即跟钟）、
/// 并发 Tick 单调、严格序 OccursBefore。
/// </summary>
public class HybridLogicClockTests
{
    /// <summary>G1：本地 Tick 严格单调——1000 次连续推进无回退。</summary>
    [Fact]
    public void Tick_StrictlyMonotonic()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clock = new HybridLogicClock(() => now);
        var previous = clock.Tick();

        for (var i = 0; i < 1000; i++)
        {
            var next = clock.Tick();
            HlcTimestamp.OccursBefore(previous, next).Should().BeTrue($"严格递增：{previous} → {next}");
            previous = next;
        }
    }

    /// <summary>G1：墙钟回拨——物理不回退，逻辑域承载单调性（回拨安全）。</summary>
    [Fact]
    public void Tick_WallClockBackward_PhysicalNeverRegresses()
    {
        long now = 1000;
        var clock = new HybridLogicClock(() => now);
        var first = clock.Tick();
        first.Physical.Should().Be(1000);

        now = 500;   // ★ 墙钟大幅回拨
        var after = clock.Tick();
        after.Physical.Should().Be(1000, "物理不回退——回拨由逻辑域吸收");
        after.Logical.Should().Be(first.Logical + 1, "逻辑域推进");

        var after2 = clock.Tick();
        HlcTimestamp.OccursBefore(after, after2).Should().BeTrue("继续严格递增");
    }

    /// <summary>G1：跨分区重连因果吸收——远端领先时间戳 → 本端物理跳进吸收，本地 Tick 永不回退。</summary>
    [Fact]
    public void Receive_RemoteAhead_AbsorbsWithoutRegression()
    {
        var now = 1000L;
        var clock = new HybridLogicClock(() => now);
        var local = clock.Tick();

        // 分区期间远端跑到物理 5000（同毫秒内已 3 个事件）
        var remoteStamp = new HlcTimestamp(5000, 3);

        // 重连——吸收远端
        var absorbed = clock.Receive(remoteStamp);
        absorbed.Physical.Should().Be(5000, "因果吸收——物理跳进到远端水位");
        absorbed.Logical.Should().Be(4, "跟钟到远端物理——逻辑越过远端计数（Kulkarni h.j > m，#417）");

        // 后续本地 Tick 继续单调（不回退到旧物理）
        var next = clock.Tick();
        HlcTimestamp.OccursBefore(absorbed, next).Should().BeTrue("吸收后本地推进保持单调");

        // 迟到的旧远端时间戳——不造成回退
        clock.Receive(new HlcTimestamp(1000, 2));
        clock.Current.Physical.Should().BeGreaterThanOrEqualTo(5000);
    }

    /// <summary>G1 补（#417）：远端物理领先且同毫秒多事件——Receive 结果必须严格晚于远端
    /// （跟钟分支：logical = remote.Logical + 1，因果不倒置）。</summary>
    [Fact]
    public void Receive_RemoteAheadWithLogicalCount_ResultStrictlyAfterRemote()
    {
        var now = 100L;
        var clock = new HybridLogicClock(() => now);
        clock.Tick();

        var remote = new HlcTimestamp(200, 3);   // 远端同毫秒已 3 事件
        var received = clock.Receive(remote);

        received.Physical.Should().Be(200);
        received.Logical.Should().Be(4, "跟钟到远端物理——逻辑必须越过远端计数");
        HlcTimestamp.OccursBefore(remote, received).Should().BeTrue(
            "接收不变式：结果严格晚于被吸收的远端时间戳（#417 回归门）");
    }

    /// <summary>G1 补（#417 对偶分支）：墙钟领先两者（新物理严格大于远端物理）——逻辑归零安全。</summary>
    [Fact]
    public void Receive_WallClockLeadsBoth_LogicalZeroSafe()
    {
        var now = 300L;   // 墙钟 300 > 远端物理 200
        var clock = new HybridLogicClock(() => now);

        var remote = new HlcTimestamp(200, 3);
        var received = clock.Receive(remote);

        received.Physical.Should().Be(300, "墙钟领先——物理取墙钟");
        received.Logical.Should().Be(0, "新物理严格大于远端物理——逻辑域归零安全");
        received.CompareTo(remote).Should().BeGreaterThan(0, "物理维度已严格晚于远端");
    }

    /// <summary>G1：并发 Tick——多线程推进严格单调（无重复无回退）。</summary>
    [Fact]
    public async Task Tick_Concurrent_StrictlyMonotonic()
    {
        var clock = new HybridLogicClock(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var seen = new System.Collections.Concurrent.ConcurrentBag<HlcTimestamp>();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) seen.Add(clock.Tick());
        }));
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        seen.Count.Should().Be(800);
        var ordered = seen.OrderBy(t => t.Physical).ThenBy(t => t.Logical).ToList();
        ordered.Distinct().Count().Should().Be(ordered.Count, "无重复时间戳（并发安全）");
        HlcTimestamp.OccursBefore(ordered.First(), ordered.Last()).Should().BeTrue("全序成立");
    }
}
