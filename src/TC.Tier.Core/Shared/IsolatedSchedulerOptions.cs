namespace TC.Tier.Core.Shared;

/// <summary>
/// <see cref="IsolatedTaskScheduler"/> 的构造选项（集中所有旋钮）。
/// <para>★ <see cref="IsolatedTaskScheduler.Shared"/> 用全默认；<see cref="IsolatedTaskScheduler.Create"/> 传自定义。</para>
/// </summary>
public sealed record IsolatedSchedulerOptions
{
    /// <summary>
    /// 专用线程数 M（并发度上限）。默认 <see cref="IsolatedTaskScheduler.RecommendedThreadCount"/>。
    /// <para>★ 超核 throw、过半核 WARN（§7）。</para>
    /// </summary>
    public int ThreadCount { get; init; } = IsolatedTaskScheduler.RecommendedThreadCount;

    /// <summary>
    /// 调度器任务队列容量（§3.3）：
    /// <list type="bullet">
    /// <item><c>0</c>（默认）= 自动有界 <c>max(M*4, 16)</c>（满则阻塞背压）。</item>
    /// <item><c>&gt;0</c> = 指定有界容量。</item>
    /// <item><c>&lt;0</c> = 无界（永不阻塞，仅监控）。</item>
    /// </list>
    /// <para>★ 背压主战场在 worker 工作项队列，调度器队列仅防御性有界（§3.3）。</para>
    /// </summary>
    public int QueueCapacity { get; init; }

    /// <summary>诊断名前缀（线程名 / 日志 / 指标 tag）。默认 "isolated"。</summary>
    public string Name { get; init; } = "isolated";

    /// <summary>日志（可选；防扩散 WARN / 过半核 WARN / 队列满 等用）。默认 null。</summary>
    public ILogger? Logger { get; init; }

    /// <summary>可观测 hub（指标：队列深度 / 执行延迟 / 背压等，§6）。默认 null → <see cref="ObservabilityHub.Disabled"/>（零开销）。</summary>
    public ObservabilityHub? Hub { get; init; }

    /// <summary>watchdog 检查周期（§5）。默认 5s；≤ <see cref="TimeSpan.Zero"/> 关闭 watchdog（纯隔离、无监控的最轻模式）。</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>单任务最大执行时长（§5），超此判慢任务（WARN + 计数）。默认 30s。</summary>
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>疑似死锁判定：连续 N 个 tick 无推进（§5，防抖，减误报）。默认 3。</summary>
    public int DeadlockConfirmTicks { get; init; } = 3;

    /// <summary>专用线程死亡重启策略（§4）。默认 <see cref="SchedulerRestartPolicy.Always"/>（重启维持 M 线程）。</summary>
    public SchedulerRestartPolicy RestartPolicy { get; init; } = SchedulerRestartPolicy.Always;
}