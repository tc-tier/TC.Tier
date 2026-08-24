// ReSharper disable InconsistentNaming
namespace TC.Tier.Core.Epochs;

/// <summary>
/// 状态机执行结果。
/// </summary>
public enum StateMachineExecutionStatus
{
    /// <summary>
    /// 执行成功。
    /// </summary>
    OK,
    /// <summary>
    /// 执行未成功，但可重试（如已有活跃状态机）。
    /// </summary>
    RETRY,
    /// <summary>
    /// 执行失败，不应重试（如目标版本已被超过）。
    /// </summary>
    FAIL
}