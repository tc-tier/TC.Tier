namespace TC.Tier.Core.Shared;

/// <summary>
/// 后台循环的 5 档优先级（值小者先出）。内建队列固定 5 档，覆盖全部底层 worker 场景。
/// </summary>
public enum WorkerPriority : byte
{
    /// <summary>最高——强制/紧急（如 Allocate 缺段强制建段）。</summary>
    Critical = 0,
    /// <summary>高——正常优先任务（如普通建段）。</summary>
    High = 1,
    /// <summary>普通——默认优先级（如段满事件、管道消息）。</summary>
    Normal = 2,
    /// <summary>低——可延迟任务（如区间表压缩）。</summary>
    Low = 3,
    /// <summary>后台——空闲时处理的清理/维护任务。</summary>
    Background = 4,
}