using System.Threading;
using FluentAssertions;
using TC.Tier.Core.Execution;
using Xunit;

namespace TC.Tier.Core.Tests.Execution;

/// <summary>
/// AsyncPump 契约测试（执行原语 1:1）——单线程泵：续体回流同线程/顺序执行/异常重抛/
/// 再入自锁快速失败/上下文还原/ConfigureAwait(false) 段安全/空转等待唤醒/取消/并发独立实例。
/// </summary>
public class AsyncPumpTests
{
    [Fact]
    public void Run_ContinuationsFlowBackToSameThread()
    {
        var pump = new AsyncPump();
        var callerThread = Environment.CurrentManagedThreadId;
        var threads = new List<int> { callerThread };

        pump.Run(async () =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();                       // 续体 Post 回泵队列——同线程执行
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            threads.Add(Environment.CurrentManagedThreadId);
        });

        threads.Should().AllBeEquivalentTo(callerThread, "泵域内续体回流发起（泵）线程——单线程亲和");
    }

    [Fact]
    public void Run_MultipleAwaits_ExecuteInOrder()
    {
        var pump = new AsyncPump();
        var order = new List<int>();

        pump.Run(async () =>
        {
            order.Add(1);
            await Task.Yield();
            order.Add(2);
            await Task.Yield();
            order.Add(3);
        });

        order.Should().BeInAscendingOrder().And.HaveCount(3);
    }

    [Fact]
    public void Run_ReturnsValue()
    {
        var pump = new AsyncPump();
        var result = pump.Run(async () =>
        {
            await Task.Yield();
            return 42;
        });
        result.Should().Be(42);
    }

    [Fact]
    public void Run_WorkThrows_OriginalExceptionRethrown()
    {
        var pump = new AsyncPump();

        var act = () => pump.Run(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public void Run_SyncThrowBeforeFirstAwait_PropagatesAndContextRestored()
    {
        var pump = new AsyncPump();
        var before = SynchronizationContext.Current;

        var act = () => pump.Run<int>(() => throw new ArgumentException("sync"));

        act.Should().Throw<ArgumentException>();
        SynchronizationContext.Current.Should().Be(before, "发起段抛出——上下文还原");
    }

    [Fact]
    public void Run_NestedRun_FailsFast()
    {
        var pump = new AsyncPump();

        var act = () => pump.Run(async () =>
        {
            await Task.Yield();
            pump.Run(async () => await Task.Yield());   // 泵内再泵 = 自锁——快速失败
        });

        act.Should().Throw<InvalidOperationException>("*嵌套*");
    }

    [Fact]
    public void Run_RestoresPreviousSynchronizationContext()
    {
        var pump = new AsyncPump();
        var before = SynchronizationContext.Current;

        pump.Run(async () => await Task.Yield());

        SynchronizationContext.Current.Should().Be(before, "Run 返回后原上下文还原");
    }

    [Fact]
    public void Run_ConfigureFalseSegments_CompleteSafely()
    {
        // 第三方库形态：ConfigureAwait(false) 段不回流（线程池执行）——泵空转等待其完成
        var pump = new AsyncPump();
        var pumpThread = -1;

        pump.Run(async () =>
        {
            pumpThread = Environment.CurrentManagedThreadId;
            await Task.Delay(50).ConfigureAwait(false);   // 不回流的段——池上续体
            // 后续续体（无 ConfigureAwait(false)）在 ConfigureAwait(false) 段之后仍捕获泵上下文回流
        });

        // 无死锁完成即契约成立（若泵误等回流将挂到测试超时）
    }

    [Fact]
    public void Run_IdleWaiting_WakesOnCompletion()
    {
        // work 等待长外部段（不回流）——队列空 park（Post/完成双唤醒）——不烧 CPU 且按时完成
        var pump = new AsyncPump();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        pump.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);   // 全程不回流——纯空转等待路径
        });

        sw.Elapsed.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(280, "外部段实际耗时");
    }

    [Fact]
    public void Run_CancellationDuringIdleWait_ThrowsOce()
    {
        var pump = new AsyncPump();
        using var cts = new CancellationTokenSource(100);

        var act = () => pump.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);   // 不回流——空转等待被取消打断
        }, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task Run_ConcurrentInstances_AreIndependent()
    {
        // 两个泵在两个线程各自 Run——上下文互不干扰（SetSynchronizationContext 是线程槽）
        var pumpA = new AsyncPump("A");
        var pumpB = new AsyncPump("B");
        var threadsA = new List<int>();
        var threadsB = new List<int>();

        var a = Task.Run(() => pumpA.Run(async () =>
        {
            threadsA.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            threadsA.Add(Environment.CurrentManagedThreadId);
            await Task.Delay(20).ConfigureAwait(false);   // 重叠窗口制造——末尾段（其后无域内代码：不回流段之后不在泵线程）
        }));
        var b = Task.Run(() => pumpB.Run(async () =>
        {
            threadsB.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            threadsB.Add(Environment.CurrentManagedThreadId);
        }));

        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));   // 超时上界由 WaitAsync 保证
        threadsA.Should().AllBeEquivalentTo(threadsA[0], "泵 A 域内单线程亲和");
        threadsB.Should().AllBeEquivalentTo(threadsB[0], "泵 B 域内单线程亲和");
        // ★ 泗间线程独立性不断言物理线程号不同——OS 线程复用下两泵合法拿到同一 ManagedThreadId
        //   （M 系 mac runner 实锤 2026-09-11）；独立性的本质 = 各泵独占自己的同步上下文槽，
        //   由上面两条域内单线程亲和断言承载
    }

    [Fact]
    public void Run_ManyContinuations_AllDrained()
    {
        var pump = new AsyncPump();
        const int n = 1_000;
        var count = 0;

        pump.Run(async () =>
        {
            for (var i = 0; i < n; i++)
            {
                await Task.Yield();
                count++;
            }
        });

        count.Should().Be(n, "全部续体泵完——无丢失无残留");
    }

    [Fact]
    public void Send_ExecutesInlineOnCallingThread()
    {
        var pump = new AsyncPump();
        var executedOn = -1;
        var pumpThread = -1;

        pump.Run(async () =>
        {
            pumpThread = Environment.CurrentManagedThreadId;
            await Task.Yield();
            var ctx = SynchronizationContext.Current!;
            ctx.Send(_ => executedOn = Environment.CurrentManagedThreadId, null);   // Send=调用线程就地执行（泵线程）
        });

        executedOn.Should().Be(pumpThread, "Send 就地执行——不 Post 入队");
    }
}
