namespace TC.Tier.Core.Shared;

/// <summary>
/// 资源组内单个资源的信息（对齐 <c>LeaseInfo</c>）——ResourceGroup.GetResources 返回。
/// </summary>
public sealed class ResourceInfo
{
    /// <summary>注册名（Add 时指定或自动 Type.Name）。</summary>
    public string Name { get; init; } = "";

    /// <summary>资源类型名。</summary>
    public string TypeName { get; init; } = "";

    /// <summary>添加时间戳（TickCount64, ms）。</summary>
    public long AddedTimestampMs { get; init; }

    /// <summary>所有权模式（Owned=组释放 / Referenced=外部管只跟踪）。</summary>
    public ResourceOwnership Ownership { get; init; }

    /// <summary>注册调用栈（Debug 配置捕获，Release 为 null）。泄漏定位用。</summary>
    public string? DebugStack { get; init; }
}