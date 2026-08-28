namespace TC.Tier.Core.Shared;

/// <summary>
/// 同步-异步桥（docs/sync-async-bridge.md §6）——"写路径必须同步转异步"的统一出口。
/// <para>★ <see cref="Start"/>：在独立池上发起异步工作，<b>立即</b>返回 <see cref="AsyncOperation"/> 状态句柄
///   （返回时状态已同步置 Running——可见性原则）。调用方继续本地逻辑，最后时刻才 <c>Wait</c>（"Start 早、Wait 晚"）。</para>
/// <para>★ <see cref="Run"/>/<see cref="Run{T}"/>：一次性便捷入口（Start + 有界 Wait + 失败重抛）——
///   同步 API 桥接的常规形态，替代裸 <c>GetAwaiter().GetResult()</c>。</para>
/// <para>★ 死锁拆解：异步工作跑 <see cref="DefaultScheduler"/>（IsolatedTaskScheduler 私有线程，
///   continuation 回流私有线程）——推进<b>不依赖公共池可用性</b>；同步等待者 park 在事件上（非 Task.Wait），
///   无同步上下文回流、无完成边缘内联续体语义模糊。</para>
/// <para>★ 再入防护：桥 work 体内再经<b>同一池</b>同步等待 = 池自锁（M 线程全 park 等嵌套 work）→
///   <see cref="Start"/> 快速失败（InvalidOperationException）。嵌套场景注入独立 <c>Scheduler</c> 分池豁免；
///   跨池嵌套等待必须无环（DAG，对齐 WaitForDependenciesAsync 纪律）。</para>
/// <para>★ work 契约：<b>协作式异步</b>——<c>await</c> 让出、不阻塞（M 很小，阻塞任务直接饿死同池操作，
///   对齐 dedicated-task-scheduler.md §7.2 教训）。真正无法异步化的同步重 IO 由调用方注入 own 单线程实例。</para>
/// </summary>
public static class SyncAsyncBridge
{
    /// <summary>同步等待默认超时（ms）。</summary>
    public const int DefaultTimeoutMs = 15_000;

