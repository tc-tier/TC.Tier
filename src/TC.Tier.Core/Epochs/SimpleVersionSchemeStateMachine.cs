using System.Diagnostics;

namespace TC.Tier.Core.Epochs;

internal class SimpleVersionSchemeStateMachine(Action<long, long> criticalSection, long toVersion = -1)
    : VersionSchemeStateMachine(toVersion)
{
    /// <summary>计算下一状态：恒单步 Rest → Rest，目标版本 = 构造指定的 ToVersion()，未指定（-1）则当前版本 +1。</summary>
    /// <param name="currentState">当前状态（Phase 须为 Rest，DEBUG 断言）。</param>
    /// <param name="nextState">输出下一状态（Rest 相，版本号见上）。</param>
    /// <returns>恒返回 true——单步即可完成过渡（版本推进动作在 <see cref="OnEnteringState"/> 执行）。</returns>
    public override bool GetNextStep(VersionSchemeState currentState, out VersionSchemeState nextState)
    {
        Debug.Assert(currentState.Phase == VersionSchemeState.Rest);
        nextState = VersionSchemeState.Make(VersionSchemeState.Rest, ToVersion() == -1 ? currentState.Version + 1 : ToVersion());
        return true;
    }

    /// <summary>进入 Rest → Rest 转换时执行注入的临界区委托（实参 = 起止版本号）。</summary>
    /// <param name="fromState">转换前状态（Rest 相）。</param>
    /// <param name="toState">转换后状态（Rest 相）。</param>
    public override void OnEnteringState(VersionSchemeState fromState, VersionSchemeState toState)
    {
        Debug.Assert(fromState.Phase == VersionSchemeState.Rest && toState.Phase == VersionSchemeState.Rest);
        criticalSection(fromState.Version, toState.Version);
    }

    /// <summary>无操作——Simple 方案无进入后处理。</summary>
    /// <param name="state">已进入的状态。</param>
    public override void AfterEnteringState(VersionSchemeState state) { }
}