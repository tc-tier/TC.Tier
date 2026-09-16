using System.Diagnostics;

namespace TC.Tier.Core.Tests.Execution;

/// <summary>
/// DeadlineRegistry 契约测试（零 TimerQueue 节拍原语——设计稿
/// docs/design/tc-tier-net-timerqueue-free-tick-pacer-design.md）：
/// 到期回调精度 / 早移有界晚醒（封顶拉取）/ 立即到期 / 退订幂等与停发 / 回调异常隔离 /
/// deadline 计算异常隔离 / 并发订阅退订 / 多分片正确性 / Dispose fail-fast。
/// <para>★ 时序断言全部留宽（CI 友好）：封顶 100ms + 调度抖动余量。</para>
/// </summary>
public class DeadlineRegistryTests
{
    private static readonly TimeSpan WakeSlack = TimeSpan.FromMilliseconds(450);

    private sealed class Owner
    {
        private long _deadlineTicks = long.MaxValue;
        public int WakeCount;

        public void SetDeadline(TimeSpan fromNow)
            => Interlocked.Exchange(ref _deadlineTicks, Environment.TickCount64 + (long)fromNow.TotalMilliseconds);

        public void SetDeadlineTicks(long ticks) => Interlocked.Exchange(ref _deadlineTicks, ticks);

        public long DeadlineTicks() => Interlocked.Read(ref _deadlineTicks);

        public Action Wake => () => Interlocked.Increment(ref WakeCount);

        public IDisposable Subscribe(DeadlineRegistry registry) => registry.Subscribe(DeadlineTicks, Wake);
    }

