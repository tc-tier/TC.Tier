namespace TC.Tier.Core.Primitives;

/// <summary>
/// 一次性后台操作的状态句柄（无结果变体）——把"同步线程原地阻塞等异步操作"改造为
/// 「异步信号 + 可轮询状态机」的 Core 原语（docs/sync-async-bridge.md §4，SyncAsyncBridge 消费方）。
/// <para>★ API 与既有 AsyncOperation 全兼容（构造/状态/Report*/Wait*/ThrowIfFailed/Describe）——
///   拆基座改造零回归；新增 <see cref="AsyncOperationBase.Progress"/>、<see cref="AsyncOperationBase.Failed"/>、<see cref="AsyncOperationBase.Cancel"/>、
///   <see cref="CancellationToken"/>（后台操作通用契约）。</para>
/// <para>★ 后台操作统一用法：<c>new AsyncOperation(name, logger, externalCancel)</c> →
///   worker 检查 <see cref="AsyncOperationBase.CancellationToken"/> → <c>ReportProgress/ReportSucceeded/ReportFailed</c>。</para>
/// </summary>
public sealed class AsyncOperation : AsyncOperationBase
{
    /// <summary>创建已在途的操作（状态立即为 <see cref="AsyncOperationStatus.Running"/>——可见性原则）。</summary>
    /// <param name="name">诊断名（超时/告警现场）。</param>
    /// <param name="logger">泄漏绊线告警 logger（可选）。</param>
    /// <param name="externalCancellation">外部取消令牌（可选）——如引擎 Dispose 令牌，触发即取消本操作。</param>
    public AsyncOperation(string? name = null, ILogger? logger = null,
        CancellationToken externalCancellation = default)
        : base(name, logger, externalCancellation)
    {
    }
}

/// <summary>
/// 一次性后台操作的状态句柄（有结果变体，仿 <c>Task{T}</c>）——后台操作携带结果（如 CompactResult）。
/// <para>★ 用法：<c>new AsyncOperation{TResult}(name, logger, externalCancel)</c> →
///   worker 完成时 <c>ReportSucceeded(result)</c>；消费方 <c>await op.WaitAsync()</c> 拿结果（失败/取消重抛）。</para>
/// <para>★ <see cref="Completed"/> 事件（携带结果）先于 <see cref="WaitAsync"/> 唤醒（时序契约见基类）。</para>
/// </summary>
public sealed class AsyncOperation<TResult> : AsyncOperationBase, IAsyncOperation<TResult>
{
    /// <summary>创建已在途的操作（状态立即为 <see cref="AsyncOperationStatus.Running"/>——可见性原则）。</summary>
    /// <param name="name">诊断名（超时/告警现场）。</param>
    /// <param name="logger">泄漏绊线告警 logger（可选）。</param>
    /// <param name="externalCancellation">外部取消令牌（可选）——如引擎 Dispose 令牌，触发即取消本操作。</param>
    public AsyncOperation(string? name = null, ILogger? logger = null,
        CancellationToken externalCancellation = default)
        : base(name, logger, externalCancellation)
    {
    }

    /// <summary>成功完成事件（参数携带结果）。★ 事件先于 <see cref="WaitAsync"/> 唤醒（时序契约）。</summary>
    public event EventHandler<TResult>? Completed;

    /// <summary>上报成功（终态）——结果载荷与终态在基类锁内原子发布（首个生效，幂等不覆盖）。</summary>
    public void ReportSucceeded(TResult result) => ReportSucceededWithPayload(result);

    /// <summary>防御误用：泛型操作必须携带结果（override 基类虚方法——任何引用路径均拦截）。</summary>
    public override void ReportSucceeded()
        => throw new InvalidOperationException("AsyncOperation<TResult> 必须携带结果 ReportSucceeded(TResult)");

    /// <summary>
    /// 成功结果（仅终态后读；失败/取消重抛存储异常）。
    /// <para>★ 读取即视为已观察（泄漏绊线）。轮询方语义：先查 <see cref="AsyncOperationBase.IsCompleted"/>
    ///   再读本属性（与 <see cref="AsyncOperationBase.ThrowIfFailed"/> 同纪律）。</para>
    /// </summary>
    public TResult Result
    {
        get
        {
            MarkObserved();
            if (!IsCompleted)
                throw new InvalidOperationException($"操作尚未完成（{Describe()}）——Result 仅终态后可读");
            ThrowIfFailedCore();
            return (TResult)Volatile.Read(ref _succeededPayload)!;
        }
    }

    /// <summary>异步等待完成并返回结果（一等公民）；失败/取消在等待结束后重抛原异常。ct 仅取消等待本身。</summary>
    /// <param name="cancellationToken">取消等待本身（不取消操作本身）。</param>
    /// <returns>终态结果（Succeeded）或重抛原异常（Failed/Canceled）。</returns>
    public new ValueTask<TResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        MarkObserved();
        if (IsCompleted)
        {
            ThrowIfFailedCore();
            return new ValueTask<TResult>((TResult)Volatile.Read(ref _succeededPayload)!);
        }
        return WaitAsyncSlow(cancellationToken);
    }

    private async ValueTask<TResult> WaitAsyncSlow(CancellationToken cancellationToken)
    {
        await WaitCompletedAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfFailedCore();
        return (TResult)Volatile.Read(ref _succeededPayload)!;
    }

    /// <summary>成功终态钩子——触发 <see cref="Completed"/> 事件（基类 ReportTerminal 在信号置位前调用）。</summary>
    protected override void OnSucceeded()
        => Completed?.Invoke(this, (TResult)Volatile.Read(ref _succeededPayload)!);
}
