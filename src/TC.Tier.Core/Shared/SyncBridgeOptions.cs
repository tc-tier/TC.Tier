namespace TC.Tier.Core.Shared;

/// <summary>
/// <see cref="SyncAsyncBridge"/> 的调用选项（docs/sync-async-bridge.md §6.1）。
/// </summary>
public sealed record SyncBridgeOptions
{
    /// <summary>诊断名（操作句柄名 / 超时现场 / 日志标识）。默认 "sync-bridge"。</summary>
    public string Name { get; init; } = "sync-bridge";

    /// <summary>
    /// 执行 work 的调度器。null（默认）= <see cref="SyncAsyncBridge.DefaultScheduler"/>（桥专用独立池）；
    /// 可注入 own <see cref="IsolatedTaskScheduler"/> 实例分池（嵌套桥 / 独占分区场景）。
    /// </summary>
    public TaskScheduler? Scheduler { get; init; }

    /// <summary>
    /// 同步等待（<c>Run</c> 系）的超时上限（ms）。默认 <see cref="SyncAsyncBridge.DefaultTimeoutMs"/>（15s）。
    /// <para>★ 有界纪律：超时抛 <see cref="TimeoutException"/>（带现场）——同步 API 不允许无限期阻塞。
    ///   大对象/慢介质调用点按需调大（如 multipart 传整段预算）。</para>
    /// </summary>
    public int TimeoutMs { get; init; } = SyncAsyncBridge.DefaultTimeoutMs;

    /// <summary>日志（超时 WARN / 未观察告警；透传 <see cref="AsyncOperation"/> 泄漏绊线）。默认 null 静默。</summary>
    public ILogger? Logger { get; init; }
}