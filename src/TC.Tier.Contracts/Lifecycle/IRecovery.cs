namespace TC.Tier.Contracts.Lifecycle;

/// <summary>
/// 恢复规范接口——生命周期骨架的状态/进度契约。
/// </summary>
public interface IRecovery
{
    /// <summary>恢复状态快照（原子可读，并发安全）。只读——由实现内部推进。</summary>
    RecoveryState RecoveryState { get; }

    /// <summary>恢复进度变化事件（进度条订阅）。可空。</summary>
    event Action<RecoveryProgress>? RecoveryProgressChanged;

    /// <summary>恢复开始钩子（生命周期骨架起点）。</summary>
    void OnRecoveryStart();

    /// <summary>恢复完成钩子（生命周期骨架终点）。</summary>
    void OnRecoveryComplete();
    /// <summary>是否已就绪（恢复完成或无需恢复）。</summary>
    bool IsReady { get; }
    /// <summary>标记就绪（空盘/无需扫描场景）。</summary>
    void MarkReady();
    /// <summary>★ 重置恢复状态为未恢复——允许再次 RecoverAsync(hints) 覆盖之前的结果。</summary>
    void Reset();

    /// <summary>
    /// ★ 取消恢复（显式通道）——LifecycleBase.CancelRecovery 调用，通知 IRecovery 实现做取消清理。
    /// <para>与 CancellationToken 配合：LifecycleBase 同时发 CTS.Cancel()（信号兜底）+ 本方法（显式通知）。
    ///   实现可在此停止扫盘、释放扫描持有的资源、记录取消点等——这些只靠 ct 轮询做不到。</para>
    /// <para>★ 默认实现为空操作（DIM，仅依赖 ct 轮询）；需要显式取消清理的实现 override 本方法。
    ///   对齐 lease <c>Rollback()</c>/<c>ForceRelease()</c> 显式动作范式。</para>
    /// </summary>
    void CancelRecovery() { }
}
/// <summary>
/// 恢复规范接口——带 hints 泛型参数的版本，约束为 struct 值类型。
/// </summary>
/// <typeparam name="TRecoveryHints">恢复 hints 类型，约束为 struct 值类型。</typeparam>
public interface IRecovery<in TRecoveryHints> : IRecovery
    where TRecoveryHints : struct
{
    /// <summary>异步执行恢复算法（统一后台异步恢复入口，支持 CancellationToken 取消）。
    /// <para>★ 同步版 Recover 已删除——恢复统一走 LifecycleBase.Initialize 启动的后台 task，
    ///   调用方用 WaitForReady/WaitForReadyAsync 观测就绪。</para></summary>
    /// <param name="hints">恢复 hints（可选，缺省值为 default(TRecoveryHints)）</param>
    /// <param name="ct">取消令牌——取消恢复时本方法抛 <see cref="OperationCanceledException"/>。</param>
    ValueTask RecoverAsync(TRecoveryHints hints, CancellationToken ct = default);
}