    // === 桥默认独立池（进程级 well-known，惰性创建，不 Dispose——进程意图资源，对齐 IsolatedTaskScheduler.Shared）===
    private static readonly Lazy<IsolatedTaskScheduler> SDefaultScheduler = new(
        static () => new IsolatedTaskScheduler(
            new IsolatedSchedulerOptions
            {
                Name = "sync-bridge",
                ThreadCount = IsolatedTaskScheduler.RecommendedThreadCount,   // Clamp(ProcessorCount,2,4)——work 协作式异步，少量线程高并发
                TaskTimeout = TimeSpan.FromSeconds(5),                        // 桥操作应有界：慢任务告警提前于调用点超时暴露
            },
            track: false),   // 进程意图资源，不注册 InstanceTracker（对齐 Shared）
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>桥默认独立池（M = Clamp(ProcessorCount,2,4) 私有线程，watchdog 开）。</summary>
    public static IsolatedTaskScheduler DefaultScheduler => SDefaultScheduler.Value;

    // === 再入防护：桥 work 执行期间携带"当前池"，嵌套 Start 同池 → 快速失败 ===
    private static readonly AsyncLocal<TaskScheduler?> SCurrentBridgeScheduler = new();

    /// <summary>
    /// 在独立池上发起异步工作，立即返回状态句柄。
    /// <para>★ 可见性原则：返回时 <see cref="AsyncOperationBase.Status"/> 必为 Running——"已受理"不依赖被调度。</para>
    /// <para>★ 高级形态（"Start 早、Wait 晚"）：发起后继续本地逻辑，最后时刻才 Wait。常规同步桥接直接用 <see cref="Run"/>。</para>
    /// <para>⚠️ 有界队列满时 <c>StartNew</c> 阻塞入队线程（生产者背压，防御性有界——docs/sync-async-bridge.md §6.2）。</para>
    /// </summary>
    /// <param name="work">异步工作体（协作式契约：await 让出、不阻塞、不再入同池同步等待）。</param>
    /// <param name="options">桥选项（null = 全默认）。</param>
    /// <param name="cancellationToken">传给 work 的取消令牌（取消由 work 协作响应）。</param>
    /// <returns>状态句柄（可 Wait / Cancel / Describe / LeakDetect）。</returns>
    public static AsyncOperation Start(Func<CancellationToken, ValueTask> work,
        SyncBridgeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var name = options?.Name ?? "sync-bridge";
        var scheduler = options?.Scheduler ?? DefaultScheduler;

        // ★ 再入防护：当前线程正在本池跑桥 work（AsyncLocal 随 await 流动）又同步等本池新操作 = 池自锁。
        //   分池（注入不同 Scheduler）豁免；跨池嵌套须 DAG 无环（契约，见类注释）。
        if (SCurrentBridgeScheduler.Value is { } ambient && ReferenceEquals(ambient, scheduler))
            throw new InvalidOperationException(
                $"桥工作体内禁止再经同一桥池同步等待（'{name}' 再入 {ambient.GetType().Name}）——注入独立 Scheduler 分池，或改为纯异步等待");

        var op = new AsyncOperation(name, options?.Logger);   // ★ 构造即 Running——句柄对外可见前完成受理转移

        // ★ CancellationToken.None（非 ct）：ct 在调度前取消会返回 CanceledTask、lambda 体不运行，
        //   op 永不终态（调用方 Wait 必超时）——取消靠 work 内部响应 ct（对齐 LifecycleBase 同形取舍）。
        // ★ 异常全量收口进 op（wrapper 永不 fault）；fire-and-forget Task 无人观察。
        // ★ await 的 continuation 回流桥池私有线程（TaskScheduler.Current 捕获）——不经公共池。
        _ = Task.Factory.StartNew(async () =>
        {
            SCurrentBridgeScheduler.Value = scheduler;   // 随 work 的 await 流动（再入检测的判据）
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
                op.ReportSucceeded();
            }
            catch (OperationCanceledException oce)
            {
                op.ReportCanceled(oce);
            }
            catch (Exception ex)
            {
                op.ReportFailed(ex);
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler);

        return op;
    }

    /// <summary>
    /// 一次性同步桥接：发起 + 有界等待 + 失败重抛。
    /// <para>★ 超时抛 <see cref="TimeoutException"/>（含 <see cref="AsyncOperationBase.Describe"/> 现场 + WARN 日志）——
    ///   同步 API 有界纪律的机器强制。</para>
    /// </summary>
    /// <param name="work">异步工作体（协作式契约：await 让出、不阻塞、不再入同池同步等待）。</param>
    /// <param name="options">桥选项（null = 全默认）。</param>
    /// <param name="cancellationToken">传给 work 的取消令牌（取消由 work 协作响应）。</param>
    /// <remarks>★ 便捷入口：替代裸 <c>GetAwaiter().GetResult()</c>，对齐 docs/sync-async-bridge.md §6.1。</remarks>
    public static void Run(Func<CancellationToken, ValueTask> work,
        SyncBridgeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var op = Start(work, options, cancellationToken);
        WaitBounded(op, options, cancellationToken);
    }

    /// <summary>带返回值的一次性同步桥接（结果经闭包槽传递：ReportSucceeded 前写入，
    /// Wait 返回 true 后读取——CAS/事件全屏障保证可见性）。</summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="work">异步工作体（协作式契约 ：await 让出、不阻塞、不再入同池同步等待）。</param>
    /// <param name="options">桥选项（null = 全默认）。</param>
    /// <param name="cancellationToken">传给 work 的取消令牌（取消由 work 协作响应）。</param>
    /// <returns>work 的返回值（同步可见）。</returns>
    public static T Run<T>(Func<CancellationToken, ValueTask<T>> work,
        SyncBridgeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = default(T)!;
        var op = Start(async ct =>
        {
            result = await work(ct).ConfigureAwait(false);
        }, options, cancellationToken);
        WaitBounded(op, options, cancellationToken);
        return result;
    }

    /// <summary>有界等待 + 超时现场（超时 = 诊断入口：WARN + TimeoutException）。</summary>
    /// <param name="op">状态句柄。</param>
    /// <param name="options">桥选项（null = 全默认）。</param>
    /// <param name="cancellationToken">传给 Wait 的取消令牌（取消 由调用方协作响应）。</param>
    /// <remarks>★ Wait 失败 = 超时（WARN + TimeoutException）——同步 API 不允许无限期阻塞。</remarks>
    private static void WaitBounded(AsyncOperation op, SyncBridgeOptions? options, CancellationToken cancellationToken)
    {
        var timeoutMs = options?.TimeoutMs ?? DefaultTimeoutMs;
        if (op.Wait(timeoutMs, cancellationToken))
            return;
        options?.Logger?.LogWarning("桥操作超时（{TimeoutMs}ms）：{Describe}", timeoutMs, op.Describe());
        throw new TimeoutException($"桥操作超时（{timeoutMs}ms）：{op.Describe()}");
    }
}
