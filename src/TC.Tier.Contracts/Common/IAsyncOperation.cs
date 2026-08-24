namespace TC.Tier.Contracts.Common;

/// <summary>
/// 后台操作句柄（无结果变体）——后台统一执行的返回对象（如 <c>StartReclaim</c>）。
/// <para>★ 取代旧 IReclaimOperation（与 ICompactOperation 两套同构句柄合一）；实现基座 =
///   <c>Core.Primitives.AsyncOperationBase</c>（状态机 + 事件 + 取消 + 进度，见 sync-async-bridge.md §4）。</para>
/// <para>★ 消费面 = <c>AsyncOperation</c> 公开消费成员全集：轮询（<see cref="Status"/>/<see cref="IsCompleted"/>/
///   <see cref="Exception"/>）、异步等（<see cref="WaitAsync"/>）、同步兜底等（<see cref="Wait(int, CancellationToken)"/>）、
///   终态取异常（<see cref="ThrowIfFailed"/>）、事件订阅（<see cref="Progress"/>/<see cref="Failed"/>）、
///   取消（<see cref="Cancel"/>）。</para>
/// <para>★ 取消：<see cref="Cancel"/> 触发后台操作中止（后台尽快回滚/终止，配合 <see cref="WaitAsync"/> 等"回滚完全完成"）；
///   <see cref="WaitAsync"/> 的 ct 仅取消"等待"本身，不取消操作。</para>
/// <para>★ 事件时序契约：终态事件先于 <see cref="WaitAsync"/> 唤醒——等待者苏醒时事件必已投递；
///   订阅竞态用 <see cref="IsCompleted"/> 兜底（先订阅后查，true = 完成早于订阅，事件已错过）。</para>
/// </summary>
public interface IAsyncOperation
{
    /// <summary>当前状态（多消费者安全，轮询用）。</summary>
    AsyncOperationStatus Status { get; }

    /// <summary>完成态查询（成功/失败/取消均算完成）。
    /// <para>★ 事件订阅的竞态防护：先订阅事件、再查本属性——为 <c>true</c> 表示完成早于订阅，
    ///   事件已错过；为 <c>false</c> 时完成必然晚于查询，已订阅的处理器必收到事件。</para></summary>
    bool IsCompleted { get; }

    /// <summary>终态异常（Failed/Canceled 时非 null；Succeeded 为 null）。★ 读取即视为已观察。</summary>
    Exception? Exception { get; }

    /// <summary>进度（0.0 ~ 1.0）。无进度语义的操作不触发。</summary>
    event EventHandler<double>? Progress;

    /// <summary>
    /// 失败/取消终态事件（参数携带原因：异常或 <see cref="OperationCanceledException"/>）。
    /// <para>★ 语义沿用 IReclaimOperation/ICompactOperation 契约：失败（含取消回滚完成）均触发；
    ///   订阅者异常被隔离（不影响操作完成）。</para>
    /// </summary>
    event EventHandler<Exception>? Failed;

    /// <summary>
    /// 触发取消——立即返回，后台操作在下一个取消检查点中止并回滚（幂等）。
    /// <para>★ 调用方需配合 <see cref="WaitAsync"/> 等"回滚完全完成"。</para>
    /// </summary>
    void Cancel();

    /// <summary>
    /// 异步等待操作完全终止（成功/失败/取消回滚完成）；失败/取消在等待结束后重抛原异常（对齐 Task 语义）。
    /// <para>★ <paramref name="ct"/> 仅取消"等待"本身——取消操作本身用 <see cref="Cancel"/>。</para>
    /// </summary>
    /// <param name="ct">取消等待令牌（可选）。</param>
    /// <returns>操作终态完成的 <see cref="ValueTask"/>。</returns>
    ValueTask WaitAsync(CancellationToken ct = default);

    /// <summary>
    /// 同步兜底等待（分层等待：自旋 → park 分片，全程有界，绝不裸 Task.Wait）。
    /// <para>★ 语义：完成（含失败）→ true / 重抛失败异常；超时 → false；ct 取消 → OCE。
    ///   调用方拿到 false 即取得超时现场责任（自行决定重试/放弃）。</para>
    /// <para>⚠️ <paramref name="timeoutMs"/> 必须 &gt; 0（同步等待必须有界）。</para>
    /// </summary>
    /// <param name="timeoutMs">同步等待超时（毫秒）。</param>
    /// <param name="ct">取消等待令牌（可选）。</param>
    /// <returns>完成（含失败）→ true / 重抛失败异常；超时 → false；ct 取消 → OCE。</returns>
    bool Wait(int timeoutMs, CancellationToken ct = default);

    /// <summary>
    /// 轮询方终态取异常：Failed/Canceled 重抛存储的异常，Succeeded 为 no-op。
    /// <para>⚠️ 仅终态后可调（非终态抛 <see cref="InvalidOperationException"/>——防御轮询方漏查 <see cref="IsCompleted"/>）。</para>
    /// </summary>
    void ThrowIfFailed();
}

/// <summary>
/// 后台操作句柄（有结果变体）——携带结果的后台操作（如 <c>StartCompact</c> 返回 <c>CompactResult</c>）。
/// </summary>
public interface IAsyncOperation<TResult> : IAsyncOperation
{
    /// <summary>成功完成事件（参数携带结果）。★ 事件先于 <see cref="WaitAsync"/> 唤醒（时序契约同上）。</summary>
    event EventHandler<TResult>? Completed;

    /// <summary>异步等待完成并返回结果；失败/取消重抛原异常。</summary>
    /// <param name="ct">取消等待令牌（可选）。</param>
    /// <returns>终态结果（Succeeded）或重抛原异常（Failed /Canceled）。</returns>
    new ValueTask<TResult> WaitAsync(CancellationToken ct = default);
}
