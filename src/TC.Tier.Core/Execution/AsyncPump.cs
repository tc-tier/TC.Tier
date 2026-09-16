using System.Collections.Concurrent;

namespace TC.Tier.Core.Execution;

/// <summary>
/// 单线程异步泵（执行原语——调用线程驱动异步状态机）：work 执行期间安装专用
/// <see cref="SynchronizationContext"/>，<b>不写 <c>ConfigureAwait(false)</c></b> 的 await 续体经
/// Post 回泵队列、由同一线程逐项执行——单线程亲和（work 状态机无需锁/原子，泵线程是唯一推进者），
/// 零额外线程、零跨线程唤醒（对照 <see cref="SyncAsyncBridge"/>：桥=独立池+等待者 park，泵=就地推进）。
/// <para>★ 上下文契约：泵域内续体回流是<b>功能</b>——消费方在 work 内<b>不写</b> ConfigureAwait(false)
///   （与全仓 ConfigureAwait(false) 纪律的边界：域外它是防 UI 上下文捕获的缺省姿态，域内回流即语义）。
///   第三方库内部的 ConfigureAwait(false) 段不回流（线程池执行）——泵空转等待其完成，语义安全。</para>
/// <para>★ 死锁拆解：Run 内再 Run = 同线程等自己泵（自锁）——快速失败；work 内禁止阻塞等待
///   （协作式异步契约——阻塞占死唯一泵线程，域内续体无人推进）；跨 await 持有的锁需依赖单线程亲和
///   化解（这正是本原语的存在理由），跨线程进入域外等待是调用方责任。</para>
/// <para>★ 空转等待：work 等待不回流的段（外部异步/第三方 ConfigureAwait(false)）时队列空——
///   park 在事件上（Post/任务完成双双唤醒），不自旋不烧 CPU。</para>
/// </summary>
public sealed class AsyncPump
{
    /// <summary>泵内标记（同线程 Run 嵌套自锁检测）。</summary>
    [ThreadStatic]
    private static bool t_insidePump;

    private readonly string _name;
    private readonly ILogger? _logger;

    /// <summary>构造。</summary>
    /// <param name="name">诊断标识（日志区分多实例）。</param>
    /// <param name="logger">日志（null = 静默）。</param>
    public AsyncPump(string? name = null, ILogger? logger = null)
    {
        _name = name ?? nameof(AsyncPump);
        _logger = logger;
    }

    /// <summary>
    /// 一次性同步执行：在调用线程上泵 work 至完成（安装泵上下文 → 发起 → 逐项执行 Post 回流 → 完成）。
    /// 失败重抛 work 的原始异常；取消（等待外部异步段时）抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <param name="work">异步工作体（契约：域内不写 ConfigureAwait(false)、不阻塞、不再入 Run）。</param>
    /// <param name="cancellationToken">取消等待（仅中断空转等待段——work 已发起的部分协作收尾）。</param>
    public void Run(Func<Task> work, CancellationToken cancellationToken = default)
    {
        RunCore<object?>(async () => { await work(); return null; }, cancellationToken);
    }

    /// <summary>带返回值的一次性同步执行（同 <see cref="Run(Func{Task}, CancellationToken)"/>）。</summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="work">异步工作体（契约：域内不写 ConfigureAwait(false)、不阻塞、不再入 Run）。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>work 的返回值（同步可见）。</returns>
    public T Run<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
        => RunCore(work, cancellationToken)!;

    /// <summary>泵核心：上下文安装 → 发起 work → 泵队列至完成 → 还原上下文 → 结果/异常同步交还。</summary>
    private T RunCore<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (t_insidePump)
            throw new InvalidOperationException(
                $"AsyncPump.Run 禁止嵌套（'{_name}' 泉线程内再 Run = 同线程等自己泵——自锁）；泵域内直接 await。");

        var previous = SynchronizationContext.Current!;
        var context = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(context);
        t_insidePump = true;
        Task<T> task;
        try
        {
            task = work();
        }
        catch
        {
            // 发起段即抛——上下文还原后直接外泄（无队列可泵）
            SynchronizationContext.SetSynchronizationContext(previous);
            t_insidePump = false;
            context.Complete();
            throw;
        }

