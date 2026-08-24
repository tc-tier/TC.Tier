using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Epochs;

/// <summary>
/// 时代保护版本方案 (EPVS) 是允许对共享资源的安全并发访问的版本化方案。它使用底层的epoch框架来管理保护，并确保在保护线程时不会发生版本转换。EPVS维护可以被执行以在版本之间转换的状态机，并且它提供用于进入和离开保护、刷新保护以及执行具有临界区的状态机的方法。
/// </summary>
public class EpochProtectedVersionScheme
{
    private readonly LightEpoch _epoch;
    private VersionSchemeState _state;
    private VersionSchemeStateMachine? _currentMachine;

    /// <summary>
    /// 构造一个由给定 epoch 框架支撑的新 EPVS。多个 EPVS 实例可共享同一个底层 epoch 框架
    /// （⚠️：暂不支持重入，因此对这些共享实例的嵌套保护很可能出错）。
    /// </summary>
    /// <param name="epoch">底层 epoch 保护框架。</param>
    public EpochProtectedVersionScheme(LightEpoch epoch)
    {
        _epoch = epoch;
        _state = VersionSchemeState.Make(VersionSchemeState.Rest, 1);
        _currentMachine = null;
    }

    /// <summary></summary>
    /// <returns>当前状态。</returns>
    public VersionSchemeState CurrentState() => _state;

    // 原子地从 expectedState 过渡到 nextState
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MakeTransition(VersionSchemeState expectedState, VersionSchemeState nextState)
    {
        if (Interlocked.CompareExchange(ref _state.Word, nextState.Word, expectedState.Word) != expectedState.Word)
            return false;
        Debug.WriteLine("Moved to {0}, {1}", nextState.Phase, nextState.Version);
        return true;
    }

    /// <summary>
    /// 在当前线程进入保护。保护期间不会发生版本过渡。为使系统能推进，
    /// 保护必须稍后用 Leave() 或 Refresh() 在同一线程上释放。
    /// </summary>
    /// <returns>进入保护那一刻的 EPVS 状态——该状态的有效期持续到保护结束。</returns>
    public VersionSchemeState Enter()
    {
        _epoch.Resume();
        TryStepStateMachine();

        VersionSchemeState result;
        while (true)
        {
            result = _state;
            if (!result.IsIntermediate()) break;
            _epoch.Suspend();
            Thread.Yield();
            _epoch.Resume();
        }

        return result;
    }

    /// <summary>
    /// 刷新保护——等价于「先释放再立即重新获取保护」，但性能更好。
    /// </summary>
    /// <returns>刷新后进入保护那一刻的 EPVS 状态——有效期持续到保护结束。</returns>
    public VersionSchemeState Refresh()
    {
        _epoch.ProtectAndDrain();
        VersionSchemeState result;
        TryStepStateMachine();

        while (true)
        {
            result = _state;
            if (!result.IsIntermediate()) break;
            _epoch.Suspend();
            Thread.Yield();
            _epoch.Resume();
        }
        return result;
    }

    /// <summary>
    /// 释放当前线程的保护。
    /// </summary>
    public void Leave()
    {
        _epoch.Suspend();
    }

