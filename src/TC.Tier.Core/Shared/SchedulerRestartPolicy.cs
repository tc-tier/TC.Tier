namespace TC.Tier.Core.Shared;

/// <summary>专用线程死亡重启策略（§4）。</summary>
public enum SchedulerRestartPolicy
{
    /// <summary>不重启——线程死亡即标记调度器降级（故障快失败）。</summary>
    None,
    /// <summary>重启一次；若再死则停止、降级。</summary>
    RestartOnce,
    /// <summary>总是重启维持 M 线程（默认）。</summary>
    Always,
}