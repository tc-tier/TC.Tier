namespace TC.Tier.Contracts.Lifecycle;

/// <summary>
/// 生命周期持有者统一接口。全部 <c>LifecycleBase&lt;THints&gt;</c> 派生类实现此接口——
/// 数据结构基类（Log/Ring/Index/Metadata/Blob）与 IO 引擎（StorageEngine）。
/// 泛型参数 <typeparamref name="THints"/> 为各持有者自己的恢复 hints struct。
/// <para>★ 接口面只保留<b>观测/等待</b>契约（IsReady / RecoveryState / 事件 / WaitForReady* / CancelRecovery）；
///   <c>LifecycleBase&lt;THints&gt;.Initialize</c> 启动入口<b>不在接口面</b>——启动由各持有者自己的装配面提供
///   （引擎 = <c>StorageEngineBuilder.Start/StartAsync</c> 一步到位；结构 = 组合器/生成代码内部调用），
///   <b>不允许外部经接口直接调 Initialize</b>（详见 src/TC.Tier.Core/docs/lifecycle.md）。</para>
/// <para>★ 与 <see cref="IRecovery{TRecoveryHints}"/> 正交：ILifecycle 是生命周期持有者对外观测契约，
///   IRecovery 是恢复算法契约（可注入替换），LifecycleBase.Initialize 内部委托 IRecovery.Recover。</para>
/// </summary>
/// <typeparam name="THints">各持有者自己的恢复 hints struct（版本/水位注入，见 lifecycle-standard.md §5）。</typeparam>
public interface ILifecycle<in THints> where THints : struct
{
    // ════════════════════════════════════════════════════════════
    // 观测 / 等待（封装内部 Task，不外露）
    // ════════════════════════════════════════════════════════════

    /// <summary>是否已就绪（恢复完成或无需恢复）。原子可读，并发安全，适合外部轮询自旋。</summary>
    bool IsReady { get; }

    /// <summary>恢复状态快照（原子可读，并发安全）。Phase 枚举观测恢复进度阶段。</summary>
    RecoveryState RecoveryState { get; }

    /// <summary>
    /// 恢复进度变化事件（进度条订阅）。后台恢复推进时触发。
    /// <para>★ 转发内部 IRecovery 的同名事件。</para>
    /// </summary>
    event Action<RecoveryProgress>? RecoveryProgressChanged;

    /// <summary>
    /// 同步等待恢复完成（内部 join 后台恢复 task，阻塞<strong>当前调用线程</strong>直到 Ready）。
    /// <para>★ 与 <c>Initialize</c> 的关系：Initialize 启动后台恢复后立即返回；
    ///   调用方若需"启动后立即阻塞等就绪"，在 Initialize 后调本方法。</para>
    /// <para>★ 不阻塞其他数据结构，只阻塞调用本方法的线程。</para>
    /// <para>★ 若恢复已失败（Failed），本方法重抛恢复异常。</para>
    /// <para>⚠️ <b>禁止在异步上下文调用</b>：UI/ASP.NET 等同步上下文下，同步阻塞后台 Task 会经典死锁。
    ///   异步调用方请用 <see cref="WaitForReadyAsync"/>。</para>
    /// </summary>
    void WaitForReady();

    /// <summary>
    /// 带超时的同步等待。返回是否在超时内就绪。
    /// <para>超时未就绪返回 false（不抛异常），调用方可继续轮询或放弃。</para>
    /// </summary>
    /// <param name="timeoutMilliseconds">超时时间（毫秒）。</param>
    /// <returns>是否在超时内就绪。</returns>
    bool WaitForReady(int timeoutMilliseconds);

    /// <summary>
    /// 异步等待恢复完成——异步调用方的安全入口（避免 <see cref="WaitForReady()"/> 在同步上下文死锁）。
    /// <para>★ 推荐异步调用方使用本方法而非 <see cref="WaitForReady()"/>：<c>WaitForReady</c> 同步阻塞 Task
    ///   在 UI/ASP.NET 同步上下文下会经典死锁。</para>
    /// </summary>
    /// <param name="ct">取消令牌——取消等待时本方法抛 <see cref="OperationCanceledException"/>。</param>
    /// <exception cref="OperationCanceledException">等待被取消（如 <see cref="CancelRecovery"/> 触发）。</exception>
    Task WaitForReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// 取消恢复（向内部后台恢复发取消信号，不保证立即停止）。
    /// <para>后台恢复在下一个 checkpoint 检查取消令牌并抛 OperationCanceledException。</para>
    /// <para>★ 契约：<see cref="WaitForReady()"/> / <see cref="WaitForReadyAsync"/> / <see cref="WaitForReady(int)"/>
    ///   在取消恢复时会抛 <see cref="OperationCanceledException"/>（取消=等待被中断）。</para>
    /// </summary>
    void CancelRecovery();
}
