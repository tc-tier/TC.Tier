using TC.Tier.Core.Shared;

namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// BackgroundWorkerLoop 多生产者 × 多消费者独立压测——脱离引擎单独验证 N 的稳定性（N=2/4/8/12）。
/// <para>★ 动机（2026-08-15）：引擎 N=2 专轮 10 轮 5 红全挂起类零异常、BucketPriorityQueue 记账静态核验通过——
///   需要独立复现层定位：本测试用多生产者（既有测试只单生产者）+ 高体量 + 引擎同款独占调度器，
///   覆盖引擎真实形态（8 写者并发 OnSegmentCreate/OnSegmentFull 入队 + 池补建 Background 混流）。</para>
/// <para>★ 判据：不丢（总量恰好）不重（每项恰好一次）不挂（预算内完成）。</para>
/// <remarks>★ 与 IsolatedTaskSchedulerTests 共享 InstanceTracker collection——本类 Create tracked
/// scheduler，并行跑会污染后者的绝对计数断言（2026-08-17 flaky 根因）。</remarks>
/// </summary>
[Collection("instance-tracker")]
public class BackgroundWorkerLoopStressTests
{
    /// <summary>计数 worker——Interlocked 计数（无锁消费热路径），完成 TCS 通知。</summary>
    private sealed class StressWorker : BackgroundWorkerLoop<int>
    {
        private readonly int[] _counts;
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _consumed;

        public StressWorker(int consumerCount, int totalItems, TaskScheduler? scheduler)
            : base(scheduler, consumerCount, name: "StressWorker", exitTimeout: TimeSpan.FromSeconds(10))
        {
            _counts = new int[totalItems];
            TotalItems = totalItems;
        }

        public int TotalItems { get; }
        public Task Completed => _tcs.Task;

        protected override ValueTask ProcessItemAsync(int item, CancellationToken ct)
        {
            Interlocked.Increment(ref _counts[item]);
            if (Interlocked.Increment(ref _consumed) == TotalItems)
                _tcs.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public bool AllExactlyOnce()
        {
            foreach (var c in _counts)
                if (c != 1) return false;
            return true;
        }
    }

    [Theory]
    [InlineData(2, 8, 200_000, true)]    // 引擎同款：独占调度器
    [InlineData(2, 8, 200_000, false)]
    [InlineData(4, 8, 200_000, true)]
    [InlineData(8, 16, 200_000, true)]
    [InlineData(12, 16, 200_000, true)]
    public async Task MultiProducer_MultiConsumer_ExactlyOnce_NoHang(int consumers, int producers, int totalItems, bool ownScheduler)
    {
        // ★ ownScheduler=true 镜像引擎（EngineWorkerLoopBase 注入引擎 own 的 IsolatedTaskScheduler）；
        //   false 走基类默认（自建）。两者都测——调度器实现是变量之一。
        IsolatedTaskScheduler? scheduler = null;
        if (ownScheduler)
            scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { Name = "stress" });
        try
        {
            using var worker = new StressWorker(consumers, totalItems, scheduler);
            worker.Start();

            var perProducer = totalItems / producers;
            var Produce = (int producerId) => Task.Run(() =>
            {
                var base_ = producerId * perProducer;
                for (var i = 0; i < perProducer; i++)
                {
                    var item = base_ + i;
                    // 混合优先级（镜像引擎：Critical 建段 / Normal Full+Background）
                    worker.Enqueue(item, item % 11 == 0 ? WorkerPriority.Critical
                        : item % 3 == 0 ? WorkerPriority.High
                        : WorkerPriority.Normal);
                }
            });

            var produceTasks = Enumerable.Range(0, producers).Select(Produce).ToArray();
            await Task.WhenAll(produceTasks).WaitAsync(TimeSpan.FromSeconds(30));
            // ★ 全部入队后给消费预算（不丢不重的最终判据；挂起在这里超时暴露）
            await worker.Completed.WaitAsync(TimeSpan.FromSeconds(60));

            worker.AllExactlyOnce().Should().BeTrue(
                $"consumers={consumers} producers={producers}：{totalItems} 项应每项恰好消费一次（不丢不重）");
        }
        finally
        {
            scheduler?.Dispose();
        }
    }
}