        try
        {
            // 完成唤醒（任务可能在不回流的段上完成——Post 之外的唯一唤醒源）。
            // ★ 不 Dispose 续体任务：取消路径离开时 task 未完成——续体任务同样未完成，
            //   Dispose 未完成任务抛 InvalidOperationException；迟到回调只调 SignalArrival（幂等无害）。
            task.ContinueWith(
                static (_, s) => ((PumpContext)s!).SignalArrival(),
                context, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            while (!task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                if (context.TryTake(out var item))
                {
                    item.Callback(item.State);   // 续体执行——异常经 async 基建收口进 task，这里不抛
                }
                else
                {
                    // 队列空 + 未完成 = work 在不回流的段上（外部异步）——park 等唤醒（Post/完成双信号）
                    context.WaitArrival(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            CompleteRun(previous, context);
            throw;   // 等待取消——work 的迟到续体由 Complete 后的丢弃语义吸收（见 PumpContext.Post）
        }
        catch (Exception ex)
        {
            CompleteRun(previous, context);
            _logger?.LogError(ex, "AsyncPump 泵循环异常：name={Name}", _name);
            throw;
        }
        CompleteRun(previous, context);
#pragma warning disable TCSG137 // 设计必需：此处 Task 已完成（上方循环退出条件）——同步取结果零阻塞；异常在此重抛
        return task.GetAwaiter().GetResult();
#pragma warning restore TCSG137
    }

    private static void CompleteRun(SynchronizationContext previous, PumpContext context)
    {
        SynchronizationContext.SetSynchronizationContext(previous);
        t_insidePump = false;
        context.Complete();
    }

    /// <summary>泵上下文：Post 入队（生产者=任意线程），泵线程取走执行；完成后的迟到 Post 丢弃。
    /// <para>★ 唤醒原语 = <see cref="AsyncManualResetEvent"/> 内联模式（2026-09-03 换装——
    /// 底层高性能件替代手搓 MRES：纯托管 park 无内核事件回退（GC 抖动源消除），内联真实唤醒
    /// 93ns 级（perf/core-primitives-perf.md §3）；park 分片 50ms 自醒防纯自旋烧核。
    /// Post 调用点不持锁（任务完成回调/channel 写后——BoundedChannel 内锁为 Monitor 可重入，
    /// 自写自读无死锁面），内联模式安全）。纯托管无内核资源——无 Dispose 面。</para></summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private const int ParkSliceMs = 50;   // park 自醒分片（空闲低频自检——防脉冲丢失永久睡死）
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly AsyncManualResetEvent _arrived = new(initialState: false, runContinuationsAsynchronously: false);
        private int _completed;

        /// <summary>续体回流入队：完成前入泵队列并唤醒泵线程；泵已收尾后迟到 Post 静默丢弃。</summary>
        /// <param name="d">要投递的回调（await 续体），非空。</param>
        /// <param name="state">回调的上下文状态，可为 null。</param>
        /// <exception cref="ArgumentNullException"><paramref name="d"/> 为 null。</exception>
        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            if (Volatile.Read(ref _completed) != 0)
                return;   // 泵已收尾——迟到续体丢弃（取消路径：等待方已离开，执行无人等待）
            _queue.Enqueue((d, state));
            _arrived.Set();
        }

        /// <summary>阻塞 Send（泵域内同步回调）——不跨线程使用（单线程亲和契约），直接就地执行。</summary>
        /// <param name="d">要同步执行的回调，非空。</param>
        /// <param name="state">回调的上下文状态，可为 null。</param>
        public override void Send(SendOrPostCallback d, object? state)
            => d(state);   // Send 语义=调用线程执行（调用方即泵线程或域外线程——无排队跳针）

        /// <summary>泵线程取走下一个工作项；取空时复位到达信号（供空转等待）。</summary>
        /// <param name="item">成功时接收回调与状态；失败时不修改。</param>
        /// <returns>true 表示取到工作项（调用方就地执行）；false 表示队列为空。</returns>
        public bool TryTake(out (SendOrPostCallback Callback, object? State) item)
        {
            if (_queue.TryDequeue(out item))
            {
                if (_queue.IsEmpty) _arrived.Reset();   // 排空——下次空转等待从新信号起（Set 竞窗由双重检查兜底：Reset 后再验 IsEmpty）
                if (!_queue.IsEmpty) _arrived.Set();
                return true;
            }
            return false;
        }

        /// <summary>park 等新工作项/完成信号（Post 与任务完成双源 Set）——分片自醒（50ms）防纯自旋烧核，
        /// 醒后循环侧重查 IsCompleted/队列。</summary>
        /// <param name="ct">取消令牌；等待期间被取消则抛 <see cref="OperationCanceledException"/>。</param>
        public void WaitArrival(CancellationToken ct)
            => _arrived.Wait(ParkSliceMs, ct);   // 内联模式同步等待：自旋 → Monitor park 分片——零内核事件

        /// <summary>唤醒空转等待中的泵线程（新工作项入队或任务完成时调用；幂等）。</summary>
        public void SignalArrival() => _arrived.Set();

        /// <summary>标记泵收尾：置位完成标志并唤醒可能残留的等待者（取消路径）；此后 Post 到达的工作项被丢弃。</summary>
        public void Complete()
        {
            Interlocked.Exchange(ref _completed, 1);
            _arrived.Set();   // 唤醒可能残留的等待（取消路径）
        }

        // AsyncManualResetEvent 纯托管无内核资源——无可释放句柄
    }
}