    internal void TryStepStateMachine(VersionSchemeStateMachine? expectedMachine = null)
    {
        var machineLocal = _currentMachine;
        var oldState = _state;

        // 无状态机可推进
        if (machineLocal == null) return;

        // 应退出，避免无限递归推进（直到栈溢出）
        if (expectedMachine != null && machineLocal != expectedMachine) return;

        // 还在计算实际目标版本
        if (machineLocal.ActualToVersion == -1) return;

        // 状态机已完成但尚未重置。应重置并避免再启动一轮
        if (oldState.Phase == VersionSchemeState.Rest && oldState.Version == machineLocal.ActualToVersion)
        {
            Interlocked.CompareExchange(ref _currentMachine, null, machineLocal);
            return;
        }

        // 正在过渡中，或当前无可用步骤
        if (oldState.IsIntermediate() || !machineLocal.GetNextStep(oldState, out var nextState)) return;

        var intermediate = VersionSchemeState.MakeIntermediate(oldState);
        if (!MakeTransition(oldState, intermediate)) return;

        // 延迟到独立函数执行，避免预先分配内存
        StepMachineHeavy(machineLocal, oldState, nextState);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StepMachineHeavy(VersionSchemeStateMachine machineLocal, VersionSchemeState old, VersionSchemeState next)
    {
        // 恢复 epoch 以确保状态机能推进（本线程可能是唯一活跃线程）。
        // 且 StepMachineHeavy 会调 BumpCurrentEpoch，后者要求线程已受保护。
        bool isProtected = _epoch.ThisInstanceProtected();
        if (!isProtected)
            _epoch.Resume();
        try
        {
            _epoch.BumpCurrentEpoch(() =>
            {
                machineLocal.OnEnteringState(old, next);
                var success = MakeTransition(VersionSchemeState.MakeIntermediate(old), next);
                machineLocal.AfterEnteringState(next);
                Debug.Assert(success);

                // 本 action 若由某个 BumpCurrentEpoch 的 ProtectAndDrain 内联触发（当前线程仍在
                // bump 回调栈内），不能在此推进下一步——那会形成嵌套 bump（守卫禁止，有 epoch
                // 自死锁风险）。此情形由下方「bump 返回后」的检查接力推进。
                // 若由 SuspendDrain/Resume 等非 bump 路径触发（当前线程不在任何 bump 栈内），
                // 可安全推进下一步，保证状态机不依赖外部驱动也能走完（FASTER 语义）。
                if (!_epoch.IsInsideBump())
                    TryStepStateMachine(machineLocal);
            });

            // 上一步过渡已在本线程完成（action 内联触发）——继续推进下一步。
            // 必须在 bump 之外推进：此时不构成嵌套 bump。
            if (VersionSchemeState.Equal(_state, next))
                TryStepStateMachine(machineLocal);
        }
        finally
        {
            if (!isProtected)
                _epoch.Suspend();
        }
    }

    /// <summary>
    /// 通知 EPVS 状态机有新步骤可用。当状态机延迟某步骤（例如等待 IO 完成）后，
    /// 在步骤就绪时调用本方法，使状态机即使没有活跃线程进入/离开系统也能推进。
    /// 若步骤始终可用，则无需调用本方法。
    /// </summary>
    public void SignalStepAvailable()
    {
        TryStepStateMachine();
    }

    /// <summary>
    /// 尝试启动执行给定的状态机。
    /// </summary>
    /// <param name="stateMachine">要执行的状态机。</param>
    /// <returns>
    /// 状态机是否成功启动（OK）、因存在活跃状态机而无法启动（RETRY），
    /// 或因版本已超过指定目标版本而无法启动（FAIL）。
    /// </returns>
    public StateMachineExecutionStatus TryExecuteStateMachine(VersionSchemeStateMachine stateMachine)
    {
        if (stateMachine.ToVersion() != -1 && stateMachine.ToVersion() <= _state.Version) return StateMachineExecutionStatus.FAIL;
        var actualStateMachine = Interlocked.CompareExchange(ref _currentMachine, stateMachine, null);
        if (actualStateMachine == null)
        {
            // 计算状态机的实际目标版本
            stateMachine.ActualToVersion =
                stateMachine.ToVersion() == -1 ? _state.Version + 1 : stateMachine.ToVersion();
            // 触发一次初始步骤以启动流程
            TryStepStateMachine(stateMachine);
            return StateMachineExecutionStatus.OK;
        }

        // 否则需检查：是否为重复推进同一版本的尝试
        if (stateMachine.ToVersion() != -1 && actualStateMachine.ActualToVersion >= stateMachine.ToVersion())
            return StateMachineExecutionStatus.FAIL;

        return StateMachineExecutionStatus.RETRY;
    }


    /// <summary>
    /// 启动执行给定的状态机。
    /// </summary>
    /// <param name="stateMachine">要启动的状态机。</param>
    /// <param name="spin">是否自旋等待直到版本过渡完成。</param>
    /// <returns>状态机是否可执行。若为 false，表示 EPVS 已把版本推进到超过指定的目标版本。</returns>
    public bool ExecuteStateMachine(VersionSchemeStateMachine stateMachine, bool spin = false)
    {
        if (_epoch.ThisInstanceProtected())
            throw new InvalidOperationException("unsafe to execute a state machine blockingly when under protection");
        StateMachineExecutionStatus status;
        do
        {
            status = TryExecuteStateMachine(stateMachine);
        } while (status == StateMachineExecutionStatus.RETRY);

        if (status != StateMachineExecutionStatus.OK) return false;

        if (spin)
        {
            while (_state.Version != stateMachine.ActualToVersion || _state.Phase != VersionSchemeState.Rest)
            {
                TryStepStateMachine();
                Thread.Yield();
            }
        }

        return true;
    }

    /// <summary>
    /// 用单个临界区把版本推进到请求的版本。
    /// </summary>
    /// <param name="criticalSection">要执行的临界区，参数为旧版本号与新（目标）版本号。</param>
    /// <param name="targetVersion">要过渡到的目标版本；-1 表示无条件过渡到未指定的下一版本。</param>
    /// <returns>
    /// 状态机是否成功启动（OK）、因存在活跃状态机而无法启动（RETRY），
    /// 或因版本已超过指定目标版本而无法启动（FAIL）。
    /// </returns>
    public StateMachineExecutionStatus TryAdvanceVersionWithCriticalSection(Action<long, long> criticalSection, long targetVersion = -1)
    {
        return TryExecuteStateMachine(new SimpleVersionSchemeStateMachine(criticalSection, targetVersion));
    }

    /// <summary>
    /// 用单个临界区把版本推进到请求的版本。
    /// </summary>
    /// <param name="criticalSection">要执行的临界区，参数为旧版本号与新（目标）版本号。</param>
    /// <param name="targetVersion">要过渡到的目标版本；-1 表示无条件过渡到未指定的下一版本。</param>
    /// <param name="spin">是否自旋等待直到版本过渡完成。</param>
    /// <returns>状态机是否可执行。若为 false，表示 EPVS 已把版本推进到超过指定的目标版本。</returns>
    public bool AdvanceVersionWithCriticalSection(Action<long, long> criticalSection, long targetVersion = -1, bool spin = false)
    {
        return ExecuteStateMachine(new SimpleVersionSchemeStateMachine(criticalSection, targetVersion), spin);
    }

}