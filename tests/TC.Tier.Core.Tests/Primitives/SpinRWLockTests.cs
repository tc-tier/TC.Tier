using System.Diagnostics;
using TC.Tier.Core.Primitives;
using Xunit;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// SpinRWLock（写偏向自旋读写锁）原语契约测试。
/// <para>★ 背景（事故，自 LockWord 承接）：AcquireShared 曾用 <b>OR 置位</b>而非 ADD 递增——
///   "读计数"实为固定位，第 2 个读者获取"成功"却不加计数、每人释放都 −1 → 下溢借位高位假"写者位"
///   → 全进程楔死数月，被误读为"压测不稳定"。该 bug <b>单线程两次获取+两次释放即可捕获</b>——
///   原语必须有契约测试，不能只靠集成测试兜底（集成层只表现为楔死/flaky，掩盖原语算术错误）。</para>
/// <para>★ 契约：读计数是 bits61..32 的<b>计数</b>（acquire=+1、release=−1）；bit63 排他；bit62 写等待；
///   下溢 = 无配对释放 → 绊线撤销 + 抛 InvalidOperationException。</para>
/// <para>★ 写偏向契约（重构新增）：等待中的写者（pending）挡住<b>新读者</b>——
///   写者最多等"在途读者"退出，不被持续读者流饿死（LockWord 读优先语义的根治）。</para>
/// </summary>
public class SpinRWLockTests
{
    // ════════════════════════════════════════════════════════════
    //  排他
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Exclusive_SequentialAcquireRelease_Balanced()
    {
        var l = new SpinRWLock();
        l.AcquireExclusive();
        l.ReleaseExclusive();
        l.AcquireExclusive();   // 释放后可重入
        l.ReleaseExclusive();
    }

    [Fact]
    public void Exclusive_BlocksConcurrentAcquire_UntilReleased()
    {
        var l = new SpinRWLock();
        l.AcquireExclusive();
        var acquired = 0;
        var t = Task.Run(() => { l.AcquireExclusive(); Interlocked.Increment(ref acquired); l.ReleaseExclusive(); });

        Thread.Sleep(100);   // 持锁期间另一方拿不到（负向断言：仍为 0）
        Assert.Equal(0, Volatile.Read(ref acquired));

        l.ReleaseExclusive();
        Assert.True(t.Wait(2000), "释放后等待方应能获取排他锁");
    }

    // ════════════════════════════════════════════════════════════
    //  共享计数（★ OR 置位 bug 的回归测试族——事故的直接产物）
    // ════════════════════════════════════════════════════════════

    /// <summary>★ OR-bug 单线程回归：两次获取必须计两次、两次释放平衡、第三次释放必须被绊线拦下。
    /// 旧 bug 下：第二次获取空转（计数仍 1）→ 第一次释放 1→0 → 第二次释放 0→−1 触发绊线 → 本测试红。</summary>
    [Fact]
    public void Shared_SequentialAcquireTwice_CountsTwo()
    {
        var l = new SpinRWLock();
        l.AcquireShared();
        l.AcquireShared();     // 第二次必须真递增（OR bug 下为空转）
        l.ReleaseShared();     // 2→1，不得触发下溢绊线
        l.ReleaseShared();     // 1→0 平衡
        Assert.Throws<InvalidOperationException>(() => l.ReleaseShared());   // 0→−1 必须被绊线拦下
    }

    /// <summary>★ OR-bug 并发重叠回归：N 个线程同时持共享锁（CountdownEvent 对齐），全部持有后统一释放——
    /// 计数必须恰为 N 且全部释放后归零（额外释放必被绊线拦下）。
    /// 旧 bug 下：N 个重叠获取只计 1 → 第二个释放即下溢 → 任务组失败 → 本测试红。</summary>
    [Fact]
    public async Task Shared_OverlappingHolders_AllCounted()
    {
        var l = new SpinRWLock();
        const int n = 8;
        using var allHeld = new CountdownEvent(n);
        using var releaseGate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(() =>
        {
            l.AcquireShared();
            allHeld.Signal();
            releaseGate.Wait();          // 全部同时持有
            l.ReleaseShared();
        })).ToArray();

