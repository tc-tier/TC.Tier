namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// BackgroundWorkerLoop&lt;T&gt;（泛型子类）单元测试——验证内建队列 + 标准后台任务模式。
/// </summary>
public class BackgroundWorkerLoopGenericTests
{
    private static readonly string[] s_expectedPriorityOrder = ["critical", "high", "normal", "low", "background"];
    // ════════════════════════════════════════════════════════════
    //  测试用最小子类——标准路径（只实现 ProcessItemAsync）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 标准 worker——只实现 ProcessItemAsync，记录收到的元素。
    /// 用 CountdownEvent 让测试同步等待处理完成。
    /// </summary>
    private sealed class CollectWorker<T> : BackgroundWorkerLoop<T>
    {
        public readonly List<T> Received = new();
        private readonly int _targetCount;
        public readonly TaskCompletionSource Tcs;

        public CollectWorker(int targetCount = int.MaxValue)
        {
            _targetCount = targetCount;
            Tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        protected override ValueTask ProcessItemAsync(T item, CancellationToken ct)
        {
            lock (Received) Received.Add(item);
            if (Received.Count >= _targetCount)
                Tcs.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  标准路径：Enqueue → ProcessItemAsync
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnqueueDequeueFIFO_ProcessItemAsyncReceivesItems()
    {
        var worker = new CollectWorker<int>(targetCount: 3);
        worker.Start();

        worker.Enqueue(10);
        worker.Enqueue(20);
        worker.Enqueue(30);

        // 等全部处理完
        await worker.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (worker.Received)
        {
            worker.Received.Should().HaveCount(3);
            worker.Received.Should().BeInAscendingOrder();  // FIFO
            worker.Received.Should().Contain([10, 20, 30]);
        }

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    [Fact]
    public async Task PriorityOrdering_HigherPriorityDequeuedFirst()
    {
        var worker = new CollectWorker<string>(targetCount: 5);
        worker.Start();

        // 先等 worker 进入等待（确保入队前的空转不消耗顺序）
        // 注：此处保留 delay 型等待——"已进入等待"在 CollectWorker 无可观测信号，无法轮询对齐；
        //   且保序的真正保证是「5 连续入队（纳秒级突发）远快于 worker 唤醒（微秒级以上）」，
        //   池压力只会推迟 worker 唤醒（对本测试只会更安全），不存在被压穿的假失败窗口。
        await Task.Delay(50);

        // 按逆序入队——验证按优先级出队
        worker.Enqueue("background", WorkerPriority.Background);
        worker.Enqueue("low", WorkerPriority.Low);
        worker.Enqueue("normal", WorkerPriority.Normal);
        worker.Enqueue("high", WorkerPriority.High);
        worker.Enqueue("critical", WorkerPriority.Critical);

        await worker.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (worker.Received)
        {
            worker.Received.Should().HaveCount(5);
            // 严格优先级：Critical > High > Normal > Low > Background
            worker.Received.Should().BeEquivalentTo(s_expectedPriorityOrder,
                opts => opts.WithStrictOrdering(), "应按优先级从高到低出队");
        }

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    [Fact]
    public async Task ProcessItemAsyncCalled_CorrectItemPassed()
    {
        var worker = new CollectWorker<string>(targetCount: 1);
        worker.Start();

        worker.Enqueue("hello", WorkerPriority.Normal);

        await worker.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (worker.Received)
        {
            worker.Received.Should().ContainSingle().Which.Should().Be("hello");
        }

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    [Fact]
    public void EnqueueBeforeStart_ItemsQueuedAndProcessedWhenStarted()
    {
        var worker = new CollectWorker<int>(targetCount: 2);
        // Start 前入队——队列应缓存
        worker.Enqueue(1);
        worker.Enqueue(2);

        worker.QueueCount.Should().Be(2, "Start 前入队应累积");

        worker.Start();

        worker.Tcs.Task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("应在 Start 后处理完入队的元素");

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  override RunOneCycleAsync 路径
    // ════════════════════════════════════════════════════════════

    /// <summary>override RunOneCycleAsync 的 worker——自定义循环但内建队列仍在。</summary>
    private sealed class OverrideWorker : BackgroundWorkerLoop<int>
    {
        public readonly List<int> Received = new();
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForItemAsync => _tcs.Task;

        protected override ValueTask ProcessItemAsync(int item, CancellationToken ct)
        {
            lock (Received) Received.Add(item);
            _tcs.TrySetResult();
            return ValueTask.CompletedTask;
        }

        // ★ override RunOneCycleAsync——自定义出队逻辑，但用内建队列
        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            // 自定义：先检查队列空则等一下，再出队
            if (QueueCount == 0)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
                return true;  // 重试
            }
            // 从内建队列手动出队（非标准路径，验证 Queue 属性可用）
            if (Queue.TryDequeue(out var item))
                await ProcessItemAsync(item, ct).ConfigureAwait(false);
            return true;
        }
    }

    [Fact]
    public async Task OverrideRunOneCycle_QueueStillAvailable()
    {
        var worker = new OverrideWorker();
        worker.Start();

        worker.Enqueue(42);

        await worker.WaitForItemAsync.WaitAsync(TimeSpan.FromSeconds(5));

        lock (worker.Received)
        {
            worker.Received.Should().Contain(42, "override RunOneCycleAsync 仍能用内建队列");
        }

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  多消费者（consumerCount > 1）
    // ════════════════════════════════════════════════════════════

    /// <summary>多消费者测试 worker——记录已消费 item + 参与消费的线程 ID（锁保护）。
    /// 可选启动屏障：全部消费者首次到齐（Barrier）后才开始消费——确定性证明真并发，
    /// 不受"空闲机器上第一个线程抢先吃完"的调度运气影响。</summary>
    private sealed class MultiConsumerWorker : BackgroundWorkerLoop<int>
    {
        private readonly List<int> _received = new();
        private readonly HashSet<int> _consumerThreads = new();
        private readonly int _target;
        private readonly Barrier? _startBarrier;   // null=无屏障（快照语义测试用）
        public readonly TaskCompletionSource Tcs;

        public MultiConsumerWorker(int consumerCount, bool dedicated, int target, bool requireAllConsumersPresent = false)
            : base(dedicated ? IsolatedTaskScheduler.Shared : null, consumerCount: consumerCount, name: "MultiConsumer")
        {
            _target = target;
            _startBarrier = requireAllConsumersPresent ? new Barrier(consumerCount) : null;
            Tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public int ReceivedCount
        {
            get { lock (_received) return _received.Count; }
        }

        public List<int> SnapshotReceived()
        {
            lock (_received) return new List<int>(_received);
        }

        public int DistinctConsumerThreadCount
        {
            get { lock (_consumerThreads) return _consumerThreads.Count; }
        }

        protected override async ValueTask ProcessItemAsync(int item, CancellationToken ct)
        {
            // ★ 启动屏障：首轮消费前等全部消费者到齐（超时防卡死——屏障语义违反时 fail 而非挂）
            var barrier = _startBarrier;
            if (barrier is not null && barrier.CurrentPhaseNumber == 0)
            {
                // ct 转发：Stop 时屏障等待也被唤醒（消费者未全部在场则本测试失败路径不挂死）
                var ready = false;
                try { ready = barrier.SignalAndWait(TimeSpan.FromSeconds(10), ct); }
                catch (OperationCanceledException) { throw; }   // Stop 取消——正常退出路径
                catch (BarrierPostPhaseException) { /* 不应发生——无 post-phase 动作 */ }
                if (!ready && barrier.CurrentPhaseNumber == 0)
                    throw new TimeoutException("消费者启动屏障 10s 未到齐——消费者未全部并发在场");
            }

            await Task.Yield();   // 让多个消费者在同一 item 流上交错（真并发窗口）

            lock (_received)
            {
                _received.Add(item);
                lock (_consumerThreads) _consumerThreads.Add(Environment.CurrentManagedThreadId);
                if (_received.Count >= _target) Tcs.TrySetResult();
            }
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, false)]
    public async Task MultiConsumer_AllItemsConsumedOnce_AndActuallyConcurrent(int consumers, bool dedicated)
    {
        const int items = 3000;
        // ★ requireAllConsumersPresent：屏障保证 N 个消费者同时在场再消费——
        //   "DistinctConsumerThreadCount > 1" 成为确定性断言（不受调度运气影响，治假红）
        var worker = new MultiConsumerWorker(consumerCount: consumers, dedicated: dedicated, target: items,
            requireAllConsumersPresent: true);
        worker.ConsumerCount.Should().Be(consumers);
        worker.Start();

        for (var i = 0; i < items; i++)
            worker.Enqueue(i, i % 7 == 0 ? WorkerPriority.High : WorkerPriority.Normal);

        await worker.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        worker.Stop();
        worker.WaitForExit();

        // ★ 不丢不重：每个 item 恰好被消费一次
        var snapshot = worker.SnapshotReceived();
        snapshot.Should().HaveCount(items);
        snapshot.Distinct().Should().HaveCount(items);
        // ★ 真并发（屏障保证下确定性）：多个线程参与消费
        worker.DistinctConsumerThreadCount.Should().BeGreaterThan(1, $"consumerCount={consumers} 应扇出多线程");

        worker.Dispose();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(8, true)]
    public async Task MultiConsumer_ExactlyOnce_Semantics(int consumers, bool dedicated)
    {
        // ★ 纯语义测试（无屏障、无线程数断言）：不丢不重不受调度运气影响——
        //   并发线程数是屏障测试的职责，此处只锁 ExactlyOnce 契约
        const int items = 3000;
        var worker = new MultiConsumerWorker(consumerCount: consumers, dedicated: dedicated, target: items);
        worker.Start();

        for (var i = 0; i < items; i++)
            worker.Enqueue(i, i % 7 == 0 ? WorkerPriority.High : WorkerPriority.Normal);

        await worker.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        worker.Stop();
        worker.WaitForExit();

        var snapshot = worker.SnapshotReceived();
        snapshot.Should().HaveCount(items, "不丢");
        snapshot.Distinct().Should().HaveCount(items, "不重");

        worker.Dispose();
    }

    [Fact]
    public void Ctor_RejectsNonPositiveConsumerCount()
    {
        var act = static () =>
        {
            using var _ = new MultiConsumerWorker(consumerCount: 0, dedicated: true, target: 1);
        };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Ctor_RejectsExcessiveConsumerCount_WithClearMessage()
    {
        // 治"外部传巨大 N → 静默卡死无人知因"：超上限必须 fail-fast 且消息说清原因与出路
        var act = static () =>
        {
            using var _ = new MultiConsumerWorker(consumerCount: BackgroundWorkerLoop.MaxConsumerCount + 1, dedicated: true, target: 1);
        };
        act.Should().Throw<ArgumentOutOfRangeException>()
           .Which.Message.Should().Contain("不是线程数").And.Contain("ThreadCount");
    }

    [Fact]
    public async Task ManyConsumers_PoolMode_StartsAndStopsCleanly()
    {
        // 治"巨大 N 卡死"回归：合法上限内的 N（消费者与线程数解耦后不再随 N 开线程）。
        // ★ 卡死检测 = Tcs 10s 超时（若系统楔死，item 无人消费 → WaitAsync 抛 TimeoutException → 测试失败）。
        using var worker = new MultiConsumerWorker(consumerCount: 16, dedicated: false, target: 1);
        worker.Start();
        worker.Enqueue(1);
        await worker.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    /// <summary>验证多消费者 OnLoopExitAsync 只跑一次 + Dispose 不挂。</summary>
    private sealed class ExitCountingWorker : BackgroundWorkerLoop<int>
    {
        public int ExitCount;
        public ExitCountingWorker(int consumerCount)
            : base(IsolatedTaskScheduler.Shared, consumerCount: consumerCount, name: "ExitCount") { }
        protected override ValueTask ProcessItemAsync(int item, CancellationToken ct) => ValueTask.CompletedTask;
        protected override ValueTask OnLoopExitAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref ExitCount);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void MultiConsumer_OnLoopExitAsyncRunsExactlyOnce()
    {
        var worker = new ExitCountingWorker(consumerCount: 4);
        worker.Start();
        for (var i = 0; i < 100; i++) worker.Enqueue(i);
        worker.Dispose();   // Stop + WaitForExit(all 4)
        worker.ExitCount.Should().Be(1, "多消费者下末位退出者跑一次 OnLoopExitAsync");
    }
}
