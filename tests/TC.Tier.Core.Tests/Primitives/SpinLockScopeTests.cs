using TC.Tier.Core.Primitives;
using Xunit;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// SpinLockScope 契约测试——using 块自动 Enter/Exit SpinLock（锁原语）。
/// 契约：Enter 持锁互斥；Dispose 释放（可再次 Enter）；异常路径 using 也释放。
/// ⚠️ 不测同线程递归 Enter（默认 SpinLock 无所有权跟踪，递归自旋死锁——契约禁止）；
/// 不测双 Dispose（实现无幂等守卫，Exit 两次未定义——调用方用 using 保证单次）。
/// </summary>
public class SpinLockScopeTests
{
    [Fact]
    public void EnterDispose_Cycle_LockReacquirable()
    {
        var spinLock = new SpinLock(false);
        using (SpinLockScope.Enter(ref spinLock))
        {
            // 持锁中
        }
        using (SpinLockScope.Enter(ref spinLock))   // Dispose 后可重入
        {
        }
    }

    [Fact]
    public void Hold_ExcludesOtherThread_UntilDisposed()
    {
        var spinLock = new SpinLock(false);
        var enteredByOther = 0;

        using (var scope = SpinLockScope.Enter(ref spinLock))
        {
            var t = Task.Run(() =>
            {
                using (SpinLockScope.Enter(ref spinLock))
                    Interlocked.Increment(ref enteredByOther);
            });
            Thread.Sleep(100);                        // 持锁期间他线程进不来（负向断言）
            Assert.Equal(0, Volatile.Read(ref enteredByOther));
            scope.Dispose();                          // 提前释放
            Assert.True(t.Wait(2000), "释放后他线程应能进入");
        }
    }

    [Fact]
    public void UsingBodyThrows_LockStillReleased()
    {
        var spinLock = new SpinLock(false);
        try
        {
            using (var scope = SpinLockScope.Enter(ref spinLock))
                throw new InvalidOperationException("临界区内异常");
        }
        catch (InvalidOperationException) { /* 预期 */ }

        // 异常后锁已释放：可再次进入（用超时任务证明不卡死）
        var t = Task.Run(() => { using var s = SpinLockScope.Enter(ref spinLock); });
        Assert.True(t.Wait(2000), "异常路径 using 应已释放锁");
    }

    [Fact]
    public async Task ConcurrentEnterExit_NoException_AllProgress()
    {
        var spinLock = new SpinLock(false);
        const int threads = 8, iters = 5000;
        using var gate = new ManualResetEventSlim(false);
        var counter = 0;

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            for (var i = 0; i < iters; i++)
                using (SpinLockScope.Enter(ref spinLock))
                    Interlocked.Increment(ref counter);
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        counter.Should().Be(threads * iters, "互斥下计数不丢不重");
    }
}
