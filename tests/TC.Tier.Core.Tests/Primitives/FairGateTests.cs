using System.Diagnostics;
using TC.Tier.Core.Primitives;
using Xunit;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// FairGate（公平门）原语契约测试。
/// <para>★ 背景（，自 SegmentTable.AcquireExtent 手搓公平门下沉）：双写者长持锁窗口下，
///   零间隙复占者永远插队 + 被唤醒者无先手持续失手——8 并发写者 3~4 个超时。根治协议两件套：
///   ①有等待者时新到者不走快路径 ②唤醒者让渡 5ms 先手。本测试族锁定这两条契约 + 计数配对。</para>
/// </summary>
public class FairGateTests
{
    // ════════════════════════════════════════════════════════════
    //  基础状态与零开销路径
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Initial_NoWaiters_WakeReturnsImmediately()
    {
        var gate = new FairGate();
        Assert.False(gate.HasWaiters);

        var sw = Stopwatch.StartNew();
        gate.Wake();   // 无等待者——零阻塞直返
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"无等待者的 Wake 应立即返回（实测 {sw.ElapsedMilliseconds}ms）");
    }

    // ════════════════════════════════════════════════════════════
    //  等待者计数配对（获取/释放协议）
    // ════════════════════════════════════════════════════════════

    /// <summary>★ 配对契约：TryAcquireSlow 无论成功（立即返回）或失败（park 超时返回），
    /// 等待者登记必须配对撤销——残留的 HasWaiters=true 会永久把所有后来者逼进慢路径。</summary>
    [Fact]
    public void TryAcquireSlow_BothPaths_WaitersPaired()
    {
        var gate = new FairGate();

        // 成功路径：门锁内占用成功，立即返回 true，计数回落
        Assert.True(gate.TryAcquireSlow(() => true));
        Assert.False(gate.HasWaiters);

        // 失败路径：park 满 50ms 超时返回 false，计数回落
        var sw = Stopwatch.StartNew();
        Assert.False(gate.TryAcquireSlow(() => false));
        sw.Stop();
        Assert.False(gate.HasWaiters);
        Assert.True(sw.ElapsedMilliseconds >= 40,
            $"失败路径应 park 等待（50ms 超时兜底），实测 {sw.ElapsedMilliseconds}ms——未 park 即返回说明协议破坏");
    }

    // ════════════════════════════════════════════════════════════
    //  唤醒协议
    // ════════════════════════════════════════════════════════════

    /// <summary>★ 唤醒契约：park 中的等待者能被 Wake 唤醒返回（活性）。
    /// <para>★ 轮询 Wake（防丢失唤醒竞态的轮询唤醒手法）：Wake 的丢失唤醒窗口是协议真实存在
    ///   的——TryAcquireSlow 在"登记 → 拿门锁 → park"之间 PulseAll 可能打进空等待集，这正是
    ///   ParkTimeoutMs=50 兜底存在的原因（7a9685aa 设计意图：兜底防丢唤醒，非消灭它）。故本测试
    ///   断言"轮询 Wake 下等待者限时返回"，<b>不</b>用绝对 ms 阈值断言"未走兜底"（调度延迟与兜底
    ///   时延分布重叠，必假红）；唤醒时效由下方无饿死压测的定额限时端到端覆盖。</para></summary>
    [Fact]
    public void Wake_WakesParkedWaiter_ReturnsWithinBound()
    {
        var gate = new FairGate();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = Task.Run(() =>
        {
            gate.TryAcquireSlow(() => false);
            returned.TrySetResult();
        });

        // 轮询 Wake 直到唤醒（防丢失唤醒竞态：单次 Wake 可能早于 waiter park 打进空等待集）
        var sw = Stopwatch.StartNew();
        while (!returned.Task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            gate.Wake();
            Thread.Sleep(10);
        }

        Assert.True(returned.Task.Wait(2000), "park 中的等待者应被 Wake 唤醒返回");
        Assert.True(waiter.Wait(2000));
        Assert.False(gate.HasWaiters);
    }

    // ════════════════════════════════════════════════════════════
    //  无饿死压测（★ AcquireExtent 饥饿形态的复刻）
    // ════════════════════════════════════════════════════════════

    /// <summary>★ 无饿死契约：8 线程按 AcquireExtent 同形协议（HasWaiters 让位 + TryAcquireSlow +
    /// 释放后 Wake）竞争单槽资源——限时内每线程必须完成定额获取（无插队饿死），且槽互斥不破。
    /// 无公平门协议下（纯 CAS 复占），被挤出者持续失手 → 超时；此测试是根治验证。</summary>
    [Fact]
    public async Task StarvationFreedom_8Threads_SingleSlot_AllMeetQuota()
    {
        var gate = new FairGate();
        var holder = -1;   // -1 = 空闲，否则 = 线程号
        var acquiredCount = new int[8];
        var concurrency = 0;
        var maxConcurrency = 0;

        bool TryOccupy(int me)
        {
            if (Interlocked.CompareExchange(ref holder, me, -1) != -1) return false;
            var c = Interlocked.Increment(ref concurrency);
            int cur, seen;
            do
            {
                seen = cur = Volatile.Read(ref maxConcurrency);
                if (c <= cur) break;
            } while ((cur = Interlocked.CompareExchange(ref maxConcurrency, c, seen)) != seen);
            return true;
        }
        void Release(int me)
        {
            Interlocked.Decrement(ref concurrency);
            Volatile.Write(ref holder, -1);
        }

        const int quota = 50;
        var tasks = Enumerable.Range(0, 8).Select(me => Task.Run(() =>
        {
            while (Volatile.Read(ref acquiredCount[me]) < quota)
            {
                var got = gate.HasWaiters
                    ? gate.TryAcquireSlow(() => TryOccupy(me))   // 有等待者——让位走慢路径
                    : TryOccupy(me);                             // 无等待者——快路径直占
                if (!got)
                {
                    Thread.Yield();
                    continue;
                }
                Volatile.Write(ref acquiredCount[me], acquiredCount[me] + 1);
                Thread.Sleep(1);   // ★ 长持锁窗口（原事故形态：双写者长持 → 复占者插队 → 挤出者饿死）
                Release(me);
                gate.Wake();      // 资源已空闲再唤醒（唤醒过早 = 双检必败）
            }
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));   // 超时 = 有线程未达定额（饿死）

        Assert.Equal(1, Volatile.Read(ref maxConcurrency));   // 槽互斥全程不破
    }
}
