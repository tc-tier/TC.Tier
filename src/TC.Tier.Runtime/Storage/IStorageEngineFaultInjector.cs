namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 引擎故障注入器（故障注入面补全设计 件二——引擎缝常设对抗面，非测试代码泄漏）。
/// <para>★ 哲学对齐 spec-12 §9.2 传输面：注入脸只建在真实失败边界上。引擎缝 = <b>契约级</b>失败
/// （引擎对上层的合法异常/等待语义）与状态窗；盘的故障类（失败/腐败/慢/乱序）归 fs 脸
/// （Core.IO <c>FaultInjectingFileSystem</c>），数据伪造（错值/短读）不做——数据破坏是盘的故障类。</para>
/// <para>★ 挂载：<see cref="StorageEngineOptions.WithFaults"/> 显式开启（缺省关闭零开销——引擎各公开
///   op 入口一次空引用短路），经引擎 <c>Faults</c> 属性取得本面。</para>
/// </summary>
public interface IStorageEngineFaultInjector
{
    /// <summary>op 级失败——命中 <paramref name="opPattern"/> 的操作入口即按 <paramref name="error"/>
    /// 抛 <see cref="FileIOException"/>（null = <see cref="IOError.IOFailure"/>）；语义码原样透传不丢失。</summary>
    /// <param name="opPattern">op 名或 "*"（op 全集：Allocate / Write / WriteAsync / Append / AppendAsync /
    ///   Read / ReadAsync / Flush / Reclaim / ReclaimHead / ReclaimTail / StartReclaim / StartCompact /
    ///   StartRangeCompact / OpenSequentialReader）。</param>
    /// <param name="error">注入的类型化错误码（null = <see cref="IOError.IOFailure"/>）。</param>
    /// <param name="probability">注入概率 [0,1]（默认 1 = 恒注入；与 <paramref name="atCallIndex"/> 互斥——都设时确定性优先）。</param>
    /// <param name="atCallIndex">确定性注入：第 N 次（1 起）匹配调用注入一次（null = 概率模式）。</param>
    void FailOn(string opPattern, IOError? error = null, double probability = 1, long? atCallIndex = null);

    /// <summary>op 级慢——命中操作前置延迟（上层 watchdog / 节流降档 / 有界等待语义的确定性触发器）。</summary>
    /// <param name="opPattern">op 名或 "*"（全集同 <see cref="FailOn"/>）。</param>
    /// <param name="delay">注入的前置延迟时长。</param>
    /// <param name="probability">注入概率 [0,1]（默认 1 = 恒注入）。</param>
    void DelayOn(string opPattern, TimeSpan delay, double probability = 1);

    /// <summary>op 级挂——命中操作永不到达业务体（上层 watchdog / 取消路径的确定性触发器）。
    /// <para>★ 释放仅供拆除：<see cref="Reset"/> 置位挂起点后操作才继续——正常故障语义下挂点永不放行，
    ///   被测方必须以自身超时/取消路径先行处置。</para></summary>
    /// <param name="opPattern">op 名或 "*"（全集同 <see cref="FailOn"/>）。</param>
    void HangOn(string opPattern);

    /// <summary>进入引擎状态窗（确定性进入，<see cref="Reset"/> 退出；多窗可并存）。</summary>
    /// <param name="state">目标状态窗。</param>
    void EnterState(EngineFaultState state);

    /// <summary>清除全部注入（规则全清 + 挂起点放行 + 状态窗全退——Compact 活动窗的排他占住同此释放）。</summary>
    void Reset();
}

/// <summary>引擎状态窗（引擎缝状态注入——确定性进入，Reset 退出）。</summary>
public enum EngineFaultState
{
    /// <summary>恢复窗——恢复核心延后启动（<see cref="ILifecycle{THints}.WaitForReady()"/> 与上层超时容忍
    /// 路径的确定性验证）；存续期间引擎 op 入口按"尚未完成恢复"拒绝（对齐 EnsureReady 异常语义）。</summary>
    RecoveringWindow,

    /// <summary>Compact 活动窗——目标区间排他占住（CompactLease 持至数据尾段段末；写者让位/自旋路径的
    /// 确定性验证，占区间协议同真实 Compact）。</summary>
    CompactActive,

    /// <summary>CPU 节流饱和窗——节流系数强制饱和（<c>EnsureCpuCapacity</c> 拒绝/自旋等待路径的确定性验证）。</summary>
    ThrottleSaturated,
}
