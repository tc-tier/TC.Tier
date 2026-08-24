using System.Runtime.CompilerServices;
using TC.Tier.Contracts.Common;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 一次性后台操作状态句柄的<b>机制基座</b>——<see cref="AsyncOperation"/> / <see cref="AsyncOperation{TResult}"/>
/// 共用（docs/sync-async-bridge.md §4）。
/// <para>★ 通用后台操作原语（2026-08-24 升级）：取代 Storage 层 IReclaimOperation/ICompactOperation
///   两套同构句柄的手写实现（TCS + 事件 + 时序对齐三层各写一遍）——事件/取消/进度/结果/等待
///   一套契约一处实现，后台操作统一 <c>new AsyncOperation(...)</c> + <c>Report*</c>。</para>
/// <para>★ 实现 <see cref="IAsyncOperation"/>（Contracts）——消费面全集见接口（含
///   <see cref="AsyncOperationStatus"/> 状态轮询 / <see>
///       <cref>IAsyncOperation.Wait(int, CancellationToken)</cref>
///   </see>
///   同步兜底 / <see cref="IAsyncOperation.ThrowIfFailed"/> 终态取异常）。</para>
/// <para>★ 三种消费模式：① 轮询 <see cref="Status"/>/<see cref="IsCompleted"/>（多消费者安全）；
///   ② <see cref="WaitAsync"/>（异步一等公民，失败/取消重抛）；
///   ③ <see>
///       <cref>Wait(int, CancellationToken)</cref>
///   </see>
///   （同步兜底——内嵌 <see cref="AsyncManualResetEvent"/> 的
///   「自旋 → park 分片」分层等待，全程有界，<b>绝不</b>裸 <c>Task.Wait()</c>）。</para>
/// <para>★ 事件：<see cref="Progress"/>（进度）+ <see cref="Failed"/>（失败/取消终态，含订阅者异常隔离）；
///   成功终态事件由子类经 <see cref="OnSucceeded"/> 触发（无结果变体无 Completed 事件——await 等价）。</para>
/// <para>★ 取消：<see cref="Cancel"/> 触发内部链接令牌（构造可链接外部取消——引擎 Dispose 自动取消在途操作）；
///   <see cref="CancellationToken"/> 供后台 worker 在检查点响应。</para>
/// <para>★ 状态机：构造即 <see cref="AsyncOperationStatus.Running"/>（可见性原则）；终态经 CAS 单次转移、
///   不可逆；首个 <c>Report</c> 生效，后续幂等 no-op（对齐 RecoveryBase"Failed 销毁重建"哲学——重试 = 新建操作）。</para>
/// <para>★ 事件时序契约（Storage 历史 flaky 根因区收口）：终态事件先于完成信号置位——等待者
///   （<see cref="WaitAsync"/> 唤醒）苏醒时事件必已投递；订阅竞态经 <see cref="IsCompleted"/> 兜底。</para>
/// <para>★ 泄漏绊线：终态 Failed/Canceled 且从未被任何消费模式观察 → 终结器告警（无 logger 静默，
///   对齐 LifecycleBase 泄漏探测哲学）。</para>
/// <para>★ <c>#if DEBUG</c>：状态转移环形记录（最近 16 次：时间戳/线程/方向），
///   <see cref="Describe"/> 自动携带——对齐 SpinRWLock 值示波器。</para>
/// </summary>
public abstract class AsyncOperationBase : IAsyncOperation
{
    // === 状态（int 压缩：AsyncOperationStatus；0=Running 初始，无需 Interlocked 初始化）===
    private int _state;
    private Exception? _exception;      // 终态异常——置态前写（volatile 序保证观察者见终态即可见异常）
    protected object? _succeededPayload;  // 成功载荷（泛型变体结果）——锁内置态前写（与终态原子发布）
    private int _observed;              // 消费侧观察标志（Wait/WaitAsync/ThrowIfFailed/Exception/Result 读）
    private readonly AsyncManualResetEvent _completed = new();   // 完成信号（默认线程池异步调度——Set 调用点不持锁）
    private readonly object _completionLock = new();             // 终态转移（冷路径，一次性）
    private readonly string _name;      // 诊断名（桥传入，超时/告警现场用）
    private readonly long _createdTicks;
    private readonly ILogger? _logger;  // 泄漏告警用（null=静默）