    /// <summary>到期回调：deadline = 未来 250ms → 唤醒在 [250ms, 250ms+封顶+余量] 内到达。</summary>
    [Fact]
    public async Task DeadlineReached_WakesOnTime()
    {
        using var registry = new DeadlineRegistry();
        var owner = new Owner();
        owner.SetDeadline(TimeSpan.FromMilliseconds(250));

        var sw = Stopwatch.StartNew();
        using var sub = owner.Subscribe(registry);
        await WaitForAsync(() => owner.WakeCount >= 1, TimeSpan.FromSeconds(3));

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200), "不得早于 deadline（±抖动余量）");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250) + WakeSlack, "到期后唤醒 ≤ 封顶+余量");
    }

    /// <summary>立即到期：deadline 已过 → 快速唤醒（remaining ≤ 0 分支 + 防热转节流）。</summary>
    [Fact]
    public async Task DeadlineInPast_WakesImmediately()
    {
        using var registry = new DeadlineRegistry();
        var owner = new Owner();
        owner.SetDeadlineTicks(Environment.TickCount64 - 100);

        using var sub = owner.Subscribe(registry);
        await WaitForAsync(() => owner.WakeCount >= 1, TimeSpan.FromSeconds(1));
    }

    /// <summary>早移有界晚醒（拉取模型核心契约）：deadline 从远移近，唤醒 ≤ 移近后封顶+余量。</summary>
    [Fact]
    public async Task DeadlineMovedEarlier_WakesWithinCap()
    {
        using var registry = new DeadlineRegistry();
        var owner = new Owner();
        owner.SetDeadline(TimeSpan.FromSeconds(30));   // 远——先睡

        using var sub = owner.Subscribe(registry);
        await Task.Delay(200);                        // 让节拍线程进入长睡眠
        var sw = Stopwatch.StartNew();
        owner.SetDeadline(TimeSpan.FromMilliseconds(50));   // 早移——拉取模型下轮醒来即见

        await WaitForAsync(() => owner.WakeCount >= 1, TimeSpan.FromSeconds(2));
        sw.Elapsed.Should().BeLessThan(WakeSlack + TimeSpan.FromMilliseconds(100), "早移后唤醒 ≤ 封顶+余量（拉取有界）");
    }

    /// <summary>退订：幂等 + 停发（残余快照回调至多一个封顶窗，之后不再增长）。</summary>
    [Fact]
    public async Task Unsubscribe_Idempotent_StopsWake()
    {
        using var registry = new DeadlineRegistry();
        var owner = new Owner();
        owner.SetDeadline(TimeSpan.FromMilliseconds(50));

        var sub = owner.Subscribe(registry);
        await WaitForAsync(() => owner.WakeCount >= 1, TimeSpan.FromSeconds(1));

        sub.Dispose();
        sub.Dispose();   // 幂等

        var count = Volatile.Read(ref owner.WakeCount);
        await Task.Delay(400);   // > 2× 封顶——残余快照回调已过
        Volatile.Read(ref owner.WakeCount).Should().Be(count, "退订后不再唤醒");
    }

    /// <summary>唤醒回调异常隔离：单条抛不杀节拍线程——同注册表其他条目照常唤醒。</summary>
    [Fact]
    public async Task WakeThrows_Isolated_OtherEntriesStillWake()
    {
        using var registry = new DeadlineRegistry();
        var bad = new Owner();
        var good = new Owner();
        bad.SetDeadline(TimeSpan.FromMilliseconds(80));
        good.SetDeadline(TimeSpan.FromMilliseconds(80));

        using var badSub = registry.Subscribe(bad.DeadlineTicks, () => throw new InvalidOperationException("唤醒回调爆炸"));
        using var goodSub = good.Subscribe(registry);

        await WaitForAsync(() => good.WakeCount >= 1, TimeSpan.FromSeconds(2));
    }

    /// <summary>deadline 计算异常隔离：抛的条目跳过——其余条目照常。</summary>
    [Fact]
    public async Task DeadlineThrows_Isolated_OthersStillWake()
    {
        using var registry = new DeadlineRegistry();
        var good = new Owner();
        good.SetDeadline(TimeSpan.FromMilliseconds(80));

        using var badSub = registry.Subscribe(() => throw new InvalidOperationException("deadline 计算爆炸"), () => { });
        using var goodSub = good.Subscribe(registry);

        await WaitForAsync(() => good.WakeCount >= 1, TimeSpan.FromSeconds(2));
    }

    /// <summary>并发订阅/退订：百级条目交错启停——无异常、EntryCount 收敛一致。</summary>
    [Fact]
    public async Task ConcurrentSubscribeUnsubscribe_Safe()
    {
        using var registry = new DeadlineRegistry();
        var owners = Enumerable.Range(0, 40).Select(_ => new Owner()).ToArray();
        foreach (var o in owners) o.SetDeadline(TimeSpan.FromMilliseconds(200));

        var subs = new IDisposable[owners.Length];
        Parallel.For(0, owners.Length, i => subs[i] = owners[i].Subscribe(registry));
        await WaitForAsync(() => owners.Count(o => o.WakeCount >= 1) >= 30, TimeSpan.FromSeconds(3));

        Parallel.For(0, owners.Length, i => subs[i].Dispose());
        await Task.Delay(300);

        registry.EntryCount.Should().Be(0, "全部退订后条目清零");
    }

    /// <summary>多分片：shardCount=4——各分片独立节拍线程，全部条目照常唤醒。</summary>
    [Fact]
    public async Task ShardedRegistry_AllEntriesWake()
    {
        using var registry = new DeadlineRegistry(shardCount: 4);
        var owners = Enumerable.Range(0, 8).Select(_ => new Owner()).ToArray();
        foreach (var o in owners) o.SetDeadline(TimeSpan.FromMilliseconds(100));

        var subs = owners.Select(o => o.Subscribe(registry)).ToArray();
        await WaitForAsync(() => owners.All(o => o.WakeCount >= 1), TimeSpan.FromSeconds(3));
        registry.EntryCount.Should().Be(owners.Length);
        foreach (var s in subs) s.Dispose();
    }

    /// <summary>Dispose fail-fast：释放后订阅抛 ObjectDisposedException。</summary>
    [Fact]
    public void SubscribeAfterDispose_Throws()
    {
        var registry = new DeadlineRegistry();
        registry.Dispose();
        var act = () => registry.Subscribe(() => 0L, () => { });
        act.Should().Throw<ObjectDisposedException>();
    }

    /// <summary>Dispose 有界退出：在途唤醒中的注册表同步释放不挂。</summary>
    [Fact]
    public async Task DisposeWithActiveEntries_ExitsBounded()
    {
        var registry = new DeadlineRegistry();
        var owner = new Owner();
        owner.SetDeadline(TimeSpan.FromMilliseconds(50));
        using var sub = owner.Subscribe(registry);
        await WaitForAsync(() => owner.WakeCount >= 1, TimeSpan.FromSeconds(1));

        var sw = Stopwatch.StartNew();
        registry.Dispose();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "同步释放有界退出");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(20);
        }
    }
}