        // 轮询等待全部持有者完成获取（满套并行负载下任务启动有池调度延迟——固定 2s Wait 会假红）
        Assert.True(SpinWait.SpinUntil(() => allHeld.CurrentCount == 0, 10000), "全部持有者应完成获取");
        releaseGate.Set();               // 统一释放
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        // 全部释放后计数归零：一次正常配对无事，额外一次必被绊线拦下
        l.AcquireShared();
        l.ReleaseShared();
        Assert.Throws<InvalidOperationException>(() => l.ReleaseShared());
    }

    [Fact]
    public async Task Shared_ConcurrentAcquireRelease_StressBalanced()
    {
        var l = new SpinRWLock();
        const int threads = 8, iters = 2000;
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            for (var i = 0; i < iters; i++)
            {
                l.AcquireShared();
                l.ReleaseShared();
            }
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        l.AcquireShared();
        l.ReleaseShared();
        Assert.Throws<InvalidOperationException>(() => l.ReleaseShared());
    }

    // ════════════════════════════════════════════════════════════
    //  共享与排他互斥
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void SharedBlocksExclusive_AndExclusiveBlocksShared()
    {
        var l = new SpinRWLock();

        // 共享持有 → 排他进不来
        l.AcquireShared();
        var gotExclusive = 0;
        var t1 = Task.Run(() => { l.AcquireExclusive(); Interlocked.Increment(ref gotExclusive); l.ReleaseExclusive(); });
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref gotExclusive));
        l.ReleaseShared();
        Assert.True(t1.Wait(2000));

        // 排他持有 → 共享进不来
        l.AcquireExclusive();
        var gotShared = 0;
        var t2 = Task.Run(() => { l.AcquireShared(); Interlocked.Increment(ref gotShared); l.ReleaseShared(); });
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref gotShared));
        l.ReleaseExclusive();
        Assert.True(t2.Wait(2000));
    }

    // ════════════════════════════════════════════════════════════
    //  写偏向契约（★ 重构新增——LockWord 读优先写饥饿的根治验证）
    // ════════════════════════════════════════════════════════════

    // ★ 刻意不设"8 读者紧循环锤击下写者限时落地"类压测：其契约（新读者被 pending 挡住、写者落地）
    //   已由下方两个确定性测试完全覆盖——后到读者让位测试在读优先语义下必红（判别力无损）；
    //   而紧循环锤击会在线程池里制造满载饥饿，xUnit 并行 collection 下连带其他测试假红
    //   （满套验证实录：SpinLockScope/MaintenanceGate 等预存测试被殃及）。
    //   原语级判别用确定性序号断言，负载形态留给 FairGate 的定额限时压测。

    /// <summary>★ 写偏向顺序契约：后到读者必须让位等待中的写者——R1 在途、W 等待（pending）、
    /// R2 后到：R1 释放后 W 必须先于 R2 获取（序号断言）。写偏向下此序由协议保证（R2 被
    /// W 的 pending 挡住，只有 W 释放清 pending 后 R2 才能进）。</summary>
    [Fact]
    public void WriteBias_LatecomerReader_DefersToWaitingWriter()
    {
        var l = new SpinRWLock();
        var seq = 0;
        var wOrder = 0;
        var r2Order = 0;
        using var wStarted = new ManualResetEventSlim(false);

        l.AcquireShared();   // R1 = 本线程，在途持有

        // W：信号后立刻进场（信号→置 pending 之间只有纳秒级指令间隙）
        var w = Task.Run(() =>
        {
            wStarted.Set();
            l.AcquireExclusive();
            wOrder = Interlocked.Increment(ref seq);
            l.ReleaseExclusive();
        });
        Assert.True(SpinWait.SpinUntil(() => wStarted.IsSet, 2000), "写者任务应启动");
        Thread.Sleep(100);   // W 已置 pending 进入等待（获取不了——R1 在途）

        // R2：后到读者，应被 W 的 pending 挡住
        var r2 = Task.Run(() =>
        {
            l.AcquireShared();
            r2Order = Interlocked.Increment(ref seq);
            l.ReleaseShared();
        });
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref seq));   // 负向断言：W 与 R2 都进不去

        l.ReleaseShared();   // R1 退出——在途读者清零，W 应先于 R2 落地

        Assert.True(Task.WhenAll(w, r2).Wait(2000), "R1 释放后 W 与 R2 都应完成");
        Assert.True(wOrder < r2Order,
            $"写偏向违约：等待中的写者应先于后到读者获取（W={wOrder}, R2={r2Order}）");
    }

    /// <summary>★ pending 卫生契约：写者释放必须连 pending 一并清除——否则残留门闩永久挡新读者。
    /// 释放后读者立即可进（多次进出）、写者可再进（无残留写位）。</summary>
    [Fact]
    public void WriteBias_PendingClearedAfterRelease_ReadersFlowAgain()
    {
        var l = new SpinRWLock();
        l.AcquireExclusive();
        l.ReleaseExclusive();

        // 读者必须立即可进（pending 残留会永久阻塞——用限时任务防测试挂死）
        var ok = Task.Run(() => { l.AcquireShared(); l.ReleaseShared(); });
        Assert.True(ok.Wait(2000), "释放后读者必须能立即进入——pending 残留会永久挡读者");

        // 多轮读写交替无残留
        l.AcquireShared();
        l.ReleaseShared();
        l.AcquireExclusive();
        l.ReleaseExclusive();

        var ok2 = Task.Run(() => { l.AcquireShared(); l.ReleaseShared(); });
        Assert.True(ok2.Wait(2000), "多轮交替后读者仍必须能立即进入");
    }

    // ════════════════════════════════════════════════════════════
    //  Try 变体（投机路径——不自旋、不排队）
    // ════════════════════════════════════════════════════════════

    /// <summary>★ TryShared 契约：空闲成功计数+1；排他持有/写等待时 false 且不改变锁态。</summary>
    [Fact]
    public void TryAcquireShared_FreeSucceeds_HeldExclusiveFails()
    {
        var l = new SpinRWLock();
        Assert.True(l.TryAcquireShared());
        Assert.Equal(1, l.ReaderCount);
        l.ReleaseShared();

        l.AcquireExclusive();
        Assert.False(l.TryAcquireShared(), "排他持有时 TryAcquireShared 必须立即 false");
        l.ReleaseExclusive();
        Assert.True(l.TryAcquireShared(), "释放后 TryAcquireShared 恢复成功");
        l.ReleaseShared();
    }

    /// <summary>★ TryExclusive 投机契约：失败<b>不挂 pending 闸</b>——失败后新读者照常可进
    /// （与 AcquireExclusive 的本质区别：后者进门即挡新读者）。拿不到就走别的路，不留副作用。</summary>
    [Fact]
    public void TryAcquireExclusive_HeldSharedFails_WithoutPendingGate()
    {
        var l = new SpinRWLock();
        Assert.True(l.TryAcquireExclusive());
        Assert.True(l.IsHeldExclusive);
        l.ReleaseExclusive();

        // 共享持有 → Try 失败，但不得挂 pending（后续读者不受影响）
        l.AcquireShared();
        Assert.False(l.TryAcquireExclusive(), "共享持有时 TryAcquireExclusive 必须立即 false");
        var readerGotIn = Task.Run(() => l.TryAcquireShared());
        Assert.True(readerGotIn.Wait(2000) && readerGotIn.Result,
            "TryAcquireExclusive 失败后新读者必须仍能进——投机写者不挂 pending 闸");
        l.ReleaseShared();
        l.ReleaseShared();
    }

    /// <summary>★ 无损伤下溢绊线（v2）：count==0 的释放直接抛，锁字不动——
    /// 异常后锁仍可正常使用（旧实现先减再回滚，减完到回滚之间锁字短暂呈现假写者位阻塞等待方）。</summary>
    [Fact]
    public void ReleaseShared_Underflow_NoDamage_LockStillUsable()
    {
        var l = new SpinRWLock();
        Assert.Throws<InvalidOperationException>(() => l.ReleaseShared());
        Assert.Equal(0, l.ReaderCount);
        Assert.False(l.IsHeldExclusive);

        // 绊线触发后锁照常工作（无残留破坏态）
        l.AcquireShared();
        Assert.Equal(1, l.ReaderCount);
        l.ReleaseShared();
        l.AcquireExclusive();
        l.ReleaseExclusive();
    }
}
