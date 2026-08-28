namespace TC.Tier.Contracts.Lifecycle;

/// <summary>
/// ★ 恢复阶段（持有者中立——所有 <c>LifecycleBase&lt;THints&gt;</c> 派生类共享同一套通用恢复状态语义）。
/// <para>严格顺序推进，不可回退：<see cref="NotStarted"/> → <see cref="Recovering"/> →
///   <see cref="Completed"/>（成功）/ <see cref="Failed"/>（失败）。</para>
/// <para>★ 恢复进行中的细分步骤（读 meta/配置、扫数据源定位边界、应用恢复结果等）<b>不</b>另设阶段枚举——
///   一律处于 <see cref="Recovering"/>，由 <see cref="RecoveryProgress"/> 的 <c>Percent</c>(0-100) + <c>Detail</c>
///   表达粒度。这样枚举对任何持有者（IO 引擎 / Log / Ring / Index / Metadata / Blob）都通用，
///   无需按域分别解释阶段语义。</para>
/// <para>★ 生产代码<b>不</b>按 <see cref="Recovering"/> 内部的细分做分支判断——细分仅供进度展示；
///   控制流只认终态（<see cref="Completed"/>/<see cref="Failed"/>/<see cref="NotStarted"/>）。</para>
/// </summary>
public enum RecoveryPhase
{
    /// <summary>未开始（构造后初始状态）。</summary>
    NotStarted,

    /// <summary>★ 恢复中（后台 task 正在跑：读 meta/配置、扫数据源、应用恢复结果等，全归此态）。
    /// <para>细分粒度（在哪一步、进度多少）由 <see cref="RecoveryProgress"/> 的 <c>Percent</c> + <c>Detail</c> 表达，
    ///   不再枚举化。持有者在 <c>RaiseProgress</c> 时上报有意义的 detail 文案 + 推进的 percent 即可。</para></summary>
    Recovering,

    /// <summary>恢复完成，持有者可投入使用（<c>IsReady=true</c>）。</summary>
    Completed,

    /// <summary>恢复失败（meta 校验失败、锚点无效等致命错误，非首次启动的空库）。
    /// 异常存 <see cref="RecoveryState"/> 的 <c>Error</c> 字段可观测。</summary>
    Failed
}