    // === 取消（后台操作通用契约）===
    private readonly CancellationTokenSource _linkedCts;   // 操作取消——内部 Cancel() 或构造链接的外部令牌

    /// <summary>创建已在途的操作（状态立即为 <see cref="AsyncOperationStatus.Running"/>——可见性原则）。</summary>
    /// <param name="name">诊断名（超时/告警现场）。</param>
    /// <param name="logger">泄漏绊线告警 logger（可选）。</param>
    /// <param name="externalCancellation">外部取消令牌（可选）——如引擎 Dispose 令牌，触发即取消本操作。
    ///   <see cref="CancellationToken"/> 与 <see cref="Cancel"/> 均反映到同一链接源。</param>
    protected AsyncOperationBase(string? name = null, ILogger? logger = null,
        CancellationToken externalCancellation = default)
    {
        _name = string.IsNullOrEmpty(name) ? "async-op" : name;
        _logger = logger;
        _createdTicks = Environment.TickCount64;
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        RecordDebug(AsyncOperationStatus.Running);   // 初始 →Running（发起线程）
    }

    // ════════════════════════════════════════════════════════════
    // === 状态查询（多消费者安全；全部 volatile 读）===
    // ════════════════════════════════════════════════════════════

    /// <summary>当前状态（多消费者安全，轮询用）。</summary>
    public AsyncOperationStatus Status => (AsyncOperationStatus)Volatile.Read(ref _state);

    /// <summary>是否已到终态（Succeeded/Failed/Canceled）。</summary>
    public bool IsCompleted => Volatile.Read(ref _state) != (int)AsyncOperationStatus.Running;

