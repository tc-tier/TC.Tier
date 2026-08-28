namespace TC.Tier.Core.Epochs;

/// <summary>
/// 版本状态机——指定推进到新版本的一系列过渡步骤。
/// </summary>
public abstract class VersionSchemeStateMachine
{
    private readonly long _toVersion;
    /// <summary>
    /// 本状态机实际要推进到的目标版本；-1 表示尚未确定。
    /// </summary>
    protected internal long ActualToVersion { get; set; }

    /// <summary>
    /// 为过渡到给定版本构造一个新版本状态机。
    /// </summary>
    /// <param name="toVersion">要过渡到的目标版本；-1 表示无条件过渡到未指定的下一版本。</param>
    protected VersionSchemeStateMachine(long toVersion = -1)
    {
        _toVersion = toVersion;
        ActualToVersion = toVersion;
    }

    /// <summary>
    /// 获取要过渡到的目标版本。
    /// </summary>
    /// <returns>要过渡到的目标版本；-1 表示无条件过渡到未指定的下一版本。</returns>
    public long ToVersion() => _toVersion;

    /// <summary>
    /// 给定当前状态，计算版本方案应进入的下一状态（若有）。
    /// </summary>
    /// <param name="currentState">当前状态。</param>
    /// <param name="nextState">下一状态（若有）。</param>
    /// <returns>当前时刻是否可进行状态过渡。</returns>
    public abstract bool GetNextStep(VersionSchemeState currentState, out VersionSchemeState nextState);

    /// <summary>
    /// 进入某状态前执行的代码块。保证在与其它过渡或 EPVS 保护区互斥的临界区内执行。
    /// </summary>
    /// <param name="fromState">当前状态。</param>
    /// <param name="toState">即将进入的状态。</param>
    public abstract void OnEnteringState(VersionSchemeState fromState, VersionSchemeState toState);

    /// <summary>
    /// 进入状态后执行的代码块。此处的执行可能与其它 EPVS 保护区交错——可用来协作式地完成重量级过渡工作，而不阻塞其它线程的推进。
    /// </summary>
    /// <param name="state">当前状态。</param>
    public abstract void AfterEnteringState(VersionSchemeState state);
}