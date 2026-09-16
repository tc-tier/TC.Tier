using TC.Tier.Core.Execution;
using TC.Tier.Core.Testing;

namespace TC.Tier.Core.Tests.Testing;

/// <summary>
/// FakeTimeProvider 单元测试——双钟模型（单调快进 + 墙钟跳变/漂移）与假钟定时器驱动语义。
/// <para>对应故障注入面补全设计 §1.1/§1.3：快进确定性、墙钟跳变不污染单调钟、周期定时器连发。</para>
/// </summary>
public sealed class FakeTimeProviderTests
{
    [Fact]
    public void Advance_MovesMonotonicAndWallTogether()
    {
        var clock = new FakeTimeProvider();
        clock.GetTimestamp().Should().Be(0, "单调钟从 0 起");
        clock.GetUtcNow().Should().Be(FakeTimeProvider.DefaultStart, "墙钟从固定起点起");

        clock.Advance(TimeSpan.FromSeconds(5));

        clock.GetTimestamp().Should().Be(5000, "单调戳 ms 域（与 TickCount64 同域）");
        clock.GetUtcNow().Should().Be(FakeTimeProvider.DefaultStart + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SetWallClock_JumpsWall_MonotonicUntouched()
    {
        var clock = new FakeTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(10));
        var monoBefore = clock.GetTimestamp();

        var jumped = new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
        clock.SetWallClock(jumped);

        clock.GetUtcNow().Should().Be(jumped, "墙钟跳变（NTP 校时形态）");
        clock.GetTimestamp().Should().Be(monoBefore, "单调钟不受跳变污染");
    }

    [Fact]
    public void SetDrift_ScalesWallOnly_MonotonicConstant()
    {
        var clock = new FakeTimeProvider();
        clock.SetDrift(2.0);

        clock.Advance(TimeSpan.FromSeconds(1));

        clock.GetTimestamp().Should().Be(1000, "单调钟按真实增量推进");
        (clock.GetUtcNow() - FakeTimeProvider.DefaultStart).Should().Be(TimeSpan.FromSeconds(2),
            "走速 2 的墙钟走 2 秒（漂移可见）");
    }

    [Fact]
    public void CreateTimer_SingleShot_FiresOnAdvance()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        fired.Should().Be(0, "未到期不触发");
        clock.Advance(TimeSpan.FromMilliseconds(50));
        fired.Should().Be(1, "到期触发");
        clock.Advance(TimeSpan.FromSeconds(10));
        fired.Should().Be(1, "单次定时器只触发一次");
    }

    [Fact]
    public void CreateTimer_Periodic_FiresRepeatedlyAcrossLargeAdvance()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

        clock.Advance(TimeSpan.FromSeconds(1));

        fired.Should().Be(10, "大跨度快进内周期定时器按周期连发");
    }

    [Fact]
    public void CreateTimer_ZeroDue_FiresOnFlush()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        fired.Should().Be(0, "创建即触发不是假钟语义——由 Advance 冲刷");
        clock.Advance(TimeSpan.Zero);
        fired.Should().Be(1, "零跨度 Advance = 冲刷已到期定时器");
    }

    [Fact]
    public void CreateTimer_Dispose_StopsFiring()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;
        var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
        timer.Dispose();

        clock.Advance(TimeSpan.FromSeconds(1));
        fired.Should().Be(0, "已释放定时器不触发");
    }

    [Fact]
    public async Task Delay_FakeClock_CompletesOnAdvance_NoRealSleep()
    {
        var clock = new FakeTimeProvider();
        var task = clock.Delay(TimeSpan.FromSeconds(30));
        task.IsCompleted.Should().BeFalse("假钟下延迟未到期");

        clock.Advance(TimeSpan.FromSeconds(30));

        await task;   // 快进后完成且不抛（零真实睡等）
        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Delay_FakeClock_CancellationDuringHold()
    {
        var clock = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var task = clock.Delay(TimeSpan.FromSeconds(30), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => task);
    }

    [Fact]
    public void Delay_SystemPassthrough_NegativeCompletesImmediately()
    {
        // System 快路径直通 Task.Delay（非哨兵负值 = 立即完成）——缺省零变化语义的探针。
        // （-1ms 是 Timeout.InfiniteTimeSpan 哨兵 = 永不完成，不属本用例。）
        var task = TimeProvider.System.Delay(TimeSpan.FromMilliseconds(-5));
        task.IsCompleted.Should().BeTrue("非哨兵负延迟立即完成（Task.Delay 语义）");
    }

    [Fact]
    public async Task Delay_InfiniteTimeSpan_NeverCompletesOnlyCancellable()
    {
        using var cts = new CancellationTokenSource(50);
        var act = () => TimeProvider.System.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        await act.Should().ThrowAsync<TaskCanceledException>("无限延迟仅取消可解（Task.Delay(-1) 语义）");
    }
}