    /// <summary>终态异常（Failed/Canceled 时非 null；Succeeded 为 null）。★ 读取即视为已观察（泄漏绊线）。</summary>
    public Exception? Exception
    {
        get
        {
            MarkObserved();
            return Volatile.Read(ref _exception);
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 事件（后台操作通用契约）===
    // ════════════════════════════════════════════════════════════

    /// <summary>进度（0.0 ~ 1.0）——后台操作推进时经 <see cref="ReportProgress"/> 触发；无进度语义不触发。
    /// ★ 订阅者异常被隔离（不影响操作完成）。</summary>
    public event EventHandler<double>? Progress;

    /// <summary>
    /// 失败/取消终态事件（参数携带原因：异常或 <see cref="OperationCanceledException"/>）。
    /// <para>★ 终态 Failed/Canceled 均触发（取消回滚完成 = Failed 语义）；订阅者异常被隔离。</para>
    /// <para>★ 时序契约：先于完成信号置位——<see cref="WaitAsync"/> 等待者苏醒时事件必已投递。</para>
    /// </summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>上报进度（0.0~1.0）——订阅者异常隔离 + 告警，不中断后台工作。</summary>
    public void ReportProgress(double progress)
    {
        var handler = Progress;
        if (handler is null) return;
        try
        {
            handler(this, progress);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AsyncOperation '{Name}' Progress 订阅者异常", _name);
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 取消（后台操作通用契约）===
    // ════════════════════════════════════════════════════════════

    /// <summary>操作取消令牌——后台 worker 在检查点响应（抛 OCE → 完成侧 <see cref="ReportCanceled"/>）。
    /// 由 <see cref="Cancel"/> 或构造链接的外部令牌触发。</summary>
    public CancellationToken CancellationToken => _linkedCts.Token;

    /// <summary>触发取消——立即返回，后台在下一个检查点中止并回滚（幂等；终态后调用无副作用）。</summary>
    public void Cancel()
    {
        try
        {
            _linkedCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            /* 终态后已释放（终结器）——幂等 */
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 完成侧（桥内部 / 手工装配；CAS 单次生效，幂等）===
    // ════════════════════════════════════════════════════════════

    /// <summary>上报成功（终态）。★ virtual——泛型变体 override 防御无参误用（须携带结果）。</summary>
    public virtual void ReportSucceeded() => ReportTerminal(AsyncOperationStatus.Succeeded, null, null);

    /// <summary>上报失败（终态）。首个终态生效，后续调用幂等 no-op。</summary>
    public void ReportFailed(Exception exception)
        => ReportTerminal(AsyncOperationStatus.Failed, exception ?? throw new ArgumentNullException(nameof(exception)), null);

    /// <summary>上报取消（终态）。首个终态生效，后续调用幂等 no-op。</summary>
    public void ReportCanceled(OperationCanceledException exception)
        => ReportTerminal(AsyncOperationStatus.Canceled,
            exception ?? throw new ArgumentNullException(nameof(exception)), null);

    /// <summary>上报成功（终态，携带载荷——泛型变体用）。载荷与终态在锁内原子发布：首个生效，幂等不覆盖。</summary>
    protected void ReportSucceededWithPayload(object? succeededPayload)
        => ReportTerminal(AsyncOperationStatus.Succeeded, null, succeededPayload);

    /// <summary>成功终态钩子——子类触发自己的 Completed 事件（基类无 Completed 事件：无结果操作的完成 = await 等价）。</summary>
    protected virtual void OnSucceeded() { }

    /// <summary>
    /// 终态转移（锁内冷路径——每操作至多一次生效）。
    /// <para>★ 先写异常槽/成功载荷再置终态（均 volatile）：观察者（acquire 读终态后读异常/载荷）必见其值，
    ///   无"终态可见但值未发布"窗口。锁消除并发 Report 的槽覆盖竞态（只有赢家存在——幂等 no-op 不覆盖）。</para>
    /// <para>★ 事件先于信号（L2/L22 时序契约收口）：终态事件在 <see cref="AsyncManualResetEvent.Set"/> 之前
    ///   同步触发（订阅者异常隔离）——等待者苏醒时事件必已投递，引擎层"完成通知 → 门闩释放"不依赖调度。</para>
    /// </summary>
    private void ReportTerminal(AsyncOperationStatus to, Exception? exception, object? succeededPayload)
    {
        var from = (AsyncOperationStatus)Volatile.Read(ref _state);
        lock (_completionLock)
        {
            if (Volatile.Read(ref _state) != (int)AsyncOperationStatus.Running)
                return;   // 已终态——幂等 no-op（首个终态生效）
            if (exception is not null)
                Volatile.Write(ref _exception, exception);
            if (succeededPayload is not null)
                Volatile.Write(ref _succeededPayload, succeededPayload);
            Volatile.Write(ref _state, (int)to);
        }
        RecordDebug(to, from);

        if (to == AsyncOperationStatus.Succeeded)
        {
            try
            {
                OnSucceeded();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "AsyncOperation '{Name}' Completed 订阅者异常", _name);
            }
        }
        else
        {
            var handler = Failed;
            if (handler is not null)
            {
                try
                {
                    handler(this, exception ?? new InvalidOperationException(
                        $"异步操作终态 {to} 但异常槽为空（{Describe()}）"));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "AsyncOperation '{Name}' Failed 订阅者异常", _name);
                }
            }
        }

        _completed.Set();
    }

    // ════════════════════════════════════════════════════════════
    // === 消费侧：三种模式 ===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 异步等待完成（一等公民）。失败/取消在等待结束后<b>重抛</b>原异常（对齐 Task 语义）。
    /// <para>★ 已完成快路径：终态检查 + 重抛，零分配零等待。</para>
    /// <para>★ ct 仅取消"等待"本身（不取消操作——用 <see cref="Cancel"/>）。</para>
    /// </summary>
    public ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        MarkObserved();
        if (IsCompleted)
        {
            ThrowIfFailedCore();
            return default;
        }
        return WaitAsyncSlow(cancellationToken);
    }

    /// <summary>慢路径：挂起在完成事件上，唤醒后重抛失败/取消。</summary>
    private async ValueTask WaitAsyncSlow(CancellationToken cancellationToken)
    {
        await WaitCompletedAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfFailedCore();
    }

    /// <summary>等完成信号（子类泛型等待复用）。</summary>
    /// <param name="cancellationToken">取消令牌（仅取消等待本身，不取消操作）。</param>
    /// <returns>完成信号 Task（成功/失败/取消均完成）。</returns>
    protected ValueTask WaitCompletedAsync(CancellationToken cancellationToken)
        => _completed.WaitAsync(cancellationToken);

    /// <summary>
    /// ★ 同步兜底等待——分层策略（自旋 → park 分片，内嵌 <see>
    ///     <cref>AsyncManualResetEvent.Wait(int, CancellationToken)</cref>
    /// </see>
    /// ），
    /// 全程有界，<b>绝不</b>裸 <c>Task.Wait()</c>。
    /// <para>★ 语义：完成（含失败）→ true / 重抛失败异常；超时 → false；ct 取消 → OCE。
    ///   调用方拿到 false 即取得超时现场责任（桥的 <c>Run</c> 便捷入口据此 WARN + 抛 TimeoutException）。</para>
    /// <para>⚠️ <paramref name="timeoutMs"/> 必须 &gt; 0（同步等待必须有界——docs/sync-async-bridge.md §8.1）。</para>
    /// </summary>
    /// <param name="timeoutMs">超时毫秒（必须 &gt; 0）。</param>
    /// <param name="ct">取消令牌（仅取消等待本身，不取消操作）。</param>
    /// <returns>完成（含失败）→ true / 超时 → false。</returns>
    public bool Wait(int timeoutMs, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeoutMs, 0);
        MarkObserved();
        if (IsCompleted)
        {
            ThrowIfFailedCore();
            return true;
        }
        if (!_completed.Wait(timeoutMs, ct))
            return false;   // 超时——诊断责任移交调用方
        ThrowIfFailedCore();
        return true;
    }

    /// <summary>
    /// 轮询方终态取异常：<see cref="AsyncOperationStatus.Failed"/>/<see cref="AsyncOperationStatus.Canceled"/>
    /// 重抛存储的异常，Succeeded 为 no-op。
    /// <para>⚠️ 仅终态后可调（非终态抛 <see cref="InvalidOperationException"/>——防御轮询方漏查 <see cref="IsCompleted"/>）。</para>
    /// </summary>
    public void ThrowIfFailed()
    {
        MarkObserved();
        if (!IsCompleted)
            throw new InvalidOperationException($"操作尚未完成（{Describe()}）——ThrowIfFailed 仅终态后可调");
        ThrowIfFailedCore();
    }

    /// <summary>终态失败重抛（Status 已终态）。Failed/Canceled 且异常槽意外为空时抛兜底 IOE（不冒充成功）。</summary>
    protected void ThrowIfFailedCore()
    {
        var st = (AsyncOperationStatus)Volatile.Read(ref _state);
        if (st == AsyncOperationStatus.Succeeded)
            return;
        throw Volatile.Read(ref _exception)
              ?? new InvalidOperationException($"异步操作终态 {st} 但异常槽为空（{Describe()}）");
    }

    /// <summary>标记"已被消费模式观察"（泄漏绊线：Failed/Canceled 终态未被观察 → 终结器告警）。</summary>
    protected void MarkObserved() => Volatile.Write(ref _observed, 1);

    /// <summary>测试用：是否已被消费模式观察（泄漏绊线契约验证）。</summary>
    internal bool IsObserved => Volatile.Read(ref _observed) != 0;

    /// <summary>诊断快照（超时/告警现场）：op 名 + 状态 + 年龄（+ DEBUG 转移历史）。</summary>
    public string Describe()
    {
        var age = Environment.TickCount64 - _createdTicks;
#if DEBUG
        return $"op(name='{_name}' status={Status} ageMs={age}) hist[{DebugHistoryText}]";
#else
        return $"op(name='{_name}' status={Status} ageMs={age})";
#endif
    }

    /// <inheritdoc/>
    public override string ToString() => Describe();

    // ════════════════════════════════════════════════════════════
    // === 泄漏绊线（终态失败未被观察 → 告警，对齐 LifecycleBase 终结器哲学）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 泄漏绊线：终态 Failed/Canceled 且从未被任何消费模式观察（失败被吞）→ 告警。
    /// <para>⚠️ Logger 是普通引用——终结器运行时字段可能已被回收（对齐 LifecycleBase 防护）：
    ///   null 判断 + 全吞（终结器抛异常会终止进程）。</para>
    /// <para>★ 顺带释放内部链接令牌（linked cts 有外部注册，须 Dispose 防泄漏）。</para>
    /// </summary>
    ~AsyncOperationBase()
    {
        try
        {
            _linkedCts.Dispose();
        }
        catch
        {
            /* 终结器吞所有异常 */
        }

        try
        {
            if (Volatile.Read(ref _observed) != 0 || _logger is null) return;
            var st = (AsyncOperationStatus)Volatile.Read(ref _state);
            if (st is not (AsyncOperationStatus.Failed or AsyncOperationStatus.Canceled)) return;
            _logger.LogWarning(
                "AsyncOperation '{Name}' 终态 {Status} 但从未被观察（疑似失败被吞）：{Exception}",
                _name, st, Volatile.Read(ref _exception)?.Message);
        }
        catch
        {
            /* 终结器吞所有异常——绝不让 finalize 抛出终止进程 */
        }
    }

    // ════════════════════════════════════════════════════════════
    // === #if DEBUG：状态转移环形示波器（对齐 SpinRWLock 值示波器）===
    // ════════════════════════════════════════════════════════════

#if DEBUG
    private readonly (long Ticks, int ThreadId, AsyncOperationStatus To)[] _transitions = new (long, int, AsyncOperationStatus)[16];
    private int _transitionIdx;

    /// <summary>记录一次状态转移（环形 16 槽，Interlocked 递增索引——多完成侧并发安全）。</summary>
    /// <param name="to">转入状态。</param>
    /// <param name="from">转出状态（默认 Running）。</param>
    /// <remarks>★ 仅 DEBUG 编译时生效（对齐 SpinRWLock 值示波器）。</remarks>
    private void RecordDebug(AsyncOperationStatus to, AsyncOperationStatus from = AsyncOperationStatus.Running)
    {
        var i = Interlocked.Increment(ref _transitionIdx) - 1;
        _transitions[i & (_transitions.Length - 1)] = (Environment.TickCount64, Environment.CurrentManagedThreadId, to);
    }

    /// <summary>转移历史文本（ oldest→newest，含相对毫秒与线程）。</summary>
    private string DebugHistoryText
    {
        get
        {
            var end = Volatile.Read(ref _transitionIdx);
            var start = Math.Max(0, end - _transitions.Length);
            var parts = new string[end - start];
            for (var i = start; i < end; i++)
            {
                var t = _transitions[i & (_transitions.Length - 1)];
                parts[i - start] = $"{t.To}@+{t.Ticks - _createdTicks}ms/t{t.ThreadId}";
            }
            return string.Join(" -> ", parts);
        }
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordDebug(AsyncOperationStatus to, AsyncOperationStatus from = AsyncOperationStatus.Running) { }
#endif
}