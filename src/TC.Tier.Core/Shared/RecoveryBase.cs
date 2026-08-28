using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Shared;

/// <summary>
/// ★ 通用恢复模板基类（Core 层）——模板方法模式：钉死 <see cref="RecoverAsync"/> 编排，
/// 子类只 override <see cref="OnRecoveryCoreAsync"/>（真正的恢复算法）。
/// <para>与 <see cref="LifecycleBase{THints}"/> 对称：<c>LifecycleBase</c> 钉死 Initialize 的
///   前→中→后三阶段，<c>RecoveryBase</c> 钉死 RecoverAsync 的五步编排
///   （TryEnter → WaitForDependencies → Start → Core → Complete → MarkReady）。两者合起来构成
///   "生命周期 + 恢复" 的完整通用骨架——继承 <c>LifecycleBase</c> + 注入 <c>RecoveryBase</c> 派生实例即可。</para>
/// <para>★ 消灭各 <see cref="IRecovery{TRecoveryHints}"/> 实现复制粘贴的状态机样板：
///   CAS 三态闸门 + <c>_state</c> + <c>MarkReady</c>/<c>Reset</c>/<c>RaiseProgress</c>/
///   <c>TryEnterRecovering</c> + Start/Complete 钩子 + RecoverAsync 编排 + 失败置 Failed——全部收进基类。</para>
/// <para>★ <see cref="OnRecoveryStart"/>/<see cref="OnRecoveryComplete"/> 由本模板在 RecoverAsync 内
///   <b>保证调用</b>（串行接在 Core 前后），不再是各实现手动调的"伪钩子"。当前为 <c>public virtual</c> 以满足
///   <see cref="IRecovery"/> 接口契约；待接口瘦身（移除这两个方法）后收为 <c>protected virtual</c>。</para>
/// </summary>
/// <typeparam name="THints">恢复 hints 类型（值类型 struct，由各持有者自定义，如
///   <c>EngineRecoveryHints</c>/<c>LogRecoveryHints</c>/<c>RingRecoveryHints</c> 等）。</typeparam>
public abstract class RecoveryBase<THints> : IRecovery<THints>
    where THints : struct
{
    // ════════════════════════════════════════════════════════════
    //  状态机（CAS 三态闸门 + RecoveryState 快照）
    // ════════════════════════════════════════════════════════════

    private RecoveryState _state = new() { Phase = RecoveryPhase.NotStarted };

    /// <summary>CAS 三态闸门：0=Unrecovered（未恢复）、1=Recovering（恢复中）、2=Ready（已恢复）。</summary>
    private volatile int _flag;

    private const int FlagUnrecovered = 0;
    private const int FlagRecovering = 1;
    private const int FlagReady = 2;

    // ════════════════════════════════════════════════════════════
    //  IRecovery 对外契约（全 final——子类不 override；自定义行为走下面的钩子）
    // ════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public RecoveryState RecoveryState => _state;

    /// <inheritdoc/>
    public event Action<RecoveryProgress>? RecoveryProgressChanged;

    /// <inheritdoc/>
    public bool IsReady => Volatile.Read(ref _flag) == FlagReady;

    /// <summary>★ 标记就绪——模板在 <see cref="OnRecoveryComplete"/> 后自动调。
    /// <para>外部一般不直接调；需要"跳过 Core 直接就绪"的场景，子类在
    ///   <see cref="OnRecoveryCoreAsync"/> 内 <c>return ValueTask.CompletedTask</c> 即可，模板仍会统一 MarkReady。</para></summary>
    public void MarkReady()
    {
        Interlocked.Exchange(ref _flag, FlagReady);
        _state = _state with { Phase = RecoveryPhase.Completed, Percent = 100 };
    }

    /// <summary>★ 重置恢复状态为未恢复——允许再次 <see cref="RecoverAsync"/> 覆盖之前的结果。
    /// <para>由 <see cref="LifecycleBase{THints}"/>.Initialize 的重试路径调用（旧恢复取消/失败后重新 Initialize）。</para></summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _flag, FlagUnrecovered);
        _state = new() { Phase = RecoveryPhase.NotStarted };
    }

    /// <inheritdoc/>
    /// <remarks>DIM 默认空——靠 <c>CancellationToken</c> 轮询即可取消；需要显式取消清理
    /// （释放扫描持有的资源、记录取消点）的子类 override 本方法。对齐 <c>IRecovery.CancelRecovery</c> 语义。</remarks>
    public virtual void CancelRecovery()
    {
    }

    // ════════════════════════════════════════════════════════════
    //  RecoverAsync 固定模板（非 virtual——子类不可 override，只能通过钩子表达差异）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 固定编排：CAS 闸门 → <see cref="WaitForDependenciesAsync"/>（层间 join）→
    ///   <see cref="OnRecoveryStart"/> → <see cref="OnRecoveryCoreAsync"/>（★ 唯一必 override）→
    ///   <see cref="OnRecoveryComplete"/> → <see cref="MarkReady"/>；异常置 <see cref="RecoveryPhase.Failed"/>。
    /// <para>★ 子类<b>不</b> override 本方法——恢复算法差异通过 override <see cref="OnRecoveryCoreAsync"/> 表达，
    ///   层间依赖（等子引擎就绪）通过 override <see cref="WaitForDependenciesAsync"/> 表达。</para>
    /// <para>★ 并发语义：已 Ready → 幂等 no-op 返回；Recovering 中 → 抛 <see cref="InvalidOperationException"/>
    ///   （恢复不可重入）；Unrecovered → 进入恢复。</para>
    /// <para>★ 失败语义（含取消）：异常 → 置 <see cref="RecoveryPhase.Failed"/>（存 <see cref="RecoveryState"/> 的
    ///   <c>Error</c>，可观测）+ 闸门回 Unrecovered（允许重试）+ 重抛。取消当前并入 Failed——对齐现有各 IRecovery 实现；
    ///   子类若需"取消≠失败"的细分，可在 <see cref="OnRecoveryCoreAsync"/> 内自行 catch
    ///   <see cref="OperationCanceledException"/> 处理。</para>
    /// </summary>
    public async ValueTask RecoverAsync(THints hints, CancellationToken ct = default)
    {
        if (!TryEnterRecovering()) return; // 已 Ready——幂等 no-op
        try
        {
            await WaitForDependenciesAsync(ct).ConfigureAwait(false); // 层间 join（默认空）
            OnRecoveryStart(); // [Start] 钩子（默认上报 Recovering/0%）
            await OnRecoveryCoreAsync(hints, ct).ConfigureAwait(false); // ★ [Core] 子类唯一必 override
            OnRecoveryComplete(); // [Complete] 钩子（默认上报 Completed/100%）
            MarkReady(); // → IsReady=true
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _flag, FlagUnrecovered); // 回退闸门——允许重试
            _state = _state with { Phase = RecoveryPhase.Failed, Error = ex }; // 存异常，RecoveryState.Error 可观测
            throw;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  子类钩子（virtual / abstract）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 层间依赖 join——<see cref="RecoverAsync"/> 在 <see cref="OnRecoveryStart"/> <b>之前</b>调。
    /// <para>默认 <c>CompletedTask</c>（无依赖，如 Blob/Block）。需要"子引擎先就绪"的持有者 override：在此
    ///   <c>await owner._engine.WaitForReadyAsync(ct)</c>（Log/Index/Ring/Metadata 模式）。</para>
    /// <para>★ 这是 fire-and-forget Initialize 模型下层间数据依赖的合法表达点：父层恢复核心读子层恢复产物
    ///   （如引擎 SectorSize / 段表），故须在 Core 前 join 子层。见 <see cref="LifecycleBase{THints}"/> 的
    ///   "Complete/Ready/恢复完成 三者同义"不变量——join 的正是这个语义。</para>
    /// </summary>
    protected virtual ValueTask WaitForDependenciesAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>
    /// ★ 恢复核心——子类<b>唯一必须 override</b> 的钩子：真正的恢复算法（扫盘 / 读 meta / 回放 / 重建索引 / 装配策略）。
    /// <para>在 <see cref="OnRecoveryStart"/> 之后、<see cref="OnRecoveryComplete"/> 之前执行。进度上报用
    ///   <see cref="RaiseProgress(int, string?)"/>（推进 phase/percent/detail）；取消检查用 <paramref name="ct"/>（在 IO checkpoint 处
    ///   <c>ct.ThrowIfCancellationRequested()</c>）。</para>
    /// <para>★ 四级回退（hints → meta → 引擎水位 → 扫盘）等恢复策略差异，全部封装在此 override 内——
    ///   模板不关心具体算法，只保证 Start/Core/Complete 的串行编排。</para>
    /// </summary>
    protected abstract ValueTask OnRecoveryCoreAsync(THints hints, CancellationToken ct);

    /// <summary>
    /// ★ 恢复开始钩子——默认上报 <see cref="RecoveryPhase.Recovering"/>/0%。由模板保证在 Core 前调用。
    /// <para>子类可 override 改 detail 文案（如 "engine recovery start"/"index recovery start"），
    ///   一般不需要改起始 phase（统一 Recovering 便于上层进度条聚合）。</para>
    /// </summary>
    /// <remarks>当前 <c>public virtual</c> 以满足 <see cref="IRecovery"/> 接口契约——待接口瘦身
    ///   （移除 OnRecoveryStart/OnRecoveryComplete）后收为 <c>protected virtual</c>。外部代码<b>不应</b>直接调本方法，
    ///   恢复编排只应通过 <see cref="RecoverAsync"/> 驱动。</remarks>
    public virtual void OnRecoveryStart()
        => RaiseProgress(RecoveryPhase.Recovering, 0, "recovery start");

    /// <summary>
    /// ★ 恢复完成钩子——默认上报 <see cref="RecoveryPhase.Completed"/>/100%。由模板保证在 Core 成功后、
    /// <see cref="MarkReady"/> 前调用。
    /// </summary>
    /// <remarks>同 <see cref="OnRecoveryStart"/>：当前 <c>public virtual</c> 满足接口，待瘦身收为
    ///   <c>protected virtual</c>。外部不应直接调。</remarks>
    public virtual void OnRecoveryComplete()
        => RaiseProgress(RecoveryPhase.Completed, 100, "recovery complete");

    // ════════════════════════════════════════════════════════════
    //  进度上报（子类在 OnRecoveryCoreAsync 内用）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 上报进度——原子更新 <see cref="RecoveryState"/>（phase/percent/detail）并触发
    /// <see cref="RecoveryProgressChanged"/> 事件。
    /// </summary>
    /// <param name="percent">进度百分比（0-100）。</param>
    /// <param name="detail">进度详情描述。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void RaiseProgress(int percent, string? detail = null)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), "percent must be in [0,100]");
        RaiseProgress(RecoveryPhase.Recovering, percent, detail);
    }

    /// <summary>
    /// ★ 上报进度——原子更新 <see cref="RecoveryState"/>（phase/percent/detail）并触发
    /// <see cref="RecoveryProgressChanged"/> 事件。
    /// <para>子类在 <see cref="OnRecoveryCoreAsync"/> 内调，推进 percent + detail（恢复全程处于
    ///   <see cref="RecoveryPhase.Recovering"/>，细分步骤由 detail 文案表达，如 "scanning tail"/"meta tail=..."）。
    ///   模板的 Start/Complete 默认实现也走本方法。</para>
    /// </summary>
    /// <param name="phase">恢复阶段。</param>
    /// <param name="percent">进度百分比（0-100）。</param>
    /// <param name="detail">进度详情描述。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RaiseProgress(RecoveryPhase phase, int percent, string? detail = null)
    {
        _state = _state with { Phase = phase, Percent = percent, Detail = detail };
        RecoveryProgressChanged?.Invoke(new RecoveryProgress { Phase = phase, Percent = percent, Detail = detail });
    }

    // ════════════════════════════════════════════════════════════
    //  内部
    // ════════════════════════════════════════════════════════════

    /// <summary>CAS 闸门：Unrecovered→Recovering。已 Ready→no-op return false；Recovering→抛"不可重入"。</summary>
    private bool TryEnterRecovering()
    {
        var prior = Interlocked.CompareExchange(ref _flag, FlagRecovering, FlagUnrecovered);
        return prior switch
        {
            FlagReady => false,
            FlagRecovering => throw new InvalidOperationException(
                $"{GetType().Name} recovery already in progress（恢复不可重入）"),
            _ => true
        };
    }
}