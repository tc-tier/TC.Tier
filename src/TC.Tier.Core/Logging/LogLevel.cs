namespace TC.Tier.Core.Logging;

/// <summary>
/// 日志级别枚举。
/// </summary>
public enum LogLevel
{
    /// <summary>追踪 —— 最详细级别（0）：内部流程细节，仅深度诊断。</summary>
    Trace = 0,
    /// <summary>调试（1）：开发/排障用信息。</summary>
    Debug = 1,
    /// <summary>信息（2）：常规运行信息。</summary>
    Information = 2,
    /// <summary>警告（3）：非致命异常状况，可继续运行。</summary>
    Warning = 3,
    /// <summary>错误（4）：操作失败，需关注。</summary>
    Error = 4,
    /// <summary>严重（5）：系统级故障，最高业务级别。</summary>
    Critical = 5,
    /// <summary>关闭（6）：所有级别均不输出。</summary>
    None = 6,
}