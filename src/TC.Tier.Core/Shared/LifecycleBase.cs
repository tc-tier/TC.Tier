using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Shared;

/// <summary>
/// 通用生命周期骨架基类（Core 层）——无恢复 hints struct 版本。
/// </summary>
/// <param name="logger">可选的日志器实例。</param>
public abstract class LifecycleBase(ILogger? logger = null)
    : LifecycleBase<EmptyHints>(null, logger);
/// <summary>
/// 通用生命周期骨架基类（Core 层）。数据结构基类与 IO 引擎基类共同继承本类，获得统一的
/// <para>★ 实现 <see cref="ILifecycle{THints}"/>——统一"同步 void Initialize 启动后台恢复 + 观测/等待"模型，
///   详见 src/TC.Tier.Core/docs/lifecycle.md。★ <b>Initialize 是类面方法（不在 ILifecycle 接口面）</b>——
///   设计决策接口面消除：启动入口由各持有者自己的装配面提供（引擎 = StorageEngineBuilder.Start），
///   接口只保留观测/等待；结构层内部组合面仍经具体类型调用。</para>
/// <para>★ 内建 <see cref="Resources"/>（<see cref="ResourceGroup"/>）——统一资源释放，
///   子类构造期/钩子里 <c>Resources.Add(...)</c>，Dispose 自动统一转发（消灭各基类 _owner/_disposables 样板）。</para>
/// <para>★ Initialize 是固定模板（IO 引擎与数据结构共用同一套 On 钩子模式）：
///   CAS 闸门 → <see cref="OnInitializeBegin"/> → <see cref="CreateRecovery"/> → 订阅进度事件 → 后台恢复 task。
///   子类只 override 钩子，不改流程。</para>
/// </summary>
/// <typeparam name="THints">各持有者自己的恢复 hints struct（见 lifecycle-standard.md §5）。</typeparam>
public abstract class LifecycleBase<THints> : ILifecycle<THints>, IDisposable, IAsyncDisposable
    where THints : struct
{
    // === 资源（统一释放）===
    /// <summary>资源组——子类 <c>Resources.Add(_engine)</c> / <c>Resources.Add(metaEngine, "meta")</c>，
    /// Dispose 统一转发（逆序、异步优先、聚合异常、防双释放）。</summary>
    protected ResourceGroup Resources { get; }

    // === 恢复（private set——Initialize 内部赋值一次，外部不可 set 替换）===
    private IRecovery<THints>? _recovery;
    /// <summary>恢复算法实例。构造期注入（ctor 参数）或 Initialize 内由 <see cref="CreateRecovery"/> 工厂创建、一次性赋值。
    /// <para>★ 不是构造期注入（默认 Recovery 需 this，构造期 this 未就绪）；不是 protected set（防运行期替换）。
    /// 为 null 时 Initialize 跳过后台恢复、直接置 Ready（适用于无需恢复的场景）。</para>
    /// <para>⚠️ <b>竞态契约</b>：Recovery 非 null **不代表恢复已开始**——赋值发生在后台 task 启动前。
    ///   <see cref="OnInitializeComplete"/> 在恢复成功后的后台 task 内执行（非同线程、非调度即完成）；
    ///   并发线程读到 Recovery 非 null 但 task 可能尚未跑 Recover。判断"是否就绪/恢复中"一律以
    ///   <see cref="RecoveryState"/> / <see cref="IsReady"/> 为准，不以 Recovery 非 null 为准。</para>
    /// <para>★ <b>纯读语义</b>：getter 只 <c>Volatile.Read</c>，零副作用——任意线程任意时刻读安全；
    ///   默认实例的创建收在 Initialize 的 CAS 闸门内（单一创建点，天然互斥）。
    ///   早期实现 <c>_recovery ??= CreateRecovery()</c> 在 getter 懒创建：并发读双实例竞态 +
    ///   Initialize 前任何 <see cref="IsReady"/> 观测读都会偷跑工厂（Dispose 前查一下也凭空建出
    ///   Recovery）——已废除（设计决策：风险不该上层承担，基类直接原子）。</para></summary>
    protected IRecovery<THints>? Recovery
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _recovery);
    }

    /// <summary>virtual 工厂：子类 override 返回恢复算法（默认实现/注入实例）。
    /// <para>对齐 <c>StorageEngineBase.RecoverAndBuildReader</c> 的 virtual 模式。
    /// 在 <see cref="OnInitializeBegin"/> 后由 Initialize 调用，返回值由基类持有。</para>
    /// <para>默认返回 null——null 语义：跳过后台恢复、直接置 Ready（无需恢复的结构）。
    ///   需恢复的结构子类 override 返回 <c>new DefaultXxxRecovery(this)</c>。</para>
    /// </summary>
    protected virtual IRecovery<THints>? CreateRecovery() => null;

    // === 跟踪（实例级跟踪 + 诊断 + 泄漏可见）===
    /// <summary>实例唯一标识（InstanceTracker 注册时生成，诊断/泄漏定位用）。</summary>
    public Guid Id { get; }

    // === ILifecycle 编排基础设施（Task 不外露）===
    // ★ 状态机唯一归 IRecovery（OnRecoveryStart/Complete/Failed + RecoveryState.IsCompleted/Error）。
    //   LifecycleBase 只管编排：后台 task + CAS 闸门 + CTS + WaitForReady join task。不重复维护状态。
    private Task? _recoverTask;
    private CancellationTokenSource? _recoveryCts;
    private int _initialized; // CAS 闸门（Initialize 幂等）
    private int _disposed;
    private event Action<RecoveryProgress>? _recoveryProgressChanged; // 对外发布（转发自 Recovery）

    // === 内建长生命周期 worker（见 worker-loop-unified-design.md §4）===
    /// <summary>子类通过 <see cref="ConfigureBackgroundWorker"/> 配置的后台循环 worker。Dispose 时先于 Resources 释放。</summary>
    private BackgroundWorkerLoop? _backgroundWorker;

    /// <summary>转发 Recovery→本类事件的处理器（命名字段，便于 Dispose 时 -= 解绑；匿名 lambda 无法 -=）。
    /// ★ 命名方法组（非匿名 lambda）——委托实例唯一，构造期一次绑定。</summary>
    private readonly Action<RecoveryProgress> _forwardProgress;

    /// <summary>对象是否已释放（子类读，防 Dispose 后回调）。</summary>
    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>日志器（可选注入，子类内部用）。</summary>
    protected readonly ILogger? Logger;


    /// <summary>
    ///  构造（protected，子类继承）。注入可选 <see cref="IRecovery{THints}"/> + <see cref="ILogger"/>。
    /// </summary>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="logger">可选的日志器实例。</param>
    protected LifecycleBase(IRecovery<THints>? recovery = null, ILogger? logger = null)
    {
        Resources = new ResourceGroup();
        Logger = logger;
        _recovery = recovery;
        // ★ 命名方法组（非匿名 lambda）——委托实例唯一，Dispose 时可 -= 命中
        _forwardProgress = ForwardProgress;
        // ★ 实例跟踪（对齐 lease）——构造即注册；Dispose 时 Unregister。
        //   ★ 不传 stateProvider——捕获 this 的 lambda 会强引用实例，破坏 ConditionalWeakTable 弱引用语义。
        //   实时状态查询走实例自身的 RecoveryState 属性，不在跟踪器里。
        Id = InstanceTracker.Register(this, GetType().Name);
    }

    /// <summary>转发 Recovery→本类事件（命名方法组，委托实例唯一）。进度百分比由 IRecovery 自管（RecoveryState.Percent）。</summary>
    private void ForwardProgress(RecoveryProgress p) => _recoveryProgressChanged?.Invoke(p);

    /// <summary>诊断：当前所有存活的 LifecycleBase 实例快照（对齐 lease GetActiveLeases——泄漏时定位"谁没 Dispose"）。</summary>
    public IReadOnlyList<TrackedInstanceInfo> GetAliveInstances(string? typeFilter = null)
        => InstanceTracker.GetAlive(typeFilter);

    // ════════════════════════════════════════════════════════════
    // === ILifecycle 对外契约（全 final，子类不 override）===
    // ★ 状态查询委托 Recovery（IRecovery 是状态机唯一归属）；Recovery==null（无需恢复）→ Completed。
    // ════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    /// <remarks>★ 委托 <see cref="Recovery"/>（IRecovery 自管状态）。
    /// Recovery==null 两种情况：① 未 Initialize（_initialized==0）→ 未就绪；② Initialize 过但 CreateRecovery 返回 null（无需恢复）→ 就绪。</remarks>
    public bool IsReady => Recovery is { } r ? r.IsReady : Volatile.Read(ref _initialized) != 0;

    /// <inheritdoc/>
    /// <remarks>★ 委托 <see cref="Recovery"/>.<see cref="IRecovery.RecoveryState"/>。
    /// Recovery==null：未 Initialize → NotStarted；Initialize 过但无需恢复 → Completed/100。</remarks>
    public RecoveryState RecoveryState =>
        Recovery is { } r
            ? r.RecoveryState
            : (Volatile.Read(ref _initialized) != 0
                ? new RecoveryState { Phase = RecoveryPhase.Completed, Percent = 100 }
                : new RecoveryState { Phase = RecoveryPhase.NotStarted, Percent = 0 });

    /// <inheritdoc/>
    /// <remarks>转发内部 <see cref="Recovery"/> 的事件。</remarks>
    public event Action<RecoveryProgress>? RecoveryProgressChanged
    {
        add => _recoveryProgressChanged += value;
        remove => _recoveryProgressChanged -= value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ⚠️ <b>禁止在异步上下文调用</b>：UI/ASP.NET 等同步上下文下，同步阻塞后台 Task 会经典死锁。异步调用方用 <see cref="WaitForReadyAsync"/>。
    /// <para>★ 以状态机为准（不以 _recoverTask 是否 null 为准）——消灭"状态已进入 Recovering 但 task 引用未发布"的竞态窗口。</para>
    /// <para>★ <see cref="RecoveryPhase.Failed"/> 时本方法重抛恢复异常（不让"已失败"返回成功）。</para>
    /// <para>★ <see cref="RecoveryPhase.NotStarted"/>（如取消）时 task 可能为 null——此时等待语义无意义，按未启动处理。</para>
    /// </remarks>
    public void WaitForReady()
    {
        ThrowIfDisposed();
        WaitGuardPreCheck(); // Failed→重抛；NotStarted+无 task→抛"未启动"
        Volatile.Read(ref _recoverTask)?.Wait();
    }

    /// <inheritdoc/>
    /// <remarks>⚠️ <b>禁止在异步上下文调用</b>（同 <see cref="WaitForReady()"/> 死锁警告）。</remarks>
    public bool WaitForReady(int timeoutMilliseconds)
    {
        ThrowIfDisposed();
        if (IsReady) return true; // ★ 已就绪立即返回 true（不依赖 task——CreateRecovery=null 走此路径，无 task）
        WaitGuardPreCheck();
        return Volatile.Read(ref _recoverTask)?.Wait(timeoutMilliseconds) ?? false;
    }

    /// <inheritdoc/>
    /// <remarks>★ 异步调用方安全入口（避免 <see cref="WaitForReady()"/> 同步阻塞在同步上下文死锁）。
    /// 用 <see cref="Task.WaitAsync(CancellationToken)"/>（.NET 6+）。同 <see cref="WaitForReady()"/> 的状态机守卫。</remarks>
    public Task WaitForReadyAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (IsReady) return Task.CompletedTask; // ★ 已就绪立即返回（不依赖 task）
        WaitGuardPreCheck();
        // ★ _recoverTask 为 null（状态 NotStarted/Completed 但无 task）→ 已是终态，不等待
        var t = Volatile.Read(ref _recoverTask);
        return t?.WaitAsync(ct) ?? Task.CompletedTask;
    }

    /// <summary>
    /// WaitForReady* 系列的统一前置守卫（消灭竞态 + Failed 观测）。
    /// <para>① <see cref="RecoveryPhase.Failed"/> → 重抛 RecoveryState.Error（不让"已失败"返回成功）。</para>
    /// <para>② 状态非终态(NotStarted/Recovering)但 _recoverTask 为 null → 抛"未启动"（消灭
    ///   "状态已进 Recovering 但 task 引用未发布"窗口——底层内核必须消灭理论竞态）。</para>
    /// <para>注：调用方应先 <see cref="IsReady"/> 短路（Completed 无 task 时不进本守卫）。
    ///   状态查询走 <see cref="RecoveryState"/>（委托 Recovery）。</para>
    /// </summary>
    private void WaitGuardPreCheck()
    {
        var state = RecoveryState; // ★ 委托 Recovery.RecoveryState（IRecovery 自管状态）
        if (state.Phase == RecoveryPhase.Failed)
            throw new InvalidOperationException("恢复任务已失败", state.Error);
        // 状态已进入恢复中/未启动，但 task 尚未发布（Initialize 内 Recovery.OnRecoveryStart 与 _recoverTask 赋值非原子）
        if (Volatile.Read(ref _recoverTask) is null && state.Phase != RecoveryPhase.Completed)
            throw new InvalidOperationException(
                $"恢复任务尚未启动（状态 {state.Phase}，Initialize 可能未完成调度）");
    }

    /// <inheritdoc/>
    /// <inheritdoc/>
    /// <remarks>★ 双通道取消：① <see cref="IRecovery.CancelRecovery"/> 显式通知（实现做取消清理）
    ///   + ② <c>_recoveryCts.Cancel()</c> 信号兜底（RecoverAsync 在 checkpoint 检查 ct 收 OCE）。
    ///   两者配合——显式通知覆盖"ct 轮询做不到"的清理（停扫盘/释放扫描资源），信号兜底覆盖长循环。</remarks>
    public void CancelRecovery()
    {
        Recovery?.CancelRecovery(); // ★ 显式通道：通知 IRecovery 实现做取消清理
        _recoveryCts?.Cancel(); // ★ 信号兜底：RecoverAsync 在 checkpoint 检查 ct
    }

    /// <summary>
    /// ★ 守卫：未就绪（NotStarted/Recovering/Failed）时抛 <see cref="InvalidOperationException"/>。子类读写入口第一行调用。
    /// <para>对齐 lifecycle-standard.md §3.2——Ready 前读写抛异常，调用方据此外部自旋等。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void EnsureReady()
    {
        ThrowIfDisposed();
        if (!IsReady)
            throw new InvalidOperationException(
                $"{GetType().Name} 尚未完成恢复（当前 {RecoveryState.Phase}），不可读写");
    }

    // ════════════════════════════════════════════════════════════
    // === Initialize（固定模板，对齐 StorageEngineBase.Initialize 的 On 钩子模式）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 统一就绪入口（同步 void，启动内部后台恢复后立即返回）。模板方法，子类不改流程、只 override 钩子。
    /// <para>★ <b>类面方法，不在 ILifecycle 接口面</b>（决策）——外部禁止经接口调 Initialize；
    ///   引擎侧经 StorageEngineBuilder.Start/StartAsync 一步到位，结构层经具体类型内部调用。</para>
    /// <para>流程：CAS 幂等闸门 → <see cref="OnInitializeBegin"/>（引擎 init + 资源/策略装配）
    ///   → <see cref="CreateRecovery"/> → 订阅进度事件 → 后台 task 跑 Recover → Completed/FAILED。</para>
    /// <para>★ <see cref="CreateRecovery"/> 返回 null 时跳过后台恢复、直接置 Ready（无需恢复的结构场景）。</para>
    /// <para>调用线程不阻塞、其他结构不阻塞；只阻塞本结构读写（Ready 前 EnsureReady 抛异常）。</para>
    /// <para>★ 重试契约：① <b>启动阶段失败</b>（<see cref="OnInitializeBegin"/>/钩子/<see cref="CreateRecovery"/> 抛）
    ///   回退 <c>_initialized=0</c>，允许重新 Initialize 重试；② <b>后台恢复执行阶段失败</b>
    ///   （<c>RecoverAsync</c> 抛异常→Failed）<c>_initialized</c> 保持 1，<b>实例不可修复，必须 Dispose 销毁重建</b>
    ///   （存储场景 Failed 多为持久数据损坏，重试无意义）。再次 Initialize 会被 CAS 静默拒绝。</para>
    /// <para>★ 重试时旧 task 守卫：取消后旧后台 task 可能尚未执行完 catch 块（写 NotStarted/_initialized=0）。
    ///   若不等其结束就启动新 Initialize，旧 task 延迟写入会覆盖新状态→状态机崩溃。
    ///   故重试路径（存在旧 _recoverTask）必须等旧 task 彻底结束再继续。首次 Initialize 无旧 task，不阻塞。</para>
    /// </summary>
    public void Initialize(THints hints = default)
    {
        ThrowIfDisposed();

        // ★ CAS 闸门：幂等。并发重复调只静默返回（幂等语义，对齐 lifecycle-standard.md §3.3）。
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

        // ★ 重试守卫：若存在旧后台 task（取消后重试场景），必须等其彻底结束再继续——
        //   旧 task 的 catch 块会回退 _initialized=0，若并发会覆盖新 Initialize 的状态→崩溃。
        //   重试是低频路径，等旧 task 收尾是必要代价；首次 Initialize _recoverTask 为 null，不阻塞。
        var priorTask = Volatile.Read(ref _recoverTask);
        if (priorTask is not null)
        {
            try
            {
#pragma warning disable TCSG031 // 设计必需：Initialize 同步重试守卫——等旧 task 收尾（低频路径）
                priorTask.Wait();
#pragma warning restore TCSG031
            }
            catch
            {
                /* 旧 task 取消/失败在此吞——重试不因旧异常中断 */
            }
        }
        try
        {
            // 阶段 1 + 2 钩子：引擎 init + 资源/策略装配（此时 this 已构造完，虚方法安全）
            OnInitializeBegin();
            // ★ 默认 Recovery 单一创建点：CAS 闸门内（同实例 Initialize 互斥），构造注入优先、
            //   重试保留旧实例（Reset 语义允许重跑）。getter 是纯读——创建不再借道属性，
            //   Initialize 前的 IsReady/Dispose 等观测读零副作用（不偷跑工厂）。
            Volatile.Write(ref _recovery, _recovery ?? CreateRecovery());
            if (Recovery is { } rec)
            {
                // 订阅 Recovery 进度事件，转发给本结构的 RecoveryProgressChanged
                // ★ 用命名字段 _forwardProgress（非匿名 lambda），Dispose 时可 -= 解绑
                rec.RecoveryProgressChanged += _forwardProgress;

                _recoveryCts?.Dispose();
                _recoveryCts = new CancellationTokenSource();
                var ct = _recoveryCts.Token;

                // ★ 统一后台恢复 + [后] 钩子串行（LifecycleBase 契约）：Initialize 启动后台 task 后立即返回，不阻塞调用线程。
                //   一个 task 内严格 前(OnInitializeBegin)→中(RecoverAsync)→后(OnInitializeComplete) 串行——
                //   保证不变量"OnInitializeComplete 仅在本实例恢复成功完成后调用"，故 Complete/Ready/恢复完成 三者同义，
                //   任意嵌套层都成立。需"启动后阻塞等就绪"调 WaitForReady()（其 Wait 本 task，含 [后]）；
                //   异步调用方调 WaitForReadyAsync()。不再按"引擎/数据结构"分叉（旧 RunRecoveryInBackground 已删）。
                // ★ LongRunning：恢复是 IO 密集长时间任务（扫盘日志、读 meta），分离到独立线程，
                //   避免长时间占用线程池工作线程耗尽池。
                // ⚠️ ct 语义：传 CancellationToken.None 而非 ct——否则 ct 在调度前取消会返回 CanceledTask，
                //   lambda 体不运行，OCE 处理（回退 _initialized=0）永不执行，实例死锁。
                //   取消靠 lambda 内部 await RecoverAsync(hints, ct) 响应。
                Volatile.Write(ref _recoverTask, Task.Factory.StartNew(async () =>
                    {
                        try
                        {
                            // [中] 恢复——失败/取消则 await 抛出，[后] 不执行（Complete 语义=恢复成功）
                            await rec.RecoverAsync(hints, ct).ConfigureAwait(false);
                            // [后] 仅在恢复成功后串行执行。此时恢复已 MarkReady、实例可用——
                            //   钩子异常只 log 不 fault 主 task（否则与 WaitForReadyAsync 的 IsReady 早返回竞态漏异常）。
                            try
                            {
                                if (!IsDisposed) OnInitializeComplete();
                            }
                            catch (Exception ex)
                            {
                                Logger?.LogWarning(ex,
                                    "{Type} (Id={Id}) OnInitializeComplete 异常（恢复已成功，忽略）",
                                    GetType().Name, Id);
                            }
                            // ★ 后台 worker 在 [后]（恢复完成 + OnInitializeComplete）之后启动——
                            //   长生命周期 worker 须等全部就绪（恢复产物 + 装配）后才跑，不在恢复前空转 / 抢资源 / 读半恢复状态。
                            //   故 ConfigureBackgroundWorker 放 OnInitializeBegin 或 OnInitializeComplete 均可（都在 Start 之前）。
                            if (!IsDisposed) Volatile.Read(ref _backgroundWorker)?.Start();
                        }
                        catch (OperationCanceledException)
                        {
                            Volatile.Write(ref _initialized, 0); // 取消视为非失败——允许重试
                            throw;
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap());
            }
            else
            {
                // ★ Recovery == null：无需恢复——[前]→[后] 直连（中为空），OnInitializeComplete 同步执行。
                //   RecoveryState/IsReady 委托返回 Completed/true（见属性实现）。
                Logger?.LogInformation("{Type} (Id={Id}) Initialize 跳过后台恢复（CreateRecovery 返回 null）",
                    GetType().Name, Id);
                OnInitializeComplete();
                // ★ worker 在 [后] 后启动（对齐恢复分支：长任务等全部就绪才跑）。
                Volatile.Read(ref _backgroundWorker)?.Start();
            }
        }
        catch
        {
            try
            {
                // ★ 重试时旧 Recovery 状态残留——调 Reset() 重置 IRecovery 自身状态（允许再次 Recover）
                if (Recovery is { } oldRecForReset) oldRecForReset.Reset();
            }
            catch
            {
                // 重置失败
                Logger?.LogWarning("Initialize 失败，重置旧 Recovery.Reset 状态失败（允许 Initialize 重试）");
            }
            finally
            {
                // 恢复算法：virtual 工厂创建（子类 override 决定默认/注入）
                // ★ 重试场景：取消后 _initialized 回 0 允许重新 Initialize，此时旧 Recovery 可能残留——
                //   先解绑其事件订阅，防旧 Recovery 若仍存活触发事件干扰本实例（绝对健壮，对齐 lease 防御哲学）
                if (Recovery is { } oldRec) oldRec.RecoveryProgressChanged -= _forwardProgress;
                // 启动失败（OnInitializeBegin/CreateRecovery 抛）——回退，允许重试（对齐引擎）
                Volatile.Write(ref _initialized, 0);
            }
            throw;
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 子类钩子（virtual，对齐 IO 引擎 On 钩子命名）===
    // ════════════════════════════════════════════════════════════

    /// <summary>★ Initialize 第一阶段钩子——this 构造完后、后台恢复启动前。
    /// <para>子类在此：① <c>_engine.Initialize(...)</c>（Runtime 层引擎 init）
    ///   ② <c>Resources.Add(_engine)</c>（注册主引擎到资源组）
    ///   ③ 创建 MetaPolicy（按 MetaPolicyKind）
    ///   ④ 后期资源 <c>Resources.Add(metaEngine/overflowEngine, ...)</c></para>
    /// <para>此时 this 已完全构造，虚方法安全；<see cref="CreateRecovery"/> 紧随其后被基类调用。</para>
    /// </summary>
    protected virtual void OnInitializeBegin()
    {
    }

    /// <summary>★ Initialize 第二阶段（[后]）钩子——<b>仅在本实例恢复成功完成后</b>调用。
    /// <para>★ 不变量：Complete / Ready / 恢复完成 三者同义。每层都用同一 <see cref="LifecycleBase{THints}"/>，
    ///   故任意嵌套层此不变量都成立——父层在此钩子里可安全读"本层恢复产物"。</para>
    /// <para>★ 执行线程：有恢复时在后台 LongRunning task 内（串行接在 <c>await RecoverAsync</c> 之后）；
    ///   无恢复（<see cref="CreateRecovery"/> 返回 null）时在调用线程同步执行。</para>
    /// <para>★ 异常策略：恢复已 MarkReady、实例可用——钩子内抛异常只 log，不 fault 主 task。</para>
    /// <para>★ 需配 <see cref="BackgroundWorkerLoop"/> 的子类：在 <see cref="OnInitializeBegin"/> 或本钩子里调
    ///   <see cref="ConfigureBackgroundWorker"/> 均可——worker.Start 在本钩子<b>之后</b>跑（等恢复完成 + 装配就绪）。</para>
    /// </summary>
    protected virtual void OnInitializeComplete()
    {

    }

    // ════════════════════════════════════════════════════════════
    // === 内建 worker 配置（见 worker-loop-unified-design.md §4）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 子类配置内建 worker——在 <see cref="OnInitializeBegin"/> 或 <see cref="OnInitializeComplete"/> 里调均可。
    /// <para>★ 传入自定义 <see cref="BackgroundWorkerLoop"/> 派生实例（填了 RunOneCycleAsync/ProcessItemAsync 业务逻辑）。</para>
    /// <para>★ 基类负责：Start（<see cref="OnInitializeComplete"/> 之后——等恢复完成 + 装配就绪；长任务不在恢复前空转 / 抢资源）
    ///   + Stop + WaitForExit（Dispose 编排内）。</para>
    /// <para>★ 只须在 worker.Start 之前调即可——Start 在 [后] 之后跑，故 Begin / Complete 都来得及。</para>
    /// <para>★ 幂等：重复调只首次生效（CAS）。</para>
    /// </summary>
    protected void ConfigureBackgroundWorker(BackgroundWorkerLoop worker)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            worker.Dispose();
            return;
        } // 已 Dispose——直接释放传入的 worker

        if (Interlocked.CompareExchange(ref _backgroundWorker, worker, null) == null) return;
        worker.Dispose(); // 已配置过——释放多余的
    }

    /// <summary>内建 worker 是否已配置（诊断用）。</summary>
    protected bool HasBackgroundWorker => Volatile.Read(ref _backgroundWorker) is not null;

    // ════════════════════════════════════════════════════════════
    // === Dispose（统一转发 Resources）===
    // ════════════════════════════════════════════════════════════

    /// <summary>已释放则抛 <see cref="ObjectDisposedException"/>。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// Dispose 后置清理（同步 Dispose 与异步 DisposeAsync 共用的非 task 部分）：
    /// 释放 CTS + 置 null 引用 + 解事件 + 清空多播。
    /// <para>★ task 等待在调用方做（同步 Wait / 异步 await 分轨），保证 DisposeAsync 不同步阻塞。</para>
    /// </summary>
    private void CleanupAfterTask()
    {
        // 释放 CTS（不再需要取消信号）
        _recoveryCts?.Dispose();
        _recoveryCts = null;
        // 置 null 引用（不强制等 GC，但让语义清晰：Dispose 后 task 句柄不再可用）
        Volatile.Write(ref _recoverTask, null);
        // ★ 解绑 Recovery→本类的事件转发（防 Recovery 被复用时回调到已 Dispose 的实例）
        if (Recovery is { } rec) rec.RecoveryProgressChanged -= _forwardProgress;
        // ★ 清空对外事件多播——防御性兜底（底层绝不假设调用方自觉解绑）：
        //   发布方 Dispose 时清空，防 Dispose 后误触 + 打破对外部订阅者的引用链。
        _recoveryProgressChanged = null;
    }

    /// <summary>同步 Dispose 的 task + worker 等待：Stop worker → Cancel recover → 等 recoverTask → 等 worker → Dispose worker → Cleanup。</summary>
    private void CancelRecoveryAndCleanup()
    {
        // ★ 先停 worker（防 worker 访问即将释放的组件——use-after-free 防护）
        //   worker.Stop() 只是发信号（cts.Cancel + _running=0），不阻塞——立即返回
        try
        {
            Volatile.Read(ref _backgroundWorker)?.Stop();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "停止 background worker 异常");
        }

        // 现状：Cancel recover + 等 recoverTask
        _recoveryCts?.Cancel();
        // ★ 同步等 task 结束——防 Resources.Dispose 后 task 仍访问已释放资源（use-after-free）。
        //   吞 OCE/异常——Dispose 期间的预期内收尾，不让它们冒泡。
        try
        {
            Volatile.Read(ref _recoverTask)?.Wait();
        }
        catch
        {
            /* 取消/失败在此吞——Dispose 不应抛 */
        }

        // ★ 等 worker 退出（recover task 已结束后再等 worker——两者可能都改组件，串行等更安全）
        try
        {
            Volatile.Read(ref _backgroundWorker)?.WaitForExit();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "等待 background worker 退出异常");
        }

        // ★ worker 退出后释放它（cts.Dispose + 内部资源）
        try
        {
            Volatile.Read(ref _backgroundWorker)?.Dispose();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "释放 background worker 异常");
        }

        Volatile.Write(ref _backgroundWorker, null);

        CleanupAfterTask();
    }

    /// <summary>异步 DisposeAsync 的 task + worker 等待：Stop worker → Cancel recover → 等 recoverTask → 等 worker → Dispose worker → Cleanup。
    /// ★ 用 await 而非 Wait——异步 DisposeAsync 不应同步阻塞调用线程（避免 UI/ASP.NET 同步上下文死锁）。</summary>
    private async Task CancelRecoveryAndCleanupAsync()
    {
        // ★ 先停 worker
        try
        {
            Volatile.Read(ref _backgroundWorker)?.Stop();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "停止 background worker 异常");
        }

        if (_recoveryCts is not null)
            await _recoveryCts.CancelAsync();
        if (Volatile.Read(ref _recoverTask) is { } task)
        {
            // ★ 异步等——不阻塞调用线程；吞 OCE/异常（取消/失败都是 Dispose 期间的预期内收尾）
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                /* 取消/失败在此吞 */
            }
        }

        // ★ 异步等 worker（不阻塞调用线程）
        if (Volatile.Read(ref _backgroundWorker) is { } worker)
        {
            try
            {
                await worker.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "等待 background worker 退出异常");
            }

            try
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "释放 background worker 异常");
            }

            Volatile.Write(ref _backgroundWorker, null);
        }

        CleanupAfterTask();
    }

    /// <summary>
    /// ★ 同步 Dispose——non-virtual 模板（子类不可绕过核心清理）。对齐 lease <c>Dispose(){ Rollback(); }</c> 不可绕过模式。
    /// <para>★ non-virtual（非 virtual）：子类无法 override；若用 <c>new</c> 隐藏，通过基类引用调用仍走本方法
    ///   （多态不生效），核心清理不可绕过。子类额外清理走 <see cref="DisposeOverride"/> 钩子。</para>
    /// <para>编排：CAS 防双释放 → 未 Initialize 告警 → CancelRecoveryAndCleanup（核心）→
    ///   <see cref="DisposeOverride"/>(子类钩子) → Resources.Dispose（核心）→ Unregister → SuppressFinalize。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        WarnIfNotInitialized(); // 防御：new 后未 Initialize 就 Dispose 告警
        CancelRecoveryAndCleanup(); // 核心：取消 task + 同步等结束 + 解事件 + 清多播（不可绕过）
        try
        {
            DisposeOverride(disposing: true);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "DisposeOverride 异常");
        }

        Resources.Dispose(); // 核心：资源释放（不可绕过）
        InstanceTracker.Unregister(this); // 正常释放——从跟踪表移除
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// ★ 异步 Dispose——non-virtual 模板（同 Dispose 编排，异步轨）。子类 override <see cref="DisposeOverrideAsync"/>。
    /// <para>★ task 等待走 <see cref="CancelRecoveryAndCleanupAsync"/>（await 而非 Wait）——不阻塞调用线程。</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        WarnIfNotInitialized();
        await CancelRecoveryAndCleanupAsync().ConfigureAwait(false); // ★ 异步等 task，不阻塞
        try
        {
            await DisposeOverrideAsync(disposing: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "DisposeOverrideAsync 异常");
        }

        await Resources.DisposeAsync().ConfigureAwait(false);
        InstanceTracker.Unregister(this);
        GC.SuppressFinalize(this);
    }

    /// <summary>子类同步额外清理钩子（base 核心清理不可绕过）。默认空。</summary>
    /// <param name="disposing">true=用户调 Dispose（可触托管资源）；false=终结器（不可触托管）。
    ///   本基类终结器不做资源清理（仅告警），disposing 恒为 true 供子类判断。</param>
    protected virtual void DisposeOverride(bool disposing)
    {
    }

    /// <summary>子类异步额外清理钩子（base 核心清理不可绕过）。默认空。</summary>
    protected virtual ValueTask DisposeOverrideAsync(bool disposing) => ValueTask.CompletedTask;

    /// <summary>防御：new 后未 Initialize 就 Dispose——告警（调用方违约可见，对齐 lease 防御哲学）。</summary>
    private void WarnIfNotInitialized()
    {
        if (Volatile.Read(ref _initialized) == 0)
            Logger?.LogWarning("{Type} (Id={Id}) 被 Dispose 但从未 Initialize——疑似用法错误",
                GetType().Name, Id);
    }

    /// <summary>
    /// ★ 轻量终结器——仅泄漏告警，不释放资源（资源释放走 Dispose；finalize 顺序不可控，不在此做）。
    /// <para>对齐 lease 防御：实例被 GC 但未 Dispose → logger 告警（无 logger 静默，不冒充）。
    ///   这是 .NET 推荐的 finalize 用法（探测泄漏，非释放资源）。</para>
    /// <para>finalize 队列负担可接受（LifecycleBase 实例数有限，非热路径对象）。</para>
    /// <para>⚠️ Logger 是普通引用——终结器运行时实例字段可能已被 GC 回收（finalize 顺序不可控）。
    ///   若 Logger 已被 finalize，调用它会抛——终结器抛异常会终止进程。故必须先 null 判断防护。</para>
    /// </summary>
    ~LifecycleBase()
    {
        // ★ Logger != null 防护——finalize 时 Logger 可能已被 GC，调用会抛并终止进程
        if (Volatile.Read(ref _disposed) != 0 || Logger is null) return;
        try
        {
            Logger.LogWarning("{Type} (Id={Id}) 被 GC 但未 Dispose——疑似资源泄漏",
                GetType().Name, Id);
        }
        catch
        {
            /* 终结器吞所有异常——绝不让 finalize 抛出终止进程 */
        }
    }
}