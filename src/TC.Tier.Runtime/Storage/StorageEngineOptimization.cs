namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 存储引擎优化参数——用于调整存储引擎的性能和资源使用策略。
/// </summary>
public sealed record StorageEngineOptimization
{
    /// <summary>
    /// CPU 采样间隔。null = 采样与 CPU 限流关闭（默认——不构造采样器、不开后台采样、热路径直通零开销）；
    /// 非 null = 值即采样周期，武装 CPU 限流（<see cref="ThrottleLowCutoff"/>/<see cref="ThrottleHighCutoff"/> 生效）。
    /// </summary>
    public TimeSpan? SampleInterval { get; init; }

    /// <summary>
    /// EMA 平滑系数（0 到 1 之间）。用于计算指数移动平均值，以平滑数据波动。较高的值会使平均值对新数据更敏感，较低的值会使平均值更平滑。默认值为 0.5。
    /// </summary>
    public double EmaAlpha { get; init; } = 0.5;

    /// <summary>
    /// 节流低阈值（0 到 1 之间）。当负载低于此阈值时，存储引擎将减少资源使用，以节省能耗和提高效率。默认值为 0.7。
    /// </summary>
    public double ThrottleLowCutoff { get; init; } = 0.70;

    /// <summary>
    /// 节流高阈值（0 到 1 之间）。当负载高于此阈值时，存储引擎将增加资源使用，以提高性能和响应速度。默认值为 0.9。
    /// </summary>
    public double ThrottleHighCutoff { get; init; } = 0.90;

    /// <summary>
    /// CPU 限流自旋预算（毫秒，默认 30*1000 = 30秒）——负载越线后同步路径自旋等待 CPU 回落的 deadline。
    /// 运行时下限保护：实际 deadline 取 max(本值, 采样周期)——小于一个采样周期的 deadline 观察不到
    /// CPU 回落（采样器按周期发布），自旋退化为必超时。仅在限流武装（<see cref="SampleInterval"/> 非 null）时生效。
    /// </summary>
    public long ThrottleSpinMilliseconds { get; init; } = 30 * 1000;

    /// <summary>
    /// _segIndex 初始容量（= maxSegId + 1，恢复路径用扫盘最大段号；默认 8）。
    /// </summary>
    public int IndexCapacity { get; init; } = 8;

    /// <summary>
    /// 自旋等待时间（毫秒，默认 30*1000 = 30秒）。
    /// </summary>
    public long SpinMilliseconds { get; init; } = 30 * 1000;

    /// <summary>
    /// 警告间隔（每多少次尝试记录一次警告，默认 32）。
    /// </summary>
    public int WarnEvery { get; init; } = 32;

    /// <summary>
    /// worker 消费者数（默认 2）。用于段建造/回收/压缩等异步任务的并发处理。过多可能导致线程切换开销增加，过少可能导致任务堆积。
    /// </summary>
    public int WorkerConsumers { get; init; } = 2;

    // ═══════════════════════════════════════════════════════════════
    //  不可变链（builder 子对象——With* 返回新实例）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>设置 worker 消费者数（返回新实例）。</summary>
    /// <param name="consumers">消费者个数（建议 ≥ 1；过多增加线程切换开销，过少导致任务堆积）。</param>
    /// <returns>替换 WorkerConsumers 后的新 <see cref="StorageEngineOptimization"/> 实例。</returns>
    public StorageEngineOptimization WithWorkerConsumers(int consumers)
        => this with { WorkerConsumers = consumers };

    /// <summary>设置段表初始容量（返回新实例）。</summary>
    /// <param name="capacity">段表索引初始容量（段条目数，非负）。</param>
    /// <returns>替换 IndexCapacity 后的新 <see cref="StorageEngineOptimization"/> 实例。</returns>
    public StorageEngineOptimization WithIndexCapacity(int capacity)
        => this with { IndexCapacity = capacity };
}
