namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 存储引擎优化参数——用于调整存储引擎的性能和资源使用策略。
/// </summary>
public sealed record StorageEngineOptimization
{
    /// <summary>
    /// 采样间隔（可选）。用于优化存储引擎的性能，通过定期采样数据来调整内部参数。如果为 null，则表示不进行采样优化。
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
    public StorageEngineOptimization WithWorkerConsumers(int consumers)
        => this with { WorkerConsumers = consumers };

    /// <summary>设置段表初始容量（返回新实例）。</summary>
    public StorageEngineOptimization WithIndexCapacity(int capacity)
        => this with { IndexCapacity = capacity };
}
