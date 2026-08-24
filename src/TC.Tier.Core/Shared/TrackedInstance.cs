namespace TC.Tier.Core.Shared;

/// <summary>
/// 跟踪实例信息（对齐 <c>LeaseInfo</c>）——诊断/泄漏报告用。
/// </summary>
public sealed class TrackedInstanceInfo
{
    /// <summary>实例唯一标识（注册时生成）。</summary>
    public Guid Id { get; init; }

    /// <summary>创建时间戳（TickCount64, ms）。</summary>
    public long CreatedTimestampMs { get; init; }

    /// <summary>类型名（诊断定位用）。</summary>
    public string TypeName { get; init; } = "";

    /// <summary>当前状态描述（可选，如 "NotInitialized"/"Recovering"/"Ready"；无则 null）。</summary>
    public string? State { get; init; }
